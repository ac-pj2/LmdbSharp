using System.Buffers.Binary;
using System.Text;
using Lmdb;
using Xunit;

namespace Lmdb.Tests;

/// <summary>
/// Bulk DUPFIXED retrieval (GET_MULTIPLE / NEXT_MULTIPLE / FIRST_MULTIPLE /
/// LAST_MULTIPLE): each call returns a packed span of fixed-size duplicate
/// values. Covers all three storages — plain single-value node, inline LEAF2
/// sub-page (small dup sets), and sub-DB dup trees (large dup sets) — and
/// asserts full equivalence with one-at-a-time NextDup iteration.
/// </summary>
public class DupFixedMultipleTests
{
    private const int ValSize = 8;

    private static string TmpDir(string name)
    {
        string dir = $"/tmp/lmdb-cs/{name}";
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] V(long i)
    {
        var b = new byte[ValSize];
        BinaryPrimitives.WriteInt64BigEndian(b, i);   // big-endian: memcmp order == numeric
        return b;
    }

    private static LmdbEnvironment DupFixedEnv(string dir) => LmdbEnvironment.Open(dir,
        new EnvOpenOptions
        {
            ReadOnly = false,
            MapSize = 256L << 20,
            MainDbFlags = DatabaseFlags.DupSort | DatabaseFlags.DupFixed,
        });

    private static void Seed(LmdbEnvironment env, string key, int dupCount)
    {
        using var txn = env.BeginTransaction(readOnly: false);
        var db = txn.OpenDefaultDatabase();
        for (int i = 0; i < dupCount; i++)
            txn.Put(db, Encoding.UTF8.GetBytes(key), V(i));
        txn.Commit();
    }

    /// <summary>Collect all dups for a key via bulk ops; returns (values, chunkCount).</summary>
    private static (List<long> values, int chunks) BulkCollect(LmdbCursor cur, string key)
    {
        var values = new List<long>();
        int chunks = 0;
        if (!cur.TryGet(CursorOp.GetMultiple, Encoding.UTF8.GetBytes(key), out _, out var data))
            return (values, chunks);
        do
        {
            chunks++;
            Assert.True(data.Length > 0 && data.Length % ValSize == 0,
                $"chunk length {data.Length} not a multiple of {ValSize}");
            for (int o = 0; o < data.Length; o += ValSize)
                values.Add(BinaryPrimitives.ReadInt64BigEndian(data.Slice(o, ValSize)));
        } while (cur.TryGet(CursorOp.NextMultiple, default, out _, out data));
        return (values, chunks);
    }

    private static List<long> DupCollect(LmdbCursor cur, string key)
    {
        var values = new List<long>();
        if (!cur.TryGet(CursorOp.Set, Encoding.UTF8.GetBytes(key), out _, out var v))
            return values;
        do { values.Add(BinaryPrimitives.ReadInt64BigEndian(v)); }
        while (cur.TryGet(CursorOp.NextDup, default, out _, out v));
        return values;
    }

    [Theory]
    [InlineData(1)]        // plain single-value node
    [InlineData(40)]       // inline LEAF2 sub-page
    [InlineData(120)]      // sub-page near capacity
    [InlineData(500)]      // sub-DB, few pages
    [InlineData(20_000)]   // sub-DB, many pages (depth 2)
    [InlineData(120_000)]  // sub-DB past the sub-root branch split (depth 3)
    public void BulkIteration_EqualsNextDupIteration(int dupCount)
    {
        string dir = TmpDir($"multi-{dupCount}");
        using var env = DupFixedEnv(dir);
        Seed(env, "k", dupCount);

        using var txn = env.BeginTransaction(readOnly: true);
        var db = txn.OpenDefaultDatabase();

        List<long> expected;
        using (var cur = txn.CreateCursor(db)) expected = DupCollect(cur, "k");
        Assert.Equal(dupCount, expected.Count);

        using var bulk = txn.CreateCursor(db);
        var (got, chunks) = BulkCollect(bulk, "k");

        Assert.Equal(expected, got);
        // Bulk must actually batch: far fewer chunks than values.
        if (dupCount >= 40)
            Assert.True(chunks <= 1 + dupCount / 30,
                $"{chunks} chunks for {dupCount} dups — not batching");
    }

