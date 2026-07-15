using System.Text;
using Lmdb;
using Xunit;

namespace Lmdb.Tests;

// Regressions for commit/transaction-lifecycle defects from the 2026-07-15
// audit (S2-3, S2-5, S2-6 — docs/CODE-AUDIT-2026-07-15.md).
public class CommitSafetyTests
{
    private static string NewEnvPath()
    {
        var path = $"/tmp/lmdb-cs/commit-safety-{Guid.NewGuid():N}";
        Directory.CreateDirectory(path);
        return path;
    }

    // S2-5: creating a named DB must be committable on its own.
    [Fact]
    public void Creating_named_database_with_no_other_writes_persists()
    {
        var path = NewEnvPath();
        try
        {
            using (var env = LmdbEnvironment.Open(path,
                new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 22, MaxDbs = 4 }))
            {
                using var txn = env.BeginTransaction(readOnly: false);
                txn.OpenDatabase("created-empty", DatabaseFlags.Create);
                txn.Commit();
            }
            using (var env = LmdbEnvironment.Open(path,
                new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 22, MaxDbs = 4 }))
            {
                using var read = env.BeginTransaction(readOnly: true);
                // Must not throw NotFound.
                var db = read.OpenDatabase("created-empty");
                Assert.Equal(0UL, db.Entries);
            }
        }
        finally { Directory.Delete(path, true); }
    }

    // S2-6: an operation that throws part-way through a structural mutation
    // must poison the transaction — committing it would persist a half-applied
    // change (here: the old value was deleted before MapFull aborted the
    // re-insert; a silent commit loses the key).
    [Fact]
    public void Failed_update_poisons_transaction_instead_of_committing_data_loss()
    {
        var path = NewEnvPath();
        try
        {
            var original = new byte[100];
            Array.Fill(original, (byte)0xAA);
            using (var env = LmdbEnvironment.Open(path,
                new EnvOpenOptions { ReadOnly = false, MapSize = 64 * 1024 }))
            {
                using (var txn = env.BeginTransaction(readOnly: false))
                {
                    txn.Put(txn.OpenDefaultDatabase(), "victim"u8, original);
                    txn.Commit();
                }

                using (var txn = env.BeginTransaction(readOnly: false))
                {
                    var db = txn.OpenDefaultDatabase();
                    // Exhaust the tiny map mid-update: the old node is deleted,
                    // then the overflow allocation for the new value throws.
                    var ex = Assert.ThrowsAny<LmdbException>(() =>
                    {
                        // Varying sizes defeat the same-size in-place overwrite,
                        // so every update reallocates until the map fills.
                        for (int i = 0; i < 64; i++)
                            txn.Put(db, "victim"u8, new byte[5000 + i * 16]);
                    });
                    Assert.Equal(LmdbErr.MapFull, ex.ErrorCode);

                    // The txn is poisoned: commit must refuse, not persist.
                    var commitEx = Assert.Throws<LmdbException>(txn.Commit);
                    Assert.Equal(LmdbErr.BadTxn, commitEx.ErrorCode);
                }

                using (var read = env.BeginTransaction(readOnly: true))
                {
                    Assert.True(read.TryGet(read.OpenDefaultDatabase(), "victim"u8, out var v),
                        "half-applied update was committed: key lost");
                    Assert.Equal(original, v.ToArray());
                }
            }
        }
        finally { Directory.Delete(path, true); }
    }

    // S2-4: a lockfile that cannot be opened must fail the environment open —
    // silently continuing without locking removes the writer mutex and hides
    // live readers from the freelist.
    [Fact]
    public void Unopenable_lockfile_fails_open_unless_nolock_requested()
    {
        var path = NewEnvPath();
        try
        {
            using (var env = LmdbEnvironment.Open(path,
                new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 22 }))
            {
                using var txn = env.BeginTransaction(readOnly: false);
                txn.Put(txn.OpenDefaultDatabase(), "k"u8, "v"u8);
                txn.Commit();
            }
            var lockFile = Path.Combine(path, "lock.mdb");
            File.SetUnixFileMode(lockFile, UnixFileMode.None);
            try
            {
                Assert.Throws<LmdbException>(() => LmdbEnvironment.Open(path,
                    new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 22 }));

                using var nolock = LmdbEnvironment.Open(path,
                    new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 22, NoLock = true });
                using var read = nolock.BeginTransaction(readOnly: true);
                Assert.True(read.TryGet(read.OpenDefaultDatabase(), "k"u8, out _));
            }
            finally
            {
                File.SetUnixFileMode(lockFile,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally { Directory.Delete(path, true); }
    }

    // S2-3: a write-transaction constructor failure after taking the writer
    // lock must release it — otherwise every later writer deadlocks.
    [Fact]
    public void Writer_lock_released_when_txn_constructor_throws()
    {
        var path = NewEnvPath();
        try
        {
            using var env = LmdbEnvironment.Open(path,
                new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 22 });
            // Seed churn so the free-DB has a consumable record, then poison it
            // with a duplicated page ID through the internal free-DB handle.
            for (int i = 0; i < 4; i++)
                using (var txn = env.BeginTransaction(readOnly: false))
                {
                    txn.Put(txn.OpenDefaultDatabase(), "k"u8, new byte[64]);
                    txn.Commit();
                }
            ulong poisonKey = env.CurrentTxnId;   // survives this commit's cleanup,
                                                  // consumable by the next txn
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var freeDb = txn.OpenFreeDatabase();
                var key = BitConverter.GetBytes(poisonKey);
                var idl = new byte[24];
                idl[0] = 2;                       // count = 2
                idl[8] = 3; idl[16] = 3;          // page 3 listed twice
                txn.Put(freeDb, key, idl);
                txn.Commit();
            }

            // LoadPgHead in the next write txn hits the duplicate assert and the
            // constructor throws — with the lock leaked, the retry deadlocks.
            Assert.Throws<LmdbException>(() => env.BeginTransaction(readOnly: false));

            var retry = Task.Run(() =>
            {
                try { using var t = env.BeginTransaction(readOnly: false); t.Abort(); return true; }
                catch (LmdbException) { return true; }   // corrupted again is fine — it didn't hang
            });
            Assert.True(retry.Wait(TimeSpan.FromSeconds(5)),
                "second write txn blocked forever: writer lock leaked by the failed constructor");
        }
        finally { Directory.Delete(path, true); }
    }
}
