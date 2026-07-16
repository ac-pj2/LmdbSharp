using System.Text;
using Lmdb;
using Xunit;

namespace Lmdb.Tests;

/// <summary>
/// Regression tests for large single-transaction workloads.
///
/// The dirty-page list (Id2l) was created with capacity 1024 and grew only in
/// Append — Insert (the path AllocPage actually uses) wrote past the buffer, so
/// any write transaction dirtying more than ~1024 pages crashed with
/// IndexOutOfRangeException. Also covers the equal-size in-place value overwrite
/// and the append fast path added in the same perf pass.
/// </summary>
public class LargeTxnRegressionTests
{
    private static string TmpDir(string name)
    {
        string dir = $"/tmp/lmdb-cs/{name}";
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void SingleTxn_DirtyingManyThousandsOfPages_CommitsAndReadsBack()
    {
        // 200k sequential keys dirty ~2500+ leaf pages in one txn — far past the
        // old 1024-entry dirty-list capacity.
        const int N = 200_000;
        string dir = TmpDir("large-txn");
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 256L << 20 }))
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < N; i++)
                txn.Put(db, Encoding.UTF8.GetBytes($"key{i:00000000}"),
                            Encoding.UTF8.GetBytes($"value{i:00000000}"));
            txn.Commit();
        }

        using (var env = LmdbEnvironment.Open(dir))
        {
            using var txn = env.BeginTransaction(readOnly: true);
            var db = txn.OpenDefaultDatabase();
            Assert.Equal((ulong)N, db.Entries);
            // Spot-check ends and middle.
            foreach (int i in new[] { 0, 1, N / 2, N - 2, N - 1 })
            {
                Assert.True(txn.TryGet(db, Encoding.UTF8.GetBytes($"key{i:00000000}"), out var v));
                Assert.Equal($"value{i:00000000}", Encoding.UTF8.GetString(v));
            }
        }
    }

    [Fact]
    public void RandomOrderLargeTxn_FullContentsSurvive()
    {
        const int N = 60_000;
        string dir = TmpDir("large-txn-rnd");
        var order = new int[N];
        for (int i = 0; i < N; i++) order[i] = i;
        var rng = new Random(1234);
        for (int i = N - 1; i > 0; i--) { int j = rng.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }

        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 256L << 20 }))
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            foreach (int i in order)
                txn.Put(db, Encoding.UTF8.GetBytes($"key{i:00000000}"),
                            Encoding.UTF8.GetBytes($"value{i:00000000}"));
            txn.Commit();
        }

        using (var env = LmdbEnvironment.Open(dir))
        {
            using var txn = env.BeginTransaction(readOnly: true);
            var db = txn.OpenDefaultDatabase();
            Assert.Equal((ulong)N, db.Entries);
            using var cur = txn.CreateCursor(db);
            int n = 0;
            if (cur.TryGet(CursorOp.First, default, out var k, out var v))
            {
                do
                {
                    Assert.Equal($"key{n:00000000}", Encoding.UTF8.GetString(k));
                    Assert.Equal($"value{n:00000000}", Encoding.UTF8.GetString(v));
                    n++;
                } while (cur.TryGet(CursorOp.Next, default, out k, out v));
            }
            Assert.Equal(N, n);
        }
    }

    [Fact]
    public void EqualSizeOverwrite_ReplacesValueInPlace()
    {
        string dir = TmpDir("inplace-overwrite");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 64L << 20 });
        using (var txn = env.BeginTransaction(readOnly: false))
        {
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < 1000; i++)
                txn.Put(db, Encoding.UTF8.GetBytes($"k{i:0000}"), Encoding.UTF8.GetBytes($"old-{i:0000}"));
            txn.Commit();
        }
        using (var txn = env.BeginTransaction(readOnly: false))
        {
            var db = txn.OpenDefaultDatabase();
            // Same-size values take the in-place path; verify all replaced.
            for (int i = 0; i < 1000; i++)
                txn.Put(db, Encoding.UTF8.GetBytes($"k{i:0000}"), Encoding.UTF8.GetBytes($"new-{i:0000}"));
            txn.Commit();
        }
        using (var txn = env.BeginTransaction(readOnly: true))
        {
            var db = txn.OpenDefaultDatabase();
            Assert.Equal(1000UL, db.Entries);
            for (int i = 0; i < 1000; i++)
            {
                Assert.True(txn.TryGet(db, Encoding.UTF8.GetBytes($"k{i:0000}"), out var v));
                Assert.Equal($"new-{i:0000}", Encoding.UTF8.GetString(v));
            }
        }
    }
}