    [Fact]
    public void DeepDupTree_SurvivesRootSplit_AndRebalancingDeletes()
    {
        // Regression: SplitParent/Rebalance built their temp cursor via the
        // LmdbCursor ctor, which re-resolves DbRec by DBI — redirecting an
        // xcursor's sub-DB record to the MAIN record. A dup sub-tree's root
        // split (~52k 8-byte dups) then wrote its new root/depth into the main
        // DB's record and stranded the sub-DB on the stale root: every
        // previously inserted dup silently vanished and main bookkeeping was
        // corrupted (walker: depth-mismatch, page-count-mismatch).
        const int N = 120_000;
        string dir = TmpDir("deep-dup-tree");
        using var env = DupFixedEnv(dir);

        // Insert across many txns (the original repro shape).
        for (int done = 0; done < N; done += 10_000)
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            for (int i = done; i < done + 10_000; i++)
                txn.Put(db, "k"u8, V(i));
            txn.Commit();
        }
        Assert.True(LmdbIntegrityChecker.Check(dir).Clean,
            LmdbIntegrityChecker.Check(dir).Render());

        using (var txn = env.BeginTransaction(readOnly: true))
        {
            var db = txn.OpenDefaultDatabase();
            using var cur = txn.CreateCursor(db);
            Assert.Equal(N, DupCollect(cur, "k").Count);
        }

