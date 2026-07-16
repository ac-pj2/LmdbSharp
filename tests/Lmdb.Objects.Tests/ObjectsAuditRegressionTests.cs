using Lmdb;
using Lmdb.Objects;
using MemoryPack;
using Xunit;

namespace Lmdb.Objects.Tests;

[MemoryPackable]
public partial class AuditUser
{
    public long Id { get; set; }
    public string Email { get; set; } = "";
    public int Age { get; set; }
}

[MemoryPackable]
public partial class KeyedAuditUser
{
    [LmdbKey] public long Id { get; set; }
    public string Email { get; set; } = "";
    public int Age { get; set; }
}

// Regressions for the 2026-07-16 Objects-layer audit (index ordering, GetBatch,
// LINQ translation, batch-mode state, upsert index hygiene, cold-start).
public class ObjectsAuditRegressionTests
{
    private static string TmpDir(string name)
    {
        var dir = $"/tmp/lmdb-cs/objaudit_{name}";
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ObjectDatabase Open(string dir) => ObjectDatabase.Open(dir,
        new ObjectDatabaseOptions { MapSize = 1 << 24, MaxDbs = 32 });

    private static Collection<AuditUser> UsersWithAgeIndex(ObjectDatabase db)
    {
        var users = db.GetCollection<AuditUser>("users");
        users.RegisterIndex("Age", x => (long)x.Age, unique: false);
        db.EnsureIndex(users, "Age", x => (long)x.Age);
        return users;
    }

    // C1: numeric index range scans must be value-ordered, including >255.
    [Fact]
    public void Index_range_scan_orders_numerically_across_byte_boundaries()
    {
        using var db = Open(TmpDir("c1_range"));
        var users = UsersWithAgeIndex(db);
        foreach (var age in new[] { 100, 200, 300, 999, 5 })
            users.Insert(new AuditUser { Email = $"u{age}@x", Age = age });

        using var txn = db.BeginRead();
        var inRange = users.FindByRange(txn, "Age", 150L, 400L).Select(u => u.Age).OrderBy(a => a).ToList();
        Assert.Equal(new[] { 200, 300 }, inRange);
        var all = users.FindByRange(txn, "Age", null, null).Select(u => u.Age).ToList();
        Assert.Equal(new[] { 5, 100, 200, 300, 999 }, all);   // index order == numeric order
    }

    // C2: GetBatch must find every existing key regardless of numeric magnitude
    // or request order.
    [Fact]
    public void GetBatch_finds_keys_across_byte_boundaries_and_any_order()
    {
        using var db = Open(TmpDir("c2_batch"));
        var users = db.GetCollection<KeyedAuditUser>("users");
        using (var txn = db.BeginWrite())
        {
            foreach (var id in new long[] { 1, 5, 256, 700 })
                users.Insert(txn, new KeyedAuditUser { Id = id, Email = $"e{id}" });
            txn.Commit();
        }
        using var read = db.BeginRead();
        var r1 = users.GetBatch(read, new object[] { 1L, 256L });
        Assert.Equal(2, r1.Count(x => x != null));
        var r2 = users.GetBatch(read, new object[] { 700L, 1L, 999L, 5L });
        Assert.Equal("e700", r2[0]!.Email);
        Assert.Equal("e1", r2[1]!.Email);
        Assert.Null(r2[2]);
        Assert.Equal("e5", r2[3]!.Email);
    }

    // C3: chained Where must compose.
    [Fact]
    public void Chained_where_clauses_compose()
    {
        using var db = Open(TmpDir("c3_where"));
        var users = db.GetCollection<AuditUser>("users");
        using (var txn = db.BeginWrite())
        {
            users.Insert(txn, new AuditUser { Email = "a@x", Age = 30 });
            users.Insert(txn, new AuditUser { Email = "b@x", Age = 30 });
            users.Insert(txn, new AuditUser { Email = "a@x", Age = 40 });
            txn.Commit();
        }
        using var read = db.BeginRead();
        var result = users.Query(read).Where(x => x.Age == 30).Where(x => x.Email == "a@x").ToList();
        Assert.Single(result);
        Assert.Equal(30, result[0].Age);
        Assert.Equal("a@x", result[0].Email);
    }

    // C5: inserting over an existing key must not leave stale index entries.
    [Fact]
    public void Insert_over_existing_key_replaces_index_entries()
    {
        using var db = Open(TmpDir("c5_upsert"));
        var users = db.GetCollection<KeyedAuditUser>("users");
        users.RegisterIndex("Email", x => x.Email, unique: false);
        db.EnsureIndex(users, "Email", x => x.Email);
        using (var txn = db.BeginWrite())
        {
            users.Insert(txn, new KeyedAuditUser { Id = 1, Email = "old@x" });
            txn.Commit();
        }
        using (var txn = db.BeginWrite())
        {
            users.Insert(txn, new KeyedAuditUser { Id = 1, Email = "new@x" });
            txn.Commit();
        }
        using var read = db.BeginRead();
        Assert.Empty(users.FindAllBy(read, "Email", "old@x").ToList());
        Assert.Single(users.FindAllBy(read, "Email", "new@x").ToList());
    }

    // H1: querying an unindexed field must fall back to a scan, not throw or
    // return wrong results.
    [Fact]
    public void Query_on_unindexed_field_falls_back_to_scan()
    {
        using var db = Open(TmpDir("h1_fallback"));
        var users = db.GetCollection<AuditUser>("users");
        using (var txn = db.BeginWrite())
        {
            users.Insert(txn, new AuditUser { Email = "hit@x", Age = 42 });
            users.Insert(txn, new AuditUser { Email = "miss@x", Age = 7 });
            txn.Commit();
        }
        using var read = db.BeginRead();
        var hits = users.Query(read).Where(x => x.Email == "hit@x").ToList();
        Assert.Single(hits);
        var range = users.Query(read).Where(x => x.Age >= 10 && x.Age < 100).ToList();
        Assert.Single(range);
    }

    // H3: comparison boundary semantics must match the C# operators exactly.
    [Fact]
    public void Query_comparison_boundaries_match_operator_semantics()
    {
        using var db = Open(TmpDir("h3_bounds"));
        var users = UsersWithAgeIndex(db);
        foreach (var age in new[] { 17, 18, 40, 65, 66 })
            users.Insert(new AuditUser { Email = $"u{age}", Age = age });

        using var read = db.BeginRead();
        Assert.Equal(new[] { 40, 65, 66 },
            users.Query(read).Where(x => x.Age > 18).ToList().Select(u => u.Age).OrderBy(a => a));
        Assert.Equal(new[] { 17, 18, 40, 65 },
            users.Query(read).Where(x => x.Age <= 65).ToList().Select(u => u.Age).OrderBy(a => a));
        Assert.Equal(new[] { 18, 40 },
            users.Query(read).Where(x => x.Age >= 18 && x.Age < 65).ToList().Select(u => u.Age).OrderBy(a => a));
    }

    // H4: Skip/Take must apply exactly once on index-backed paths.
    [Fact]
    public void Skip_take_apply_exactly_once_on_index_paths()
    {
        using var db = Open(TmpDir("h4_page"));
        var users = UsersWithAgeIndex(db);
        for (int i = 0; i < 10; i++)
            users.Insert(new AuditUser { Email = $"u{i}", Age = 33 });

        using var read = db.BeginRead();
        var page = users.Query(read).Where(x => x.Age == 33).Skip(4).Take(3).ToList();
        Assert.Equal(3, page.Count);
        var all = users.Query(read).Where(x => x.Age == 33).Skip(8).ToList();
        Assert.Equal(2, all.Count);
    }

    // H6: reads on a never-written collection must return empty, not throw.
    [Fact]
    public void Reads_on_brand_new_collection_return_empty()
    {
        using var db = Open(TmpDir("h6_cold"));
        var users = db.GetCollection<AuditUser>("users");
        using var read = db.BeginRead();
        Assert.Null(users.Get(read, 1L));
        Assert.All(users.GetBatch(read, new object[] { 1L, 2L }), Assert.Null);
        Assert.Empty(users.FindAllBy(read, "Email", "x").ToList());
        Assert.Empty(users.Query(read).Where(x => x.Age > 1).ToList());
        Assert.Equal(0, users.Count(read));
    }

    // M2: EnsureIndex must backfill rows that existed before the index.
    [Fact]
    public void EnsureIndex_backfills_existing_rows()
    {
        using var db = Open(TmpDir("m2_backfill"));
        var users = db.GetCollection<AuditUser>("users");
        using (var txn = db.BeginWrite())
        {
            users.Insert(txn, new AuditUser { Email = "pre@x", Age = 50 });
            txn.Commit();
        }
        users.RegisterIndex("Email", x => x.Email, unique: false);
        db.EnsureIndex(users, "Email", x => x.Email);

        using var read = db.BeginRead();
        Assert.Single(users.FindAllBy(read, "Email", "pre@x").ToList());
    }

    // C4: batching must not permanently divert index maintenance.
    [Fact]
    public void Index_maintenance_stays_immediate_after_batch_use()
    {
        using var db = Open(TmpDir("c4_batch"));
        var users = db.GetCollection<AuditUser>("users");
        users.RegisterIndex("Email", x => x.Email, unique: false);
        db.EnsureIndex(users, "Email", x => x.Email);

        using (var txn = db.BeginWrite())
        {
            users.BeginBatch();
            users.Insert(txn, new AuditUser { Email = "batched@x" });
            users.FlushPendingIndexes(txn);
            txn.Commit();
        }
        // A later, non-batched insert must maintain its index in ITS OWN txn.
        using (var txn = db.BeginWrite())
        {
            users.Insert(txn, new AuditUser { Email = "after@x" });
            using var readInTxn = db.BeginRead();
            txn.Commit();
        }
        using var read = db.BeginRead();
        Assert.Single(users.FindAllBy(read, "Email", "after@x").ToList());
        Assert.Single(users.FindAllBy(read, "Email", "batched@x").ToList());
    }
}
