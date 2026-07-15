using System.Text;
using Lmdb;
using Xunit;

namespace Lmdb.Tests;

// Torn-commit simulation (investigation priority 7). A hook aborts the commit
// at a named stage; the environment object is then discarded WITHOUT cleanup
// and the directory is reopened cold, exactly as after a process crash at that
// stage. Requirements:
//   - any stage before the meta write: the previous snapshot is fully intact
//     and the file is walker-clean (partially flushed pages are unreferenced);
//   - after the meta write: the new snapshot is complete and walker-clean.
// Every stage is exercised for a plain workload, a named-DB + overflow
// workload, and a freelist-churn workload (reuse enabled).
public class CrashPointTests
{
    private sealed class SimulatedCrash : Exception { }

    private static readonly string[] Stages =
        { "before-flush", "mid-flush", "after-flush", "after-meta" };

    public static IEnumerable<object[]> StageMatrix()
    {
        foreach (var stage in Stages)
            foreach (var workload in new[] { "plain", "named-overflow", "freelist-churn" })
                yield return new object[] { stage, workload };
    }

    [Theory]
    [MemberData(nameof(StageMatrix))]
    public void Crash_during_commit_preserves_exactly_one_consistent_snapshot(string stage, string workload)
    {
        var path = $"/tmp/lmdb-cs/crash-{stage}-{workload}-{Guid.NewGuid():N}";
        Directory.CreateDirectory(path);
        try
        {
            EnvOpenOptions Opts() => new()
            {
                ReadOnly = false,
                MapSize = 1 << 24,
                MaxDbs = 8,
                ReuseFreePages = true,
            };

            // ---- committed baseline the crash must never damage ----
            var env = LmdbEnvironment.Open(path, Opts());
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var main = txn.OpenDefaultDatabase();
                txn.Put(main, "stable"u8, "stable-value"u8);
                if (workload != "plain")
                {
                    var named = txn.OpenDatabase("records", DatabaseFlags.Create);
                    txn.Put(named, "r1"u8, new byte[3000]);   // overflow chain
                    txn.Put(named, "r2"u8, "v2"u8);
                }
                txn.Commit();
            }
            if (workload == "freelist-churn")
            {
                // Overwrites generate freed pages and consumable free-DB records.
                for (int i = 0; i < 4; i++)
                    using (var txn = env.BeginTransaction(readOnly: false))
                    {
                        txn.Put(txn.OpenDefaultDatabase(), "stable"u8, "stable-value"u8);
                        txn.Commit();
                    }
            }
            ulong baselineTxn = env.CurrentTxnId;

            // ---- the crashing transaction ----
            env.CommitHook = s => { if (s == stage) throw new SimulatedCrash(); };
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var main = txn.OpenDefaultDatabase();
                txn.Put(main, "new-key"u8, new byte[2500]);   // dirty pages + overflow
                txn.Put(main, "stable"u8, "REPLACED"u8);
                if (workload != "plain")
                {
                    var named = txn.OpenDatabase("records", DatabaseFlags.Create);
                    txn.Put(named, "r1"u8, new byte[4000]);
                }
                Assert.Throws<SimulatedCrash>(txn.Commit);
            }
            // Simulate the process dying: drop the env without further cleanup.
            env.CommitHook = null;
            env.Dispose();

            // ---- cold recovery ----
            var report = LmdbIntegrityChecker.Check(path);
            Assert.True(report.Clean, $"{stage}/{workload} walker:\n{report.Render()}");

            using var recovered = LmdbEnvironment.Open(path, Opts());
            bool published = stage == "after-meta";
            Assert.Equal(published ? baselineTxn + 1 : baselineTxn, recovered.CurrentTxnId);

            using var read = recovered.BeginTransaction(readOnly: true);
            var db = read.OpenDefaultDatabase();
            Assert.Equal(published ? "REPLACED" : "stable-value",
                Encoding.UTF8.GetString(read.Get(db, "stable"u8)));
            Assert.Equal(published, read.TryGet(db, "new-key"u8, out _));
            if (workload != "plain")
            {
                var named = read.OpenDatabase("records");
                Assert.Equal(published ? 4000 : 3000, read.Get(named, "r1"u8).Length);
                Assert.Equal("v2", Encoding.UTF8.GetString(read.Get(named, "r2"u8)));
            }

            // ---- the survivor keeps working: full write cycle post-recovery ----
            using (var txn = recovered.BeginTransaction(readOnly: false))
            {
                txn.Put(txn.OpenDefaultDatabase(), "post-crash"u8, "ok"u8);
                txn.Commit();
            }
            var report2 = LmdbIntegrityChecker.Check(path);
            Assert.True(report2.Clean, $"{stage}/{workload} post-recovery walker:\n{report2.Render()}");
        }
        finally { Directory.Delete(path, recursive: true); }
    }
}
