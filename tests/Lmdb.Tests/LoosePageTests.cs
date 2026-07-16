using System.Text;
using Lmdb;
using Xunit;

namespace Lmdb.Tests;

/// <summary>Loose pages (mdb_page_loose): dirty pages freed within their own
/// transaction are recycled immediately (same pgno and buffer) instead of
/// round-tripping through the free-DB.</summary>
public class LoosePageTests
{
    private static string TmpDir(string name)
    {
        string dir = $"/tmp/lmdb-cs/{name}";
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] K(int i) => Encoding.UTF8.GetBytes($"key{i:D6}");

    [Fact]
    public void FillClearCycles_InOneTxn_ReuseLoosePages()
    {
        // Each cycle dirties ~90 pages and then deletes everything (tree drop
        // frees them all as loose). Without loose reuse the file grows by a
        // fresh generation of pages per cycle (~450 total); with it, page
        // consumption stays near one generation.
        string dir = TmpDir("loose-cycles");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions
        { ReadOnly = false, MapSize = 256L << 20, ReuseFreePages = false });
        using (var txn = env.BeginTransaction(readOnly: false))
        {
            var db = txn.OpenDefaultDatabase();
            var val = new byte[40];
            for (int cycle = 0; cycle < 5; cycle++)
            {
                for (int i = 0; i < 5000; i++) txn.Put(db, K(i), val);
                for (int i = 0; i < 5000; i++) Assert.True(txn.Delete(db, K(i)));
                Assert.Equal(0UL, db.Entries);
            }
            for (int i = 0; i < 5000; i++) txn.Put(db, K(i), val);
            txn.Commit();
        }

        Assert.True(LmdbIntegrityChecker.Check(dir).Clean);
        using (var check = LmdbEnvironment.Open(dir))
        {
            // One generation is ~90 pages; without loose reuse this exceeded 500.
            Assert.True(check.Info.LastPgno < 250,
                $"last_pg {check.Info.LastPgno}: loose pages are not being recycled");
            using var txn = check.BeginTransaction(readOnly: true);
            var db = txn.OpenDefaultDatabase();
            Assert.Equal(5000UL, db.Entries);
        }
    }

    [Fact]
    public void LooseReuse_WithSpill_And_Shadowing_StaysCorrect()
    {
        // Loose recycling + spill + parked cursors interacting in one txn.
        string dir = TmpDir("loose-mix");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions
        { ReadOnly = false, MapSize = 256L << 20, MaxDirtyPages = 64 });
        using (var txn = env.BeginTransaction(readOnly: false))
        {
            var db = txn.OpenDefaultDatabase();
            var val = new byte[40];
            for (int i = 0; i < 20_000; i++) txn.Put(db, K(i), val);

            using var parked = txn.CreateCursor(db);
            Assert.True(parked.TryGet(CursorOp.Set, K(10_000), out _, out _));

            // Delete a band below the parked cursor (merges → loose pages),
            // then insert a band above (recycles them).
            for (int i = 0; i < 5_000; i++) Assert.True(txn.Delete(db, K(i)));
            for (int i = 20_000; i < 25_000; i++) txn.Put(db, K(i), val);

            Assert.True(parked.TryGet(CursorOp.Next, default, out var k, out _));
            Assert.Equal(K(10_001), k.ToArray());
            txn.Commit();
        }
        Assert.True(LmdbIntegrityChecker.Check(dir).Clean);
        using (var txn = env.BeginTransaction(readOnly: true))
        {
            var db = txn.OpenDefaultDatabase();
            Assert.Equal(25_000UL - 5_000, db.Entries);
        }
    }
}
