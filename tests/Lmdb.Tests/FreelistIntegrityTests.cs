using System.Text;
using Lmdb;
using Xunit;

namespace Lmdb.Tests;

// Regressions for the freed-page-reuse corruption observed in the preserved P3
// environments (docs/STORAGE-INTEGRITY-INVESTIGATION.md).
//
// Root-cause mechanism these tests pin down:
//   LoadPgHead() merges committed free-DB records (key < oldest) into the
//   ENVIRONMENT-level PgHead cache and marks them consumed via Env.PgLast.
//   The on-disk records are only deleted by FreelistSave() during a WRITTEN
//   commit. If the transaction aborts — or commits without writes — the records
//   survive on disk while their pages stay merged in Env.PgHead. The next write
//   transaction merges the same records AGAIN, so PgHead holds duplicate page
//   IDs and AllocPage hands the same physical page to two logical B-tree pages.
//   That is exactly the duplicate-ownership signature in both preserved
//   environments (main root == records root; freelist record with page IDs
//   listed twice).
public class FreelistIntegrityTests
{
    private static string NewEnvPath()
    {
        var path = $"/tmp/lmdb-cs/freelist-integrity-{Guid.NewGuid():N}";
        Directory.CreateDirectory(path);
        return path;
    }

    private static LmdbEnvironment OpenEnv(string path, bool reuseFreePages) =>
        LmdbEnvironment.Open(path, new EnvOpenOptions
        {
            ReadOnly = false,
            MapSize = 1 << 24,
            MaxDbs = 8,
            ReuseFreePages = reuseFreePages,
        });

    private static void Put(LmdbEnvironment env, string key, byte[] value)
    {
        using var txn = env.BeginTransaction(readOnly: false);
        txn.Put(txn.OpenDefaultDatabase(), Encoding.UTF8.GetBytes(key), value);
        txn.Commit();
    }

    private static byte[] Value(byte fill, int len)
    {
        var v = new byte[len];
        Array.Fill(v, fill);
        return v;
    }

    /// <summary>Seed enough churn that a free-DB record becomes consumable
    /// (record key &lt; oldest) by the next write transaction.</summary>
    private static void SeedReusablePages(LmdbEnvironment env)
    {
        Put(env, "k", Value(0xA1, 64));   // txn 1: creates root leaf
        Put(env, "k", Value(0xA2, 64));   // txn 2: COW root -> free-DB record @2
        Put(env, "k", Value(0xA3, 64));   // txn 3: record @2 now < oldest for txn 4
    }

    private static void AssertIntact(string path, LmdbEnvironment env, byte expectedFill)
    {
        // Functional check: the pre-existing key must still read back intact.
        using (var read = env.BeginTransaction(readOnly: true))
        {
            Assert.True(read.TryGet(read.OpenDefaultDatabase(), "k"u8, out var data),
                "key 'k' vanished after freed-page reuse");
            Assert.Equal(Value(expectedFill, 64), data.ToArray());
        }

        // Structural check: no page may be owned twice or be reachable-and-free.
        var report = LmdbIntegrityChecker.Check(path);
        Assert.True(report.Clean,
            "integrity walker found violations:\n" + report.Render());
    }

    [Fact]
    public void Aborted_write_txn_must_not_duplicate_reusable_pages()
    {
        var path = NewEnvPath();
        try
        {
            using var env = OpenEnv(path, reuseFreePages: true);
            SeedReusablePages(env);

            // Txn 4 loads free-DB record @2 into PgHead, then aborts without
            // consuming the loaded pages. The record survives on disk; a correct
            // engine must not re-merge it into a PgHead that still holds its pages.
            using (var doomed = env.BeginTransaction(readOnly: false))
            {
                doomed.Abort();
            }

            // Next write txn re-merges record @2 -> PgHead holds page 2 twice.
            // One Put that both COWs the root and allocates an overflow page
            // draws two pages from PgHead; with the duplicate present, both
            // logical pages receive the same pgno and the flush aliases them.
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                txn.Put(txn.OpenDefaultDatabase(), "big"u8, Value(0xB0, 3000));
                txn.Commit();
            }

            AssertIntact(path, env, 0xA3);
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public void NoWrite_commit_must_not_duplicate_reusable_pages()
    {
        var path = NewEnvPath();
        try
        {
            using var env = OpenEnv(path, reuseFreePages: true);
            SeedReusablePages(env);

            // Txn 4 loads free-DB record @2 into PgHead, then commits WITHOUT
            // writes — Commit() early-returns before FreelistSave, so the
            // consumed record is never deleted.
            using (var idle = env.BeginTransaction(readOnly: false))
            {
                idle.Commit();
            }

            using (var txn = env.BeginTransaction(readOnly: false))
            {
                txn.Put(txn.OpenDefaultDatabase(), "big"u8, Value(0xB1, 3000));
                txn.Commit();
            }

            AssertIntact(path, env, 0xA3);
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public void Monotonic_allocation_baseline_stays_clean_under_same_workload()
    {
        var path = NewEnvPath();
        try
        {
            using var env = OpenEnv(path, reuseFreePages: false);
            SeedReusablePages(env);
            using (var doomed = env.BeginTransaction(readOnly: false))
            {
                doomed.Abort();
            }
            using (var idle = env.BeginTransaction(readOnly: false))
            {
                idle.Commit();
            }
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                txn.Put(txn.OpenDefaultDatabase(), "big"u8, Value(0xB2, 3000));
                txn.Commit();
            }
            AssertIntact(path, env, 0xA3);
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public void Reopening_named_database_in_same_write_txn_must_not_double_free()
    {
        var path = NewEnvPath();
        try
        {
            using var env = OpenEnv(path, reuseFreePages: true);
            using (var setup = env.BeginTransaction(readOnly: false))
            {
                var db = setup.OpenDatabase("records", DatabaseFlags.Create);
                setup.Put(db, "a"u8, Value(0x01, 64));
                setup.Commit();
            }

            // Open the same named DB twice in one write transaction, writing
            // through both handles. The second handle must observe the first
            // handle's mutations; re-reading the stale committed record makes
            // it COW (and free) the old root a second time.
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var h1 = txn.OpenDatabase("records");
                txn.Put(h1, "b"u8, Value(0x02, 64));
                var h2 = txn.OpenDatabase("records");
                txn.Put(h2, "c"u8, Value(0x03, 64));
                txn.Commit();
            }

            using (var read = env.BeginTransaction(readOnly: true))
            {
                var db = read.OpenDatabase("records");
                Assert.Equal(Value(0x01, 64), read.Get(db, "a"u8).ToArray());
                Assert.Equal(Value(0x02, 64), read.Get(db, "b"u8).ToArray());
                Assert.Equal(Value(0x03, 64), read.Get(db, "c"u8).ToArray());
            }
            var report = LmdbIntegrityChecker.Check(path);
            Assert.True(report.Clean, report.Render());
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public void Integrity_walker_reports_clean_on_healthy_reuse_workload()
    {
        var path = NewEnvPath();
        try
        {
            using (var env = OpenEnv(path, reuseFreePages: true))
            {
                for (int i = 0; i < 30; i++)
                    Put(env, $"key-{i % 7}", Value((byte)i, 100 + i * 17));
                using var txn = env.BeginTransaction(readOnly: false);
                var named = txn.OpenDatabase("named", DatabaseFlags.Create);
                txn.Put(named, "n"u8, "v"u8);
                txn.Commit();
            }
            var report = LmdbIntegrityChecker.Check(path);
            Assert.True(report.Clean, report.Render());
        }
        finally { Directory.Delete(path, recursive: true); }
    }
}
