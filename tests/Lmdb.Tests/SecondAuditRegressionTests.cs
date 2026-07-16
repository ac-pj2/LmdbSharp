using System.Text;
using Lmdb;
using Xunit;

namespace Lmdb.Tests;

// Regressions for the 2026-07-16 second audit (adversarial review of the first
// fix wave + read-path review).
public class SecondAuditRegressionTests
{
    private static (LmdbEnvironment env, string path) OpenEnv()
    {
        var path = $"/tmp/lmdb-cs/audit2-{Guid.NewGuid():N}";
        Directory.CreateDirectory(path);
        var env = LmdbEnvironment.Open(path,
            new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 24, MaxDbs = 8 });
        return (env, path);
    }

    // F2: a named DB dropped in a CHILD txn must stay dropped after the parent
    // commits — the parent's own record used to resurrect it pointing at the
    // pages the child freed.
    [Fact]
    public void Child_drop_of_parent_opened_database_survives_parent_commit()
    {
        var (env, path) = OpenEnv();
        try
        {
            using (var setup = env.BeginTransaction(readOnly: false))
            {
                var db = setup.OpenDatabase("victim", DatabaseFlags.Create);
                for (int i = 0; i < 40; i++)
                    setup.Put(db, Encoding.UTF8.GetBytes($"k{i:D3}"), new byte[300]);
                setup.Commit();
            }
            using (var parent = env.BeginTransaction(readOnly: false))
            {
                var handle = parent.OpenDatabase("victim");
                using (var child = parent.BeginChild())
                {
                    child.Drop(handle, delete: true);
                    child.Commit();
                }
                parent.Put(parent.OpenDefaultDatabase(), "other"u8, "work"u8);
                parent.Commit();
            }
            using (var read = env.BeginTransaction(readOnly: true))
            {
                Assert.Throws<LmdbException>(() => read.OpenDatabase("victim"));
            }
            // Churn so freed pages recycle; the resurrected-root bug shows up
            // as duplicate ownership / corruption here.
            for (int i = 0; i < 10; i++)
                using (var txn = env.BeginTransaction(readOnly: false))
                {
                    txn.Put(txn.OpenDefaultDatabase(), Encoding.UTF8.GetBytes($"post{i}"), new byte[500]);
                    txn.Commit();
                }
            var report = LmdbIntegrityChecker.Check(path);
            Assert.True(report.Clean, report.Render());
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }

    // F3: keys above C's MDB_MAXKEYSIZE are rejected up front — previously a
    // long key pushed its node into overflow form, which the DUPSORT machinery
    // misread as inline data.
    [Fact]
    public void Keys_beyond_max_key_size_are_rejected()
    {
        var (env, path) = OpenEnv();
        try
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDatabase("dup", DatabaseFlags.Create | DatabaseFlags.DupSort);
            var bigKey = new byte[1600];
            var ex = Assert.Throws<LmdbException>(() => txn.Put(db, bigKey, "v"u8));
            Assert.Equal(LmdbErr.BadValsize, ex.ErrorCode);
            txn.Put(db, new byte[511], "v"u8);   // max size still accepted
            txn.Commit();
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }

    // Read-path: REVERSEKEY ordering with variable-length keys (the Memnr
    // comparator compared the wrong byte window and read out of bounds).
    [Fact]
    public void ReverseKey_database_orders_by_reversed_bytes()
    {
        var (env, path) = OpenEnv();
        try
        {
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDatabase("rev", DatabaseFlags.Create | DatabaseFlags.ReverseKey);
                foreach (var k in new[] { "abc", "zbc", "yc", "x", "ABX", "CX" })
                    txn.Put(db, Encoding.UTF8.GetBytes(k), Encoding.UTF8.GetBytes($"v-{k}"));
                txn.Commit();
            }
            using (var read = env.BeginTransaction(readOnly: true))
            {
                var db = read.OpenDatabase("rev");
                // Every inserted key must be retrievable (missorted trees lose keys
                // to binary search).
                foreach (var k in new[] { "abc", "zbc", "yc", "x", "ABX", "CX" })
                    Assert.True(read.TryGet(db, Encoding.UTF8.GetBytes(k), out _), $"key '{k}' unreachable");
                // Scan order = order of REVERSED byte strings.
                var keys = new List<string>();
                using var cur = read.CreateCursor(db);
                if (cur.TryGet(CursorOp.First, default, out var kk, out _))
                    do { keys.Add(Encoding.UTF8.GetString(kk)); }
                    while (cur.TryGet(CursorOp.Next, default, out kk, out _));
                var expected = new[] { "abc", "zbc", "yc", "x", "ABX", "CX" }
                    .OrderBy(k => new string(k.Reverse().ToArray()), StringComparer.Ordinal).ToList();
                Assert.Equal(expected, keys);
            }
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }

    // Read-path: GetBoth must require EXACT data equality on sub-DB dup sets.
    [Fact]
    public void GetBoth_requires_exact_match_on_subdb_dups()
    {
        var (env, path) = OpenEnv();
        try
        {
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDatabase("dup", DatabaseFlags.Create | DatabaseFlags.DupSort);
                for (int i = 0; i < 400; i++)   // force sub-DB conversion
                    txn.Put(db, "k"u8, Encoding.UTF8.GetBytes($"val-{i * 10:D6}"));
                txn.Commit();
            }
            using var read = env.BeginTransaction(readOnly: true);
            var rdb = read.OpenDatabase("dup");
            using var cur = read.CreateCursor(rdb);
            Assert.True(cur.TryGet(CursorOp.GetBoth, "k"u8, "val-000050"u8, out _, out _));
            // "val-000055" doesn't exist but is < "val-000060": GetBoth must miss,
            // GetBothRange must land on the next dup.
            Assert.False(cur.TryGet(CursorOp.GetBoth, "k"u8, "val-000055"u8, out _, out _));
            Assert.True(cur.TryGet(CursorOp.GetBothRange, "k"u8, "val-000055"u8, out _, out var d));
            Assert.Equal("val-000060", Encoding.UTF8.GetString(d));
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }
}
