using System.Text;
using Lmdb;
using Xunit;

namespace Lmdb.Tests;

// Regressions for the split/rebalance defects found in the 2026-07-15 audit
// (docs/CODE-AUDIT-2026-07-15.md S1-1..S1-4, S3-5). All scenarios push the
// tree to depth 3+ and keep using the SAME cursor across structural changes —
// the port has no multi-cursor fixup machinery, so the one live cursor's stack
// must survive splits, merges, and root growth/collapse.
public class BTreeDeepTreeTests
{
    private const int KeyCount = 1500;   // ~4 values/leaf, ~200 leaves/branch → depth 3

    private static byte[] Key(int i) => Encoding.UTF8.GetBytes($"key-{i:D6}");

    private static byte[] Value(int i)
    {
        var v = new byte[1000];
        for (int j = 0; j < v.Length; j++) v[j] = (byte)(i + j);
        return v;
    }

    private static (LmdbEnvironment env, string path) BuildDeepTree()
    {
        var path = $"/tmp/lmdb-cs/deeptree-{Guid.NewGuid():N}";
        Directory.CreateDirectory(path);
        var env = LmdbEnvironment.Open(path,
            new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 26 });
        using (var txn = env.BeginTransaction(readOnly: false))
        {
            var db = txn.OpenDefaultDatabase();
            // Sequential appends in ONE transaction: the cached write cursor's
            // append fast path keeps the stack alive across every split,
            // including recursive parent splits and root growth.
            for (int i = 0; i < KeyCount; i++)
                txn.Put(db, Key(i), Value(i));
            txn.Commit();
        }
        return (env, path);
    }

    private static void AssertStrictlyClean(string path)
    {
        var report = LmdbIntegrityChecker.Check(path);
        var significant = report.Findings.Where(f => f.Severity != IntegritySeverity.Info).ToList();
        Assert.True(significant.Count == 0,
            "walker findings:\n" + string.Join("\n", significant));
    }

    // S1-1: recursive parent split / root growth desyncs the append cursor.
    [Fact]
    public void Sequential_appends_through_deep_root_growth_stay_consistent()
    {
        var (env, path) = BuildDeepTree();
        try
        {
            using (var read = env.BeginTransaction(readOnly: true))
            {
                var db = read.OpenDefaultDatabase();
                Assert.Equal((ulong)KeyCount, db.Entries);
                Assert.True(db.Depth >= 3, $"test needs depth>=3, got {db.Depth}");
                for (int i = 0; i < KeyCount; i++)
                {
                    Assert.True(read.TryGet(db, Key(i), out var v), $"key {i} missing");
                    Assert.Equal(Value(i), v.ToArray());
                }
            }
            AssertStrictlyClean(path);
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }

    // S1-2: branch-level NodeMove with insert at slot 0 must not publish an
    // empty separator — deleting a middle range forces underflowed branches to
    // borrow from their left siblings.
    [Fact]
    public void Middle_range_deletion_keeps_remaining_keys_reachable()
    {
        var (env, path) = BuildDeepTree();
        try
        {
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDefaultDatabase();
                for (int i = 500; i < 990; i++)
                    Assert.True(txn.Delete(db, Key(i)), $"delete {i} failed");
                txn.Commit();
            }

            using (var read = env.BeginTransaction(readOnly: true))
            {
                var db = read.OpenDefaultDatabase();
                Assert.Equal((ulong)(KeyCount - 490), db.Entries);
                for (int i = 0; i < KeyCount; i++)
                {
                    bool expected = i < 500 || i >= 990;
                    Assert.True(expected == read.TryGet(db, Key(i), out var v),
                        $"key {i}: expected present={expected}");
                    if (expected) Assert.Equal(Value(i), v.ToArray());
                }
                // Full ordered scan must return exactly the surviving keys.
                long scanned = 0;
                using var cur = read.CreateCursor(db);
                if (cur.TryGet(CursorOp.First, default, out var k, out _))
                    do { scanned++; } while (cur.TryGet(CursorOp.Next, default, out k, out _));
                Assert.Equal(KeyCount - 490, scanned);
            }
            AssertStrictlyClean(path);
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }

    // S1-3/S1-4: root collapse and fromleft merges with a live cursor — iterate
    // with one cursor and delete every entry, shrinking depth 3 → empty.
    [Fact]
    public void Cursor_iteration_delete_empties_deep_tree()
    {
        var (env, path) = BuildDeepTree();
        try
        {
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDefaultDatabase();
                using var cur = txn.CreateCursor(db);
                long deleted = 0;
                while (cur.TryGet(CursorOp.First, default, out _, out _))
                {
                    cur.DeleteCurrent();
                    deleted++;
                    Assert.True(deleted <= KeyCount, "cursor delete loop ran past the entry count");
                }
                Assert.Equal(KeyCount, deleted);
                txn.Commit();
            }

            using (var read = env.BeginTransaction(readOnly: true))
            {
                var db = read.OpenDefaultDatabase();
                Assert.Equal(0UL, db.Entries);
                using var cur = read.CreateCursor(db);
                Assert.False(cur.TryGet(CursorOp.First, default, out _, out _));
            }
            AssertStrictlyClean(path);
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }

    // S1-2 targeted: underflow the RIGHTMOST level-1 branch against a full
    // left sibling — the borrow (fromleft NodeMove at branch level) inserts at
    // destination slot 0, where the displaced node must receive its real
    // separator key instead of remaining keyless.
    [Fact]
    public void Tail_range_deletion_borrows_from_left_branch_without_losing_keys()
    {
        var (env, path) = BuildDeepTree();
        try
        {
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDefaultDatabase();
                // Delete from the tail downward, leaving a thin rightmost branch.
                for (int i = KeyCount - 1; i >= 760; i--)
                    Assert.True(txn.Delete(db, Key(i)), $"delete {i} failed");
                txn.Commit();
            }

            using (var read = env.BeginTransaction(readOnly: true))
            {
                var db = read.OpenDefaultDatabase();
                Assert.Equal(760UL, db.Entries);
                for (int i = 0; i < 760; i++)
                {
                    Assert.True(read.TryGet(db, Key(i), out var v), $"key {i} unreachable");
                    Assert.Equal(Value(i), v.ToArray());
                }
                long scanned = 0;
                using var cur = read.CreateCursor(db);
                if (cur.TryGet(CursorOp.First, default, out var k, out _))
                    do { scanned++; } while (cur.TryGet(CursorOp.Next, default, out k, out _));
                Assert.Equal(760, scanned);
            }
            AssertStrictlyClean(path);
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }

    // S1-1 targeted: a cursor whose Put triggers a recursive parent split (root
    // growth) must still have a coherent stack — iterating onward from the
    // freshly inserted key has to visit exactly the remaining keys.
    [Fact]
    public void Cursor_iteration_after_root_growing_split_visits_exact_remainder()
    {
        var path = $"/tmp/lmdb-cs/rootgrow-{Guid.NewGuid():N}";
        Directory.CreateDirectory(path);
        var env = LmdbEnvironment.Open(path,
            new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 26 });
        try
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            using var cur = txn.CreateCursor(db);
            int grew = -1;
            for (int i = 0; i < KeyCount; i++)
            {
                ushort before = db.Depth;
                cur.Put(Key(i), Value(i), PutFlags.None);
                if (before == 2 && db.Depth == 3) { grew = i; break; }
            }
            Assert.True(grew > 0, "workload never grew the root to depth 3");

            // Iterate FORWARD from the cursor's post-split position without
            // re-seeking: it must see nothing (the just-inserted key was the
            // maximum). Then re-seek to key 0 and count everything.
            long after = 0;
            while (cur.TryGet(CursorOp.Next, default, out _, out _)) after++;
            Assert.Equal(0, after);

            long total = 0;
            if (cur.TryGet(CursorOp.First, default, out _, out _))
                do { total++; } while (cur.TryGet(CursorOp.Next, default, out _, out _));
            Assert.Equal(grew + 1, total);

            // And the stack must be sane enough to keep writing through it.
            for (int i = grew + 1; i < grew + 200; i++)
                cur.Put(Key(i), Value(i), PutFlags.None);
            txn.Commit();

            using var read = env.BeginTransaction(readOnly: true);
            var rdb = read.OpenDefaultDatabase();
            Assert.Equal((ulong)(grew + 200), rdb.Entries);
            for (int i = 0; i <= grew + 199; i++)
                Assert.True(read.TryGet(rdb, Key(i), out _), $"key {i} missing");
            AssertStrictlyClean(path);
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }

    // Values sweeping the inline/overflow boundary: a split half receiving two
    // maximum-size inline nodes needs their pointer slots too — with NodeMax at
    // exactly half the node area, PageSplit overflowed by 4 bytes and threw
    // PageFull (found by the P3-shaped soak at ~1500 records).
    [Fact]
    public void Values_sweeping_the_inline_overflow_boundary_split_safely()
    {
        var path = $"/tmp/lmdb-cs/boundary-{Guid.NewGuid():N}";
        Directory.CreateDirectory(path);
        var env = LmdbEnvironment.Open(path,
            new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 26 });
        try
        {
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDefaultDatabase();
                for (int i = 0; i < 800; i++)
                {
                    // Sizes straddle the max-inline node size from both sides.
                    int len = 1940 + (i % 160);
                    txn.Put(db, Key(i), new byte[len]);
                }
                txn.Commit();
            }
            using (var read = env.BeginTransaction(readOnly: true))
            {
                var db = read.OpenDefaultDatabase();
                Assert.Equal(800UL, db.Entries);
                for (int i = 0; i < 800; i++)
                {
                    Assert.True(read.TryGet(db, Key(i), out var v), $"key {i} missing");
                    Assert.Equal(1940 + (i % 160), v.Length);
                }
            }
            AssertStrictlyClean(path);
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }

    // Alternating delete/insert churn on a deep tree — exercises NodeMove in
    // both directions and split-after-merge interleavings with live cursors.
    [Fact]
    public void Deep_tree_survives_interleaved_delete_and_reinsert_churn()
    {
        var (env, path) = BuildDeepTree();
        try
        {
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDefaultDatabase();
                for (int round = 0; round < 3; round++)
                {
                    for (int i = round * 100; i < round * 100 + 400; i += 2)
                        txn.Delete(db, Key(i));
                    for (int i = round * 100; i < round * 100 + 400; i += 2)
                        txn.Put(db, Key(i), Value(i + 7));
                }
                txn.Commit();
            }

            using (var read = env.BeginTransaction(readOnly: true))
            {
                var db = read.OpenDefaultDatabase();
                Assert.Equal((ulong)KeyCount, db.Entries);
                for (int i = 0; i < KeyCount; i++)
                    Assert.True(read.TryGet(db, Key(i), out _), $"key {i} missing after churn");
            }
            AssertStrictlyClean(path);
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }
}
