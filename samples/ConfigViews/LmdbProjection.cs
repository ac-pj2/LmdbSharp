// LmdbProjection: the durable read model — Phase 2's projector.
//
// Every projected EntityRecord is persisted in LMDB (raw env, key = entity
// key, value = MemoryPack bytes) alongside a sync watermark. On startup the
// projection loads from disk in microseconds and then RECONCILES: one
// incremental PostgreSQL query for anything created/updated/soft-deleted
// after the watermark — so mutations that happened while this instance was
// down are caught up without a bulk reload, and restarts cost ~nothing.
//
// The projection stays disposable by design: delete the file (or pass
// RebuildProjection=true) and it rebuilds from PostgreSQL. LMDB never owns
// truth for entity data — it owns render-ready projections of it.
using System.Diagnostics;
using Lmdb;
using MemoryPack;
using Npgsql;

namespace ConfigViews;

public sealed class LmdbProjection : IRecordProjection, IDisposable
{
    private static readonly byte[] WatermarkKey = "meta:watermark"u8.ToArray();

    private readonly LmdbEnvironment _env;
    private readonly P2Options _opt;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, EntityRecord> _hot = new();
    private readonly object _writeGate = new();

    // Stats for the DevPanel.
    public int CaughtUp { get; private set; }
    public long StartupMicros { get; private set; }
    public DateTime Watermark { get; private set; }

    public LmdbProjection(P2Options opt, string path, bool rebuild)
    {
        _opt = opt;
        if (rebuild && File.Exists(path)) File.Delete(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _env = LmdbEnvironment.Open(path, new EnvOpenOptions
        { ReadOnly = false, MapSize = 1L << 30, MaxDbs = 4 });

        long t0 = Stopwatch.GetTimestamp();
        LoadFromDisk();
        StartupMicros = (Stopwatch.GetTimestamp() - t0) * 1_000_000 / Stopwatch.Frequency;
        try
        {
            Reconcile();
        }
        catch (Exception e)
        {
            // PostgreSQL unavailable (e.g. saturated shared dev instance): the
            // durable projection keeps serving reads from disk; the mutation
            // bridge re-runs a catch-up whenever it (re)connects.
            CaughtUp = -1;
            Console.WriteLine($"[projection] reconcile deferred ({e.Message.Split('\n')[0]}) — serving disk state");
        }
    }

    // ── IRecordProjection ──

    public List<EntityRecord> Snapshot() => _hot.Values.ToList();

    public void Upsert(EntityRecord rec)
    {
        _hot[rec.Key] = rec;
        lock (_writeGate)
        {
            using var txn = _env.BeginTransaction(false);
            var db = txn.OpenDatabase("projection", DatabaseFlags.Create);
            txn.Put(db, KeyBytes(rec.Key), MemoryPackSerializer.Serialize(rec));
            // We're online and just heard this from the live stream — current as of now.
            Watermark = DateTime.UtcNow;
            txn.Put(db, WatermarkKey, BitConverter.GetBytes(Watermark.Ticks));
            txn.Commit();
        }
    }

    public void Remove(string key)
    {
        _hot.TryRemove(key, out _);
        lock (_writeGate)
        {
            using var txn = _env.BeginTransaction(false);
            var db = txn.OpenDatabase("projection", DatabaseFlags.Create);
            txn.Delete(db, KeyBytes(key));
            txn.Commit();
        }
    }

    public void Fill(IEnumerable<EntityRecord> records)
    {
        lock (_writeGate)
        {
            using var txn = _env.BeginTransaction(false);
            var db = txn.OpenDatabase("projection", DatabaseFlags.Create);
            foreach (var rec in records)
            {
                _hot[rec.Key] = rec;
                txn.Put(db, KeyBytes(rec.Key), MemoryPackSerializer.Serialize(rec));
            }
            // A fill makes us current as of now.
            Watermark = DateTime.UtcNow;
            txn.Put(db, WatermarkKey, BitConverter.GetBytes(Watermark.Ticks));
            txn.Commit();
        }
    }

    public string Describe()
        => $"lmdb · {_hot.Count} records · loaded in {StartupMicros}µs · " +
           (CaughtUp < 0 ? "reconcile pending (PG unavailable)" : $"caught up {CaughtUp}");

    // ── startup: disk load + incremental reconcile ──

    private void LoadFromDisk()
    {
        using var read = _env.BeginTransaction(readOnly: true);
        LmdbDatabase rdb;
        try { rdb = read.OpenDatabase("projection"); }
        catch (LmdbException) { return; } // fresh environment — nothing projected yet
        using var cur = read.CreateCursor(rdb);
        if (!cur.TryGet(CursorOp.First, default, out var k, out var v)) return;
        do
        {
            if (k.SequenceEqual(WatermarkKey))
            {
                Watermark = new DateTime(BitConverter.ToInt64(v), DateTimeKind.Utc);
                continue;
            }
            // Corrupt-entry guard: a projection is disposable, never load-bearing.
            // Skip anything that doesn't decode to a keyed record.
            try
            {
                var rec = MemoryPackSerializer.Deserialize<EntityRecord>(v);
                if (rec is { Key.Length: > 0 }) _hot[rec.Key] = rec;
            }
            catch (Exception)
            {
                // torn/foreign bytes — ignored; the reconcile refreshes from PG
            }
        } while (cur.TryGet(CursorOp.Next, default, out k, out v));
    }

    /// <summary>Catch up on anything that changed in PostgreSQL while this
    /// instance was down. Fast path: nothing changed → the disk load IS the
    /// state, zero bulk reads. Anything changed (incl. soft-deletes) → refill
    /// from the store (idempotent; deletes removed explicitly).</summary>
    private void Reconcile()
    {
        // Small overlap window absorbs clock skew; refills are idempotent.
        var since = Watermark == default ? DateTime.MinValue : Watermark.AddSeconds(-5);

        using var conn = new NpgsqlConnection(_opt.ConnectionString);
        conn.Open();

        int changed = 0;
        var deletedKeys = new List<string>();
        using (var cmd = new NpgsqlCommand("""
            SELECT "Id"::text, "IsDeleted"
            FROM "Entities"
            WHERE "SystemSlug" = @sys AND "EntityTypeSlug" = ANY(@types)
              AND GREATEST("CreatedAt", COALESCE("UpdatedAt", "CreatedAt")) > @since
            UNION ALL
            SELECT c."Id"::text, c."IsDeleted"
            FROM "Comments" c JOIN "Entities" e ON e."Id" = c."EntityId"
            WHERE e."SystemSlug" = @sys AND e."EntityTypeSlug" = ANY(@types)
              AND c."CreatedAt" > @since
            """, conn))
        {
            cmd.Parameters.AddWithValue("sys", _opt.SystemSlug);
            cmd.Parameters.AddWithValue("types", _opt.EntityTypes);
            cmd.Parameters.AddWithValue("since", since);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                changed++;
                if (reader.GetBoolean(1)) deletedKeys.Add(reader.GetString(0));
            }
        }

        CaughtUp = changed;
        if (changed == 0 && Watermark != default) return; // fast path: disk state is current

        Fill(new P2EntityStore(_opt).LoadAll());
        foreach (var key in deletedKeys) Remove(key);
    }

    private static byte[] KeyBytes(string key) => System.Text.Encoding.UTF8.GetBytes("rec:" + key);

    public void Dispose() => _env.Dispose();
}
