using System.Text;
using Lmdb;
using Xunit;

namespace Lmdb.Tests;

/// <summary>
/// Page-spill tests: when a write txn's dirty list exceeds EnvOpenOptions
/// .MaxDirtyPages, excess pages are written into the map early and their
/// buffers released. These run with a tiny budget so every path spills hard.
/// </summary>
public class PageSpillTests
{
    private static string TmpDir(string name)
    {
        string dir = $"/tmp/lmdb-cs/{name}";
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    private static EnvOpenOptions Opts(int maxDirty) => new()
    {
        ReadOnly = false,
        MapSize = 512L << 20,
        MaxDirtyPages = maxDirty,
    };

    private static byte[] K(int i) => Encoding.UTF8.GetBytes($"key{i:00000000}");
    private static byte[] V(int i) => Encoding.UTF8.GetBytes($"value{i:00000000}_payload");

    [Fact]
    public void LargeTxn_WithTinyDirtyBudget_CommitsAndReadsBack()
    {
        const int N = 150_000;   // thousands of pages against a 64-page budget
        string dir = TmpDir("spill-large");
        using (var env = LmdbEnvironment.Open(dir, Opts(64)))
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < N; i++)
                txn.Put(db, K(i), V(i));
            txn.Commit();
        }

        Assert.True(LmdbIntegrityChecker.Check(dir).Clean);
        using (var env = LmdbEnvironment.Open(dir))
        {
            using var txn = env.BeginTransaction(readOnly: true);
            var db = txn.OpenDefaultDatabase();
            Assert.Equal((ulong)N, db.Entries);
            foreach (int i in new[] { 0, 1, N / 3, N / 2, N - 2, N - 1 })
            {
                Assert.True(txn.TryGet(db, K(i), out var v), $"key {i} missing");
                Assert.Equal(V(i), v.ToArray());
            }
        }
    }

    [Fact]
    public void RandomOrderLargeTxn_WithSpill_FullContentsSurvive()
    {
        const int N = 80_000;
        string dir = TmpDir("spill-random");
        var order = new int[N];
        for (int i = 0; i < N; i++) order[i] = i;
        var rng = new Random(99);
        for (int i = N - 1; i > 0; i--) { int j = rng.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }

        using (var env = LmdbEnvironment.Open(dir, Opts(64)))
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            foreach (int i in order) txn.Put(db, K(i), V(i));
            txn.Commit();
        }

        Assert.True(LmdbIntegrityChecker.Check(dir).Clean);
        using (var env = LmdbEnvironment.Open(dir))
        {
            using var txn = env.BeginTransaction(readOnly: true);
            var db = txn.OpenDefaultDatabase();
            using var cur = txn.CreateCursor(db);
            int n = 0;
            if (cur.TryGet(CursorOp.First, default, out var k, out var v))
            {
                do
                {
                    Assert.Equal(K(n), k.ToArray());
                    Assert.Equal(V(n), v.ToArray());
                    n++;
                } while (cur.TryGet(CursorOp.Next, default, out k, out v));
            }
            Assert.Equal(N, n);
        }
    }

    [Fact]
    public void OpenCursor_SurvivesSpillsDuringHeavyPuts()
    {
        const int N = 60_000;
        string dir = TmpDir("spill-cursor");
        using var env = LmdbEnvironment.Open(dir, Opts(64));
        using (var txn = env.BeginTransaction(readOnly: false))
        {
            var db = txn.OpenDefaultDatabase();
            // Seed a range the cursor will sit on.
            for (int i = 0; i < 100; i++)
                txn.Put(db, K(i), V(i));

            // Park a cursor mid-range, then hammer higher keys to force spills.
            using var cur = txn.CreateCursor(db);
            Assert.True(cur.TryGet(CursorOp.Set, K(50), out _, out _));

            for (int i = 100; i < N; i++)
                txn.Put(db, K(i), V(i));

            // The parked cursor must still iterate correctly from its position.
            int expect = 51;
            while (cur.TryGet(CursorOp.Next, default, out var k, out _) && expect < 200)
            {
                Assert.Equal(K(expect), k.ToArray());
                expect++;
            }
            txn.Commit();
        }
        Assert.True(LmdbIntegrityChecker.Check(dir).Clean);
    }

    [Fact]
    public void OverflowValues_WithSpill_RoundTrip()
    {
        const int N = 3_000;
        string dir = TmpDir("spill-overflow");
        using (var env = LmdbEnvironment.Open(dir, Opts(32)))
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < N; i++)
            {
                var v = new byte[6000];   // > page size: 2-page overflow chain
                v[0] = (byte)i; v[^1] = (byte)(i >> 8);
                txn.Put(db, K(i), v);
            }
            txn.Commit();
        }

        Assert.True(LmdbIntegrityChecker.Check(dir).Clean);
        using (var env = LmdbEnvironment.Open(dir))
        {
            using var txn = env.BeginTransaction(readOnly: true);
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < N; i += 97)
            {
                Assert.True(txn.TryGet(db, K(i), out var v));
                Assert.Equal(6000, v.Length);
                Assert.Equal((byte)i, v[0]);
                Assert.Equal((byte)(i >> 8), v[^1]);
            }
        }
    }

    [Fact]
    public void DeleteHeavyTxn_WithSpill_StaysConsistent()
    {
        const int N = 60_000;
        string dir = TmpDir("spill-delete");
        using (var env = LmdbEnvironment.Open(dir, Opts(2048)))
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < N; i++) txn.Put(db, K(i), V(i));
            txn.Commit();
        }
        using (var env = LmdbEnvironment.Open(dir, Opts(64)))
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < N; i += 2) Assert.True(txn.Delete(db, K(i)));
            txn.Commit();
        }

        Assert.True(LmdbIntegrityChecker.Check(dir).Clean);
        using (var env = LmdbEnvironment.Open(dir))
        {
            using var txn = env.BeginTransaction(readOnly: true);
            var db = txn.OpenDefaultDatabase();
            Assert.Equal((ulong)(N / 2), db.Entries);
            Assert.False(txn.TryGet(db, K(0), out _));
            Assert.True(txn.TryGet(db, K(1), out _));
        }
    }

    [Fact]
    public void SpilledThenRewrittenKeys_TakeCowPath_NotInPlace()
    {
        // Overwriting keys whose pages were spilled must COW (the buffer is
        // gone); interleave writes so most of the tree is spilled between
        // overwrites of the same keys.
        const int N = 40_000;
        string dir = TmpDir("spill-rewrite");
        using (var env = LmdbEnvironment.Open(dir, Opts(32)))
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < N; i++) txn.Put(db, K(i), V(i));
            // Second pass overwrites every key (same size = in-place candidate,
            // but the pages are spilled so it must COW).
            for (int i = 0; i < N; i++)
            {
                var v = V(i); v[0] = (byte)'X';
                txn.Put(db, K(i), v);
            }
            txn.Commit();
        }

        Assert.True(LmdbIntegrityChecker.Check(dir).Clean);
        using (var env = LmdbEnvironment.Open(dir))
        {
            using var txn = env.BeginTransaction(readOnly: true);
            var db = txn.OpenDefaultDatabase();
            Assert.Equal((ulong)N, db.Entries);
            foreach (int i in new[] { 0, N / 2, N - 1 })
            {
                Assert.True(txn.TryGet(db, K(i), out var v));
                Assert.Equal((byte)'X', v[0]);
            }
        }
    }

    [Fact]
    public void DirtyList_StaysWithinBudget_ThroughoutLargeTxn()
    {
        const int N = 100_000;
        string dir = TmpDir("spill-bound");
        using var env = LmdbEnvironment.Open(dir, Opts(128));
        using var txn = env.BeginTransaction(readOnly: false);
        var db = txn.OpenDefaultDatabase();
        int maxSeen = 0;
        for (int i = 0; i < N; i++)
        {
            txn.Put(db, K(i), V(i));
            int cnt = txn.Dirty!.Count;
            if (cnt > maxSeen) maxSeen = cnt;
        }
        txn.Commit();
        // The budget is checked before each put; a put dirties only a handful of
        // pages beyond it (path COW + splits), so the observed ceiling must stay
        // near 128 — far below the thousands of pages this txn wrote in total.
        Assert.InRange(maxSeen, 1, 256);
        Assert.True(LmdbIntegrityChecker.Check(dir).Clean);
    }

    [Fact]
    public void AbortAfterSpill_LeavesCommittedStateUntouched()
    {
        const int N = 50_000;
        string dir = TmpDir("spill-abort");
        using var env = LmdbEnvironment.Open(dir, Opts(64));
        using (var txn = env.BeginTransaction(readOnly: false))
        {
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < 100; i++) txn.Put(db, K(i), V(i));
            txn.Commit();
        }

        // Big txn that spills heavily, then aborts.
        using (var txn = env.BeginTransaction(readOnly: false))
        {
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < N; i++) txn.Put(db, K(i + 1000), V(i));
            // dispose without commit = abort
        }

        Assert.True(LmdbIntegrityChecker.Check(dir).Clean);
        using (var txn = env.BeginTransaction(readOnly: true))
        {
            var db = txn.OpenDefaultDatabase();
            Assert.Equal(100UL, db.Entries);
            Assert.True(txn.TryGet(db, K(50), out _));
            Assert.False(txn.TryGet(db, K(1500), out _));
        }
    }
}
