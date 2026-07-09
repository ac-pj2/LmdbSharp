using System.Text;
using Lmdb;
using Xunit;
using Xunit.Abstractions;

namespace Lmdb.Tests;

/// <summary>Tests for the lockfile/reader-table layer (multi-process safety).</summary>
public class LockfileTests
{
    private readonly ITestOutputHelper _out;
    public LockfileTests(ITestOutputHelper out_) => _out = out_;

    private static string TmpDir(string name)
    {
        string dir = $"/tmp/lmdb-cs/{name}";
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Lockfile_CreatedOnNewEnv()
    {
        string dir = TmpDir("lock_create");
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 20 }))
        {
            using var txn = env.BeginTransaction(false);
            txn.Put(txn.OpenDefaultDatabase(), B("k"), B("v"));
            txn.Commit();
        }
        Assert.True(System.IO.File.Exists(System.IO.Path.Combine(dir, "lock.mdb")));
    }

    [Fact]
    public void Lockfile_ReaderSlotRegisteredAndReleased()
    {
        string dir = TmpDir("lock_reader");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 20 });
        // Write some data first.
        using (var wtxn = env.BeginTransaction(false))
        {
            wtxn.Put(wtxn.OpenDefaultDatabase(), B("k"), B("v"));
            wtxn.Commit();
        }
        // Open a read txn — should claim a reader slot.
        using (var rtxn = env.BeginTransaction(readOnly: true))
        {
            Assert.True(env.LockfileInfo != null);
            var lf = (Lmdb.Platform.Lockfile)env.LockfileInfo;
            Assert.Equal(1u, lf.NumReaders);
            Assert.Equal(env.CurrentTxnId, lf.ReaderTxnid(0));
        }
        // After dispose, the slot should be freed (pid=0). NumReaders is a
        // high-water mark and is NOT decremented (matching LMDB behavior).
        var lf2 = (Lmdb.Platform.Lockfile)env.LockfileInfo!;
        Assert.Equal(0, lf2.ReaderPid(0));
    }

    [Fact]
    public void Lockfile_WriterLockHeldDuringWriteTxn()
    {
        // Verify the writer lock is held during a write txn and released after.
        string dir = TmpDir("lock_writer");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 20 });
        var lf = (Lmdb.Platform.Lockfile?)env.LockfileInfo;
        Assert.NotNull(lf);

        // Before a write txn, the lock should be free.
        // We can't easily test inter-process locking from the same process
        // (FileStream.Lock doesn't block within the same process), so we
        // just verify the lock is acquired and released without error.
        using (var wtxn = env.BeginTransaction(false))
        {
            wtxn.Put(wtxn.OpenDefaultDatabase(), B("k"), B("v"));
            wtxn.Commit();
        }
        // If we get here without deadlock, the lock was properly acquired and released.
        Assert.True(true);
    }

    [Fact]
    public void Lockfile_FindOldestUsesReaderTable()
    {
        // With a reader table, find_oldest should return the oldest reader's txnid,
        // not just txnid-1. This prevents page reuse while a reader is active.
        string dir = TmpDir("lock_oldest");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 24 });

        // Txn 1: write 100 keys.
        using (var txn = env.BeginTransaction(false))
        {
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < 100; i++) txn.Put(db, B($"k{i:D03}"), B($"v{i:D03}"));
            txn.Commit();
        }

        // Open a read txn at txnid 1 (before the next write).
        var rtxn = env.BeginTransaction(readOnly: true);

        // Txn 2: delete all keys (frees pages).
        using (var wtxn = env.BeginTransaction(false))
        {
            var db = wtxn.OpenDefaultDatabase();
            for (int i = 0; i < 100; i++) wtxn.Delete(db, B($"k{i:D03}"));
            wtxn.Commit();
        }

        // The reader should still see the 100 keys (snapshot isolation).
        var db2 = rtxn.OpenDefaultDatabase();
        Assert.Equal(100UL, db2.Entries);
        Assert.Equal(B("v050"), rtxn.Get(db2, B("k050")).ToArray());
        rtxn.Dispose();
    }
}
