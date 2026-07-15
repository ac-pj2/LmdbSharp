using System.Text;
using Lmdb;
using Xunit;

namespace Lmdb.Tests;

// Regressions for Drop lifecycle defects (2026-07-15 audit S2-2, S2-12):
// dropped DBs must free every page (including DUPSORT sub-trees), remove the
// record even for empty DBs, tolerate handle reuse without use-after-free,
// and allow same-txn re-creation.
public class DropTests
{
    private static (LmdbEnvironment env, string path) OpenEnv()
    {
        var path = $"/tmp/lmdb-cs/drop-{Guid.NewGuid():N}";
        Directory.CreateDirectory(path);
        var env = LmdbEnvironment.Open(path,
            new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 24, MaxDbs = 8 });
        return (env, path);
    }

    [Fact]
    public void Drop_populated_dupsort_db_frees_every_page_and_removes_record()
    {
        var (env, path) = OpenEnv();
        try
        {
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDatabase("idx", DatabaseFlags.Create | DatabaseFlags.DupSort);
                for (int k = 0; k < 5; k++)
                    for (int v = 0; v < 300; v++)   // forces dup sub-DB conversion
                        txn.Put(db, Encoding.UTF8.GetBytes($"key{k}"),
                                Encoding.UTF8.GetBytes($"value-{v:D4}"));
                var plain = txn.OpenDatabase("plain", DatabaseFlags.Create);
                txn.Put(plain, "keep"u8, "kept"u8);
                txn.Commit();
            }

            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDatabase("idx");
                txn.Drop(db, delete: true);
                txn.Commit();
            }

            using (var read = env.BeginTransaction(readOnly: true))
            {
                Assert.Throws<LmdbException>(() => read.OpenDatabase("idx"));
                var plain = read.OpenDatabase("plain");
                Assert.Equal("kept", Encoding.UTF8.GetString(read.Get(plain, "keep"u8)));
            }

            // Every page of the dropped DB (incl. dup sub-trees) must be
            // reusable: no leaks, walker clean.
            var report = LmdbIntegrityChecker.Check(path);
            Assert.True(report.Clean, report.Render());
            Assert.DoesNotContain(report.Findings, f => f.Code == "leaked-pages");
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }

    [Fact]
    public void Drop_empty_db_removes_record_and_name_can_be_recreated_same_txn()
    {
        var (env, path) = OpenEnv();
        try
        {
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                txn.OpenDatabase("empty", DatabaseFlags.Create);
                txn.Commit();
            }
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDatabase("empty");
                txn.Drop(db, delete: true);
                // Handle reuse after drop must not crash; reads see an empty DB.
                Assert.Equal(0UL, db.Entries);
                // Re-create the same name inside the same transaction.
                var recreated = txn.OpenDatabase("empty", DatabaseFlags.Create);
                txn.Put(recreated, "fresh"u8, "value"u8);
                txn.Commit();
            }
            using (var read = env.BeginTransaction(readOnly: true))
            {
                var db = read.OpenDatabase("empty");
                Assert.Equal(1UL, db.Entries);
                Assert.Equal("value", Encoding.UTF8.GetString(read.Get(db, "fresh"u8)));
            }
            Assert.True(LmdbIntegrityChecker.Check(path).Clean);
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }

    [Fact]
    public void Double_drop_is_safe()
    {
        var (env, path) = OpenEnv();
        try
        {
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDatabase("victim", DatabaseFlags.Create);
                txn.Put(db, "a"u8, "b"u8);
                txn.Commit();
            }
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDatabase("victim");
                txn.Drop(db, delete: true);
                txn.Drop(db, delete: true);   // second drop: no crash, no corruption
                txn.Commit();
            }
            Assert.True(LmdbIntegrityChecker.Check(path).Clean);
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }
}
