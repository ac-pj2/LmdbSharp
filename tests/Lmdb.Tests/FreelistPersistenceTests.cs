using Lmdb;
using Xunit;

namespace Lmdb.Tests;

// Regressions for freelist persistence (2026-07-15 audit S3-1, S3-2). The
// reusable-page pool must survive process restarts (its records used to be
// deleted at consumption while the unconsumed remainder lived only in memory),
// and the freelist writes themselves must not leak the pages they COW.
public class FreelistPersistenceTests
{
    private static EnvOpenOptions Opts() => new()
    {
        ReadOnly = false,
        MapSize = 1 << 24,
        MaxDbs = 4,
        ReuseFreePages = true,
    };

    private static void Churn(LmdbEnvironment env, int rounds)
    {
        for (int i = 0; i < rounds; i++)
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            txn.Put(db, "big-a"u8, new byte[4000]);   // overflow chain
            txn.Put(db, "big-b"u8, new byte[3000]);
            txn.Put(db, System.Text.Encoding.UTF8.GetBytes($"k{i % 10}"), new byte[120]);
            txn.Commit();
        }
    }

    // S3-1: the pool remainder must survive reopen. Before the fix every
    // restart permanently leaked the in-memory remainder and the file grew
    // without bound (the kill soak hit map-full).
    [Fact]
    public void Freed_pages_survive_environment_reopen_without_growth()
    {
        var path = $"/tmp/lmdb-cs/freelist-persist-{Guid.NewGuid():N}";
        Directory.CreateDirectory(path);
        try
        {
            // Each cycle: fill 150 large records, delete them all (≈300 pages
            // into the freelist), reopen. A lost pool forces every next cycle
            // onto fresh pages and the file grows by the working-set each time.
            ulong first = 0, final = 0;
            for (int cycle = 0; cycle < 4; cycle++)
            {
                using var env = LmdbEnvironment.Open(path, Opts());
                using (var txn = env.BeginTransaction(readOnly: false))
                {
                    var db = txn.OpenDefaultDatabase();
                    for (int i = 0; i < 150; i++)
                        txn.Put(db, System.Text.Encoding.UTF8.GetBytes($"rec-{i:D3}"), new byte[4000]);
                    txn.Commit();
                }
                using (var txn = env.BeginTransaction(readOnly: false))
                {
                    var db = txn.OpenDefaultDatabase();
                    for (int i = 0; i < 150; i++)
                        txn.Delete(db, System.Text.Encoding.UTF8.GetBytes($"rec-{i:D3}"));
                    txn.Commit();
                }
                // One more commit so the deletion's freelist record becomes
                // consumable and lands in the pool before the reopen.
                Churn(env, 2);
                if (cycle == 0) first = env.Info.LastPgno;
                final = env.Info.LastPgno;
            }

            Assert.True(final <= first + first / 4,
                $"file grew from {first} to {final} pages across reopen cycles — " +
                "the reusable pool is being lost on restart");
        }
        finally { Directory.Delete(path, true); }
    }

    // A pool remainder large enough to need overflow pages must still converge:
    // rewriting the record frees its own previous chain, so a naive save loop
    // grows freePgs on every iteration and dies with "did not stabilize"
    // (found by the P3 soak under concurrent hierarchy writes).
    [Fact]
    public void Overflow_sized_pool_record_commits_converge()
    {
        var path = $"/tmp/lmdb-cs/freelist-bigpool-{Guid.NewGuid():N}";
        Directory.CreateDirectory(path);
        try
        {
            using var env = LmdbEnvironment.Open(path, Opts());
            // Build a pool of ~800 pages: fill then mass-delete.
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDefaultDatabase();
                for (int i = 0; i < 400; i++)
                    txn.Put(db, System.Text.Encoding.UTF8.GetBytes($"fill-{i:D4}"), new byte[4000]);
                txn.Commit();
            }
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDefaultDatabase();
                for (int i = 0; i < 400; i++)
                    txn.Delete(db, System.Text.Encoding.UTF8.GetBytes($"fill-{i:D4}"));
                txn.Commit();
            }
            // Every subsequent commit must keep persisting the big remainder
            // (key-0 record on overflow pages) without diverging.
            for (int i = 0; i < 25; i++)
                Churn(env, 1);
            var report = LmdbIntegrityChecker.Check(path);
            Assert.True(report.Clean, report.Render());
            Assert.DoesNotContain(report.Findings, f => f.Code == "leaked-pages");
        }
        finally { Directory.Delete(path, true); }
    }

    // S3-1 + S3-2: at steady state every page is either reachable or in the
    // freelist — nothing may leak, including the pages the freelist write
    // itself COWs each commit.
    [Fact]
    public void No_page_is_leaked_after_sustained_churn()
    {
        var path = $"/tmp/lmdb-cs/freelist-zeroleak-{Guid.NewGuid():N}";
        Directory.CreateDirectory(path);
        try
        {
            using (var env = LmdbEnvironment.Open(path, Opts()))
            {
                Churn(env, 40);
            }
            var report = LmdbIntegrityChecker.Check(path);
            Assert.True(report.Clean, report.Render());
            var leaks = report.Findings.Where(f => f.Code == "leaked-pages").ToList();
            Assert.True(leaks.Count == 0,
                "pages leaked during normal operation:\n" + string.Join("\n", leaks));
        }
        finally { Directory.Delete(path, true); }
    }
}
