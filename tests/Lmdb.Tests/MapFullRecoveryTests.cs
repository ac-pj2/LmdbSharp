using System.Text;
using Lmdb;
using Xunit;

namespace Lmdb.Tests;

/// <summary>
/// Map-exhaustion semantics: hitting the map ceiling must surface as a clean
/// MDB_MAP_FULL error that aborts only the offending transaction — committed
/// state stays intact, the environment stays usable, and reopening with a
/// larger MapSize continues where the old map left off. (The map is fixed at
/// open; growth-by-reopen is the documented model, see docs/ROADMAP.md.)
/// </summary>
public class MapFullRecoveryTests
{
    private static string TmpDir(string name)
    {
        string dir = $"/tmp/lmdb-cs/{name}";
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] K(int i) => Encoding.UTF8.GetBytes($"key{i:D6}");
    private static readonly byte[] Val = new byte[512];

    [Fact]
    public void MapFull_IsCleanError_CommittedStateIntact_EnvUsable()
    {
        string dir = TmpDir("mapfull-clean");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 20 });

        using (var txn = env.BeginTransaction(readOnly: false))
        {
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < 100; i++) txn.Put(db, K(i), Val);
            txn.Commit();
        }

        // Fill until the 1 MB map runs out.
        var big = env.BeginTransaction(readOnly: false);
        var bdb = big.OpenDefaultDatabase();
        var ex = Assert.Throws<LmdbException>(() =>
        {
            for (int i = 100; i < 100_000; i++) big.Put(bdb, K(i), Val);
        });
        Assert.Equal(LmdbErr.MapFull, ex.ErrorCode);
        big.Dispose();

        // Committed state intact; env still writable at a smaller scale.
        Assert.True(LmdbIntegrityChecker.Check(dir).Clean);
        using (var txn = env.BeginTransaction(readOnly: false))
        {
            var db = txn.OpenDefaultDatabase();
            Assert.Equal(100UL, db.Entries);
            txn.Put(db, K(999_999), Val);
            txn.Commit();
        }
    }

    [Fact]
    public void ReopenWithLargerMap_ContinuesWhereOldMapEnded()
    {
        string dir = TmpDir("mapfull-grow");
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 20 }))
        {
            var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            var ex = Assert.Throws<LmdbException>(() =>
            {
                for (int i = 0; i < 100_000; i++) txn.Put(db, K(i), Val);
            });
            Assert.Equal(LmdbErr.MapFull, ex.ErrorCode);
            txn.Dispose();

            // Commit what fits.
            using var txn2 = env.BeginTransaction(readOnly: false);
            var db2 = txn2.OpenDefaultDatabase();
            for (int i = 0; i < 500; i++) txn2.Put(db2, K(i), Val);
            txn2.Commit();
        }

        // Reopen 64x larger and finish the load.
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 64L << 20 }))
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            for (int i = 500; i < 20_000; i++) txn.Put(db, K(i), Val);
            txn.Commit();
        }

        Assert.True(LmdbIntegrityChecker.Check(dir).Clean);
        using (var env = LmdbEnvironment.Open(dir))
        using (var txn = env.BeginTransaction(readOnly: true))
        {
            var db = txn.OpenDefaultDatabase();
            Assert.Equal(20_000UL, db.Entries);
            Assert.True(txn.TryGet(db, K(0), out _));
            Assert.True(txn.TryGet(db, K(19_999), out _));
        }
    }

    [Fact]
    public void NestedTxn_DoesNotSpill_ButStaysCorrect()
    {
        // Nested transactions are excluded from spill (their pages may alias
        // parent state) — the dirty list grows past the budget instead of
        // corrupting. Documented limitation in docs/ROADMAP.md.
        string dir = TmpDir("nested-nospill");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions
        { ReadOnly = false, MapSize = 256L << 20, MaxDirtyPages = 64 });

        using (var parent = env.BeginTransaction(readOnly: false))
        {
            var pdb = parent.OpenDefaultDatabase();
            parent.Put(pdb, K(0), Val);

            using (var child = parent.BeginChild())
            {
                var cdb = child.OpenDefaultDatabase();
                for (int i = 1; i <= 30_000; i++)
                    child.Put(cdb, K(i), Val);
                // No spill in a child: dirty list grows well past the 64-page budget.
                Assert.True(child.Dirty!.Count > 64,
                    $"child dirty count {child.Dirty!.Count} — spill unexpectedly ran in a nested txn");
                child.Commit();
            }
            parent.Commit();
        }

        Assert.True(LmdbIntegrityChecker.Check(dir).Clean);
        using (var txn = env.BeginTransaction(readOnly: true))
        {
            var db = txn.OpenDefaultDatabase();
            Assert.Equal(30_001UL, db.Entries);
        }
    }
}
