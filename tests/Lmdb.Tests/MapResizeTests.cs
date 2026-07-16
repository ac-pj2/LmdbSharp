using System.Text;
using Lmdb;
using Xunit;

namespace Lmdb.Tests;

/// <summary>
/// Cross-process map growth (MDB_MAP_RESIZED). Two LmdbEnvironment instances
/// on the same path have independent mmap views, exactly like two processes:
/// when the "other" one grows the map and commits beyond the first one's view,
/// the first must refuse transactions with MapResized (never fault past its
/// view) and recover via SetMapSize(0), which adopts the on-disk size.
/// </summary>
public class MapResizeTests
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

    /// <summary>Open env A small, then grow the file well past A's view with a
    /// second env instance B (its own mmap view — process-equivalent).</summary>
    private static (LmdbEnvironment a, string dir) GrownBehindAsBack(string name)
    {
        string dir = TmpDir(name);
        var a = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 20 });
        using (var txn = a.BeginTransaction(readOnly: false))
        {
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < 50; i++) txn.Put(db, K(i), Val);
            txn.Commit();
        }

        using (var b = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 32L << 20 }))
        using (var txn = b.BeginTransaction(readOnly: false))
        {
            var db = txn.OpenDefaultDatabase();
            for (int i = 50; i < 20_000; i++) txn.Put(db, K(i), Val);   // ~10 MB, far past A's 1 MB view
            txn.Commit();
        }
        return (a, dir);
    }

    [Fact]
    public void ReadTxn_AfterForeignGrowth_ThrowsMapResized()
    {
        var (a, _) = GrownBehindAsBack("resize-read");
        using (a)
        {
            var ex = Assert.Throws<LmdbException>(() => a.BeginTransaction(readOnly: true));
            Assert.Equal(LmdbErr.MapResized, ex.ErrorCode);
        }
    }

    [Fact]
    public void WriteTxn_AfterForeignGrowth_ThrowsMapResized()
    {
        var (a, _) = GrownBehindAsBack("resize-write");
        using (a)
        {
            var ex = Assert.Throws<LmdbException>(() => a.BeginTransaction(readOnly: false));
            Assert.Equal(LmdbErr.MapResized, ex.ErrorCode);
        }
    }

    [Fact]
    public void SetMapSizeZero_AdoptsGrownMap_FullyRecovers()
    {
        var (a, dir) = GrownBehindAsBack("resize-adopt");
        using (a)
        {
            Assert.Throws<LmdbException>(() => a.BeginTransaction(readOnly: true));

            a.SetMapSize(0);   // adopt the on-disk size (C: mdb_env_set_mapsize(env, 0))

            using (var txn = a.BeginTransaction(readOnly: true))
            {
                var db = txn.OpenDefaultDatabase();
                Assert.Equal(20_000UL, db.Entries);
                Assert.True(txn.TryGet(db, K(19_999), out _));   // page far beyond the old view
            }
            using (var txn = a.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDefaultDatabase();
                txn.Put(db, K(999_999), Val);
                txn.Commit();
            }
        }
        Assert.True(LmdbIntegrityChecker.Check(dir).Clean);
    }

    [Fact]
    public void SetMapSize_WithActiveTxn_Refuses()
    {
        string dir = TmpDir("resize-active");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 20 });
        using (var txn = env.BeginTransaction(readOnly: false))
        {
            var db = txn.OpenDefaultDatabase();
            txn.Put(db, K(0), Val);
            txn.Commit();   // committed but NOT yet disposed — still counts as live
            var ex = Assert.Throws<LmdbException>(() => env.SetMapSize(64L << 20));
            Assert.Equal(LmdbErr.BadTxn, ex.ErrorCode);
        }
        // After dispose it succeeds and the env keeps working at the new size.
        env.SetMapSize(64L << 20);
        using (var txn = env.BeginTransaction(readOnly: false))
        {
            var db = txn.OpenDefaultDatabase();
            for (int i = 1; i < 5_000; i++) txn.Put(db, K(i), Val);
            txn.Commit();
        }
        Assert.True(LmdbIntegrityChecker.Check(dir).Clean);
    }

    [Fact]
    public void SetMapSize_NeverShrinksBelowCommittedData()
    {
        string dir = TmpDir("resize-noshrink");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 32L << 20 });
        using (var txn = env.BeginTransaction(readOnly: false))
        {
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < 10_000; i++) txn.Put(db, K(i), Val);   // ~5 MB
            txn.Commit();
        }
        env.SetMapSize(1 << 20);   // request 1 MB — clamped to the committed size
        using (var txn = env.BeginTransaction(readOnly: true))
        {
            var db = txn.OpenDefaultDatabase();
            Assert.Equal(10_000UL, db.Entries);
            Assert.True(txn.TryGet(db, K(9_999), out _));
        }
    }
}
