using System.Text;
using Lmdb;
using Xunit;

namespace Lmdb.Tests;

// Regressions for environment lifecycle defects (2026-07-15 audit S2-10, S2-11).
public class EnvLifecycleTests
{
    // S2-10: opening a large-mapsize DB with smaller (default) options must not
    // let the allocator run past the mapped view.
    [Fact]
    public void Reopening_with_smaller_mapsize_option_honors_meta_mapsize()
    {
        var path = $"/tmp/lmdb-cs/mapsize-{Guid.NewGuid():N}";
        Directory.CreateDirectory(path);
        try
        {
            using (var env = LmdbEnvironment.Open(path,
                new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 25 }))   // 32 MB
            {
                using var txn = env.BeginTransaction(readOnly: false);
                var db = txn.OpenDefaultDatabase();
                for (int i = 0; i < 500; i++)
                    txn.Put(db, Encoding.UTF8.GetBytes($"k{i:D4}"), new byte[3000]);
                txn.Commit();
            }

            // Reopen with the tiny default mapsize; writes must keep working
            // inside the meta-recorded 32 MB, not fault past a 1 MB view.
            using (var env = LmdbEnvironment.Open(path,
                new EnvOpenOptions { ReadOnly = false }))
            {
                Assert.True(env.Info.MapSize >= 1 << 25);
                using var txn = env.BeginTransaction(readOnly: false);
                var db = txn.OpenDefaultDatabase();
                for (int i = 0; i < 200; i++)
                    txn.Put(db, Encoding.UTF8.GetBytes($"n{i:D4}"), new byte[3000]);
                txn.Commit();
            }
            Assert.True(LmdbIntegrityChecker.Check(path).Clean);
        }
        finally { Directory.Delete(path, true); }
    }

    // S2-11: Copy must produce a consistent snapshot even while a writer is
    // churning the source.
    [Fact]
    public void Copy_under_concurrent_writes_produces_consistent_snapshot()
    {
        var srcPath = $"/tmp/lmdb-cs/copy-src-{Guid.NewGuid():N}";
        var dstPath = $"/tmp/lmdb-cs/copy-dst-{Guid.NewGuid():N}";
        Directory.CreateDirectory(srcPath);
        try
        {
            using var env = LmdbEnvironment.Open(srcPath,
                new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 24 });
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDefaultDatabase();
                for (int i = 0; i < 100; i++)
                    txn.Put(db, Encoding.UTF8.GetBytes($"k{i:D3}"), new byte[500]);
                txn.Commit();
            }

            using var stop = new CancellationTokenSource();
            var writer = Task.Run(() =>
            {
                for (int i = 0; !stop.IsCancellationRequested && i < 3000; i++)
                {
                    using var txn = env.BeginTransaction(readOnly: false);
                    var db = txn.OpenDefaultDatabase();
                    txn.Put(db, Encoding.UTF8.GetBytes($"k{i % 100:D3}"), new byte[500 + i % 700]);
                    txn.Commit();
                }
            });

            Thread.Sleep(30);   // let the writer get going
            env.Copy(dstPath);
            stop.Cancel();
            writer.Wait();

            // The copy must be structurally sound and fully readable.
            var report = LmdbIntegrityChecker.Check(dstPath);
            Assert.True(report.Clean, report.Render());
            using var copyEnv = LmdbEnvironment.Open(dstPath,
                new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 24 });
            using var read = copyEnv.BeginTransaction(readOnly: true);
            var rdb = read.OpenDefaultDatabase();
            Assert.Equal(100UL, rdb.Entries);
            for (int i = 0; i < 100; i++)
                Assert.True(read.TryGet(rdb, Encoding.UTF8.GetBytes($"k{i:D3}"), out _),
                    $"key k{i:D3} missing from copy");
        }
        finally
        {
            Directory.Delete(srcPath, true);
            if (Directory.Exists(dstPath)) Directory.Delete(dstPath, true);
        }
    }
}
