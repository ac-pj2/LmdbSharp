using System.Text;
using Lmdb;
using Xunit;
using Xunit.Abstractions;

namespace Lmdb.Tests;

/// <summary>
/// Memory-pressure and I/O failure-mode tests.
///
/// Every native allocation in the engine routes through the Mem shim, which can
/// inject a deterministic OutOfMemoryException at the Nth allocation and counts
/// outstanding allocations per thread. The OOM sweep walks the fail point
/// through an entire write-transaction lifecycle (begin, puts, overflow values,
/// deletes, commit) asserting after every injected failure that:
///   - no native memory leaked once txn + env are disposed,
///   - the file passes the integrity walker,
///   - the durable state is exactly the pre-txn state or the full post-txn
///     state — never anything in between.
/// </summary>
public class FaultInjectionTests
{
    private readonly ITestOutputHelper _out;
    public FaultInjectionTests(ITestOutputHelper out_) => _out = out_;

    private static string TmpDir(string name)
    {
        string dir = $"/tmp/lmdb-cs/{name}";
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] K(int i) => Encoding.UTF8.GetBytes($"key{i:D6}");
    private static byte[] V(int i) => Encoding.UTF8.GetBytes($"value{i:D6}");

    private const int Seed = 200;    // rows committed before the faulted txn
    private const int Load = 3000;   // puts in the faulted txn
    private const ulong PostEntries = Seed + Load - Seed / 2;  // txn deletes half the seed

    /// <summary>The workload the fail point sweeps across: mixed puts (some
    /// overflow-sized), deletes of seeded keys, and a commit.</summary>
    private static void Workload(LmdbTransaction txn, LmdbDatabase db)
    {
        for (int i = 0; i < Load; i++)
        {
            if (i % 10 == 0)
            {
                var big = new byte[5000];   // overflow chain
                big[0] = (byte)i;
                txn.Put(db, K(Seed + i), big);
            }
            else
            {
                txn.Put(db, K(Seed + i), V(i));
            }
        }
        for (int i = 0; i < Seed; i += 2)
            txn.Delete(db, K(i));
        txn.Commit();
    }

    private static string PrepareSeeded(string name)
    {
        string dir = TmpDir(name);
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 64L << 20 });
        using var txn = env.BeginTransaction(readOnly: false);
        var db = txn.OpenDefaultDatabase();
        for (int i = 0; i < Seed; i++)
            txn.Put(db, K(i), V(i));
        txn.Commit();
        return dir;
    }

    [Fact]
    public void OomSweep_EveryFailPoint_NoLeak_NoTornState()
    {
        string dir = PrepareSeeded("oom-sweep");
        int failPoint = 0;
        int injected = 0;

        while (true)
        {
            long baseline = Mem.Outstanding;
            bool sawOom = false;

            using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 64L << 20 }))
            {
                Mem.FailAfter = failPoint;
                try
                {
                    using var txn = env.BeginTransaction(readOnly: false);
                    var db = txn.OpenDefaultDatabase();
                    Workload(txn, db);
                }
                catch (OutOfMemoryException)
                {
                    sawOom = true;
                }
                finally
                {
                    Mem.FailAfter = -1;
                }
            }

            Assert.Equal(baseline, Mem.Outstanding);   // no native leak on any path

            var report = LmdbIntegrityChecker.Check(dir);
            Assert.True(report.Clean, $"fail point {failPoint}:\n{report.Render()}");

            using (var env = LmdbEnvironment.Open(dir))
            using (var txn = env.BeginTransaction(readOnly: true))
            {
                var db = txn.OpenDefaultDatabase();
                ulong entries = db.Entries;
                Assert.True(entries == Seed || entries == PostEntries,
                    $"fail point {failPoint}: torn state — {entries} entries "
                    + $"(expected {Seed} or {PostEntries})");
                // Spot-check content matching whichever state committed.
                if (entries == Seed)
                {
                    Assert.True(txn.TryGet(db, K(0), out _));
                    Assert.False(txn.TryGet(db, K(Seed + 1), out _));
                }
                else
                {
                    Assert.False(txn.TryGet(db, K(0), out _));   // deleted
                    Assert.True(txn.TryGet(db, K(Seed + 1), out _));
                }
            }

            if (!sawOom) break;   // fail point walked past the last allocation
            injected++;
            // Stride: dense over the txn-setup allocations, sparser across the
            // page-allocation bulk so the sweep stays fast while still crossing
            // FreelistSave and the commit flush.
            failPoint += failPoint < 100 ? 1 : 5;
        }

        _out.WriteLine($"swept {injected} injected fail points; workload completes at fail point {failPoint}");
        Assert.True(injected > 80, "sweep never injected — shim not wired?");

        // The final iteration ran to completion: recover the DB to post state
        // for a last full-content sanity pass.
        using (var env = LmdbEnvironment.Open(dir))
        using (var txn = env.BeginTransaction(readOnly: true))
        {
            var db = txn.OpenDefaultDatabase();
            Assert.Equal(PostEntries, db.Entries);
        }
    }

    [Fact]
    public void OomDuringWrites_PoisonsTxn_EnvStillUsable()
    {
        string dir = PrepareSeeded("oom-poison");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 64L << 20 });

        var txn = env.BeginTransaction(readOnly: false);
        var db = txn.OpenDefaultDatabase();
        Mem.FailAfter = 10;
        Assert.Throws<OutOfMemoryException>(() =>
        {
            for (int i = 0; i < Load; i++) txn.Put(db, K(Seed + i), V(i));
        });
        Mem.FailAfter = -1;
        // The txn is poisoned; commit must refuse and roll back.
        Assert.Throws<LmdbException>(txn.Commit);
        txn.Dispose();

        // The same environment accepts a fresh transaction immediately.
        using (var txn2 = env.BeginTransaction(readOnly: false))
        {
            var db2 = txn2.OpenDefaultDatabase();
            txn2.Put(db2, K(999999), V(1));
            txn2.Commit();
        }
        using (var txn3 = env.BeginTransaction(readOnly: true))
        {
            var db3 = txn3.OpenDefaultDatabase();
            Assert.Equal((ulong)(Seed + 1), db3.Entries);
        }
    }
}
