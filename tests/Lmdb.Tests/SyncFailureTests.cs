using System.Text;
using Lmdb;
using Xunit;

namespace Lmdb.Tests;

/// <summary>
/// fsync-failure semantics (disk full, EIO). The commit path syncs twice:
/// step 2 (data pages, BEFORE the meta write) and step 4 (the meta page).
///
///   - Failure at the DATA sync: recoverable. The old meta is still the
///     published state; the txn aborts and the environment stays usable.
///   - Failure at the META sync: fatal for writes (C LMDB: MDB_FATAL_ERROR).
///     The new meta is already in the map but its durability is unknown, so
///     further write txns are refused until the environment is reopened.
/// </summary>
public class SyncFailureTests
{
    private static string TmpDir(string name)
    {
        string dir = $"/tmp/lmdb-cs/{name}";
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static LmdbEnvironment Seeded(string dir, int rows)
    {
        var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 64L << 20 });
        using var txn = env.BeginTransaction(readOnly: false);
        var db = txn.OpenDefaultDatabase();
        for (int i = 0; i < rows; i++)
            txn.Put(db, B($"seed{i:D4}"), B($"val{i:D4}"));
        txn.Commit();
        return env;
    }

    /// <summary>Arms the env to throw IOException on the Nth SyncFile call
    /// (1-based) and returns the applied hook for disarming.</summary>
    private static void ArmSyncFailure(LmdbEnvironment env, int failOnNthCall)
    {
        int calls = 0;
        env.SyncFailureHook = () =>
        {
            if (++calls == failOnNthCall)
                throw new IOException("injected fsync failure (disk full)");
        };
    }

    [Fact]
    public void DataSyncFailure_TxnAborts_OldStateIntact_EnvUsable()
    {
        string dir = TmpDir("sync-fail-data");
        using var env = Seeded(dir, 100);

        ArmSyncFailure(env, failOnNthCall: 1);   // step-2 data sync
        var txn = env.BeginTransaction(readOnly: false);
        var db = txn.OpenDefaultDatabase();
        for (int i = 0; i < 500; i++)
            txn.Put(db, B($"new{i:D4}"), B("x"));
        Assert.Throws<IOException>(txn.Commit);
        txn.Dispose();
        env.SyncFailureHook = null;

        // Not fatal: the old meta was never touched.
        using (var txn2 = env.BeginTransaction(readOnly: false))
        {
            var db2 = txn2.OpenDefaultDatabase();
            Assert.Equal(100UL, db2.Entries);            // failed txn left no trace
            Assert.False(txn2.TryGet(db2, B("new0001"), out _));
            txn2.Put(db2, B("after"), B("ok"));
            txn2.Commit();                                // env fully usable
        }

        Assert.True(LmdbIntegrityChecker.Check(dir).Clean);
        using (var txn3 = env.BeginTransaction(readOnly: true))
        {
            var db3 = txn3.OpenDefaultDatabase();
            Assert.Equal(101UL, db3.Entries);
        }
    }

    [Fact]
    public void MetaSyncFailure_PanicsWrites_ReopenRecovers()
    {
        string dir = TmpDir("sync-fail-meta");
        using (var env = Seeded(dir, 100))
        {
            ArmSyncFailure(env, failOnNthCall: 2);   // step-4 meta sync
            var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < 500; i++)
                txn.Put(db, B($"new{i:D4}"), B("x"));
            Assert.Throws<IOException>(txn.Commit);
            txn.Dispose();
            env.SyncFailureHook = null;

            // Fatal for writes: the meta is written but not verifiably durable.
            var ex = Assert.Throws<LmdbException>(() => env.BeginTransaction(readOnly: false));
            Assert.Equal(LmdbErr.Panic, ex.ErrorCode);

            // Reads remain allowed in-process (data pages were synced in step 2).
            using var rtxn = env.BeginTransaction(readOnly: true);
            var rdb = rtxn.OpenDefaultDatabase();
            Assert.Equal(600UL, rdb.Entries);
        }

        // Reopen: whatever state is on disk must be one of the two valid metas.
        Assert.True(LmdbIntegrityChecker.Check(dir).Clean);
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 64L << 20 }))
        {
            using (var txn = env.BeginTransaction(readOnly: true))
            {
                var db = txn.OpenDefaultDatabase();
                Assert.True(db.Entries == 100UL || db.Entries == 600UL,
                    $"reopened state has {db.Entries} entries — neither pre nor post txn");
            }
            // Writes are accepted again after reopen.
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDefaultDatabase();
                txn.Put(db, B("post-reopen"), B("ok"));
                txn.Commit();
            }
        }
        Assert.True(LmdbIntegrityChecker.Check(dir).Clean);
    }

    [Fact]
    public void MetaSyncFailure_SubsequentWriteAttempts_KeepFailingUntilReopen()
    {
        string dir = TmpDir("sync-fail-sticky");
        using var env = Seeded(dir, 10);
        ArmSyncFailure(env, failOnNthCall: 2);
        var txn = env.BeginTransaction(readOnly: false);
        var db = txn.OpenDefaultDatabase();
        txn.Put(db, B("k"), B("v"));
        Assert.Throws<IOException>(txn.Commit);
        txn.Dispose();
        env.SyncFailureHook = null;

        // Panic is sticky: every write-txn attempt is refused.
        for (int i = 0; i < 3; i++)
        {
            var ex = Assert.Throws<LmdbException>(() => env.BeginTransaction(readOnly: false));
            Assert.Equal(LmdbErr.Panic, ex.ErrorCode);
        }
    }
}