        // Rebalance twin: delete two thirds, forcing sub-tree merges at depth 3.
        using (var txn = env.BeginTransaction(readOnly: false))
        {
            var db = txn.OpenDefaultDatabase();
            using var cur = txn.CreateCursor(db);
            for (int i = 0; i < N; i++)
            {
                if (i % 3 == 0) continue;
                Assert.True(cur.TryGet(CursorOp.GetBoth, "k"u8, V(i), out _, out _), $"dup {i} missing");
                cur.DeleteCurrent();
            }
            txn.Commit();
        }
        Assert.True(LmdbIntegrityChecker.Check(dir).Clean,
            LmdbIntegrityChecker.Check(dir).Render());
        using (var txn = env.BeginTransaction(readOnly: true))
        {
            var db = txn.OpenDefaultDatabase();
            using var cur = txn.CreateCursor(db);
            var left = DupCollect(cur, "k");
            Assert.Equal(N / 3 + (N % 3 == 0 ? 0 : 1), left.Count);
            Assert.All(left, v => Assert.Equal(0, v % 3));
        }
    }

    [Fact]
    public void GetMultiple_MissingKey_ReturnsFalse()
    {
        string dir = TmpDir("multi-miss");
        using var env = DupFixedEnv(dir);
        Seed(env, "k", 10);
        using var txn = env.BeginTransaction(readOnly: true);
        var db = txn.OpenDefaultDatabase();
        using var cur = txn.CreateCursor(db);
        Assert.False(cur.TryGet(CursorOp.GetMultiple, "absent"u8, out _, out _));
    }

    [Fact]
    public void GetMultiple_OnPositionedCursor_NoKeyNeeded()
    {
        string dir = TmpDir("multi-positioned");
        using var env = DupFixedEnv(dir);
        Seed(env, "k", 300);
        using var txn = env.BeginTransaction(readOnly: true);
        var db = txn.OpenDefaultDatabase();
        using var cur = txn.CreateCursor(db);
        Assert.True(cur.TryGet(CursorOp.First, default, out _, out _));
        Assert.True(cur.TryGet(CursorOp.GetMultiple, default, out var k, out var data));
        Assert.Equal("k", Encoding.UTF8.GetString(k));
        Assert.True(data.Length >= ValSize);
        Assert.Equal(0L, BinaryPrimitives.ReadInt64BigEndian(data));
    }

    [Fact]
    public void FirstMultiple_RestartsIteration_LastMultiple_ReturnsTail()
    {
        string dir = TmpDir("multi-firstlast");
        using var env = DupFixedEnv(dir);
        const int N = 5_000;
        Seed(env, "k", N);
        using var txn = env.BeginTransaction(readOnly: true);
        var db = txn.OpenDefaultDatabase();
        using var cur = txn.CreateCursor(db);

        var (all, _) = BulkCollect(cur, "k");
        Assert.Equal(N, all.Count);

        // Iteration is exhausted; FirstMultiple rewinds to chunk one.
        Assert.True(cur.TryGet(CursorOp.FirstMultiple, default, out _, out var first));
        Assert.Equal(0L, BinaryPrimitives.ReadInt64BigEndian(first));

        // LastMultiple returns the final chunk: its values are the tail of the list.
        Assert.True(cur.TryGet(CursorOp.LastMultiple, default, out _, out var last));
        int lastCount = last.Length / ValSize;
        Assert.Equal(N - 1, BinaryPrimitives.ReadInt64BigEndian(last[^ValSize..]));
        var lastVals = new long[lastCount];
        for (int i = 0; i < lastCount; i++)
            lastVals[i] = BinaryPrimitives.ReadInt64BigEndian(last.Slice(i * ValSize, ValSize));
        Assert.Equal(all[^lastCount..], lastVals);
        // After LastMultiple the iteration is at the end.
        Assert.False(cur.TryGet(CursorOp.NextMultiple, default, out _, out _));
    }

    [Fact]
    public void MultipleOps_OnNonDupFixedDb_ThrowIncompatible()
    {
        string dir = TmpDir("multi-incompat");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions
        { ReadOnly = false, MapSize = 64L << 20, MainDbFlags = DatabaseFlags.DupSort });
        Seed(env, "k", 5);
        using var txn = env.BeginTransaction(readOnly: true);
        var db = txn.OpenDefaultDatabase();
        using var cur = txn.CreateCursor(db);
        var ex = Assert.Throws<LmdbException>(() => cur.TryGet(CursorOp.GetMultiple, "k"u8, out _, out _));
        Assert.Equal(LmdbErr.Incompatible, ex.ErrorCode);
    }

    [Fact]
    public void BulkIteration_InWriteTxn_SeesUncommittedDups()
    {
        string dir = TmpDir("multi-writetxn");
        using var env = DupFixedEnv(dir);
        using var txn = env.BeginTransaction(readOnly: false);
        var db = txn.OpenDefaultDatabase();
        for (int i = 0; i < 2_000; i++)
            txn.Put(db, "k"u8, V(i));

        using var cur = txn.CreateCursor(db);
        var (got, _) = BulkCollect(cur, "k");
        Assert.Equal(Enumerable.Range(0, 2_000).Select(i => (long)i), got);
        txn.Commit();
    }

    [Fact]
    public void BulkIteration_AcrossManyKeys_MatchesPerKeyDupCounts()
    {
        // Index-shaped workload: many keys, varying dup counts, iterate every key.
        string dir = TmpDir("multi-manykeys");
        using var env = DupFixedEnv(dir);
        var counts = new Dictionary<string, int>();
        using (var txn = env.BeginTransaction(readOnly: false))
        {
            var db = txn.OpenDefaultDatabase();
            var rng = new Random(7);
            for (int k = 0; k < 50; k++)
            {
                string key = $"tag{k:D3}";
                int n = 1 + rng.Next(3_000);
                counts[key] = n;
                for (int i = 0; i < n; i++)
                    txn.Put(db, Encoding.UTF8.GetBytes(key), V(i));
            }
            txn.Commit();
        }

        using (var txn = env.BeginTransaction(readOnly: true))
        {
            var db = txn.OpenDefaultDatabase();
            using var cur = txn.CreateCursor(db);
            foreach (var (key, n) in counts)
            {
                var (got, _) = BulkCollect(cur, key);
                Assert.Equal(n, got.Count);
                Assert.Equal(Enumerable.Range(0, n).Select(i => (long)i), got);
            }
        }
        Assert.True(LmdbIntegrityChecker.Check(dir).Clean);
    }
}
