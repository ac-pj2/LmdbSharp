// Regression tests for multi-threaded writers on one environment.
//
// Found via the MissionControl demo: a background timer thread and session
// threads both ran write transactions. Two bugs let them corrupt the tree:
//   1. Lockfile.LockWrite() returned early when _writeLocked was already true,
//      so a second THREAD proceeded with no lock at all (fcntl byte-range
//      locks never conflict within a process either).
//   2. The write-txn constructor snapshotted the meta page / TxnId / NextPgno
//      BEFORE acquiring the writer lock, so even a properly-blocked writer
//      resumed with a stale snapshot and overwrote the previous commit.
// Symptom in the wild: sub-DB entry counts said 1704 while the cursor could
// only reach 11 records, and records deserialized as garbage.
using System.Text;
using Lmdb;
using Xunit;

namespace Lmdb.Tests;

public class ConcurrentWriterTests
{
    private static string TmpDir(string name)
    {
        string dir = $"/tmp/lmdb-cs/{name}";
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] Key(int thread, int i) => Encoding.UTF8.GetBytes($"t{thread:d2}-k{i:d6}");
    private static byte[] Val(int thread, int i) => Encoding.UTF8.GetBytes($"value-{thread}-{i}-{new string('x', 40)}");

    [Fact]
    public void ConcurrentWriters_TwoSubDbs_TreeStaysConsistent()
    {
        const int Threads = 4;
        const int TxnsPerThread = 150;
        string dir = TmpDir("concurrent_writers");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 28, MaxDbs = 8 });

        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var threads = new List<Thread>();
        for (int t = 0; t < Threads; t++)
        {
            int tid = t;
            threads.Add(new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < TxnsPerThread; i++)
                    {
                        using var txn = env.BeginTransaction(false);
                        var a = txn.OpenDatabase("a", DatabaseFlags.Create);
                        var b = txn.OpenDatabase("b", DatabaseFlags.Create);
                        txn.Put(a, Key(tid, i), Val(tid, i));
                        txn.Put(b, Key(tid, i), Val(tid, i));
                        // Mimic the demo's churn: periodically delete older entries.
                        if (i % 7 == 6)
                            txn.Delete(b, Key(tid, i - 3));
                        txn.Commit();
                    }
                }
                catch (Exception e) { errors.Enqueue(e); }
            }));
        }
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.Empty(errors);

        // Verify: every committed record reachable, counts consistent, values intact.
        using var read = env.BeginTransaction(readOnly: true);
        foreach (var (name, expected) in new[]
        {
            ("a", (long)Threads * TxnsPerThread),
            ("b", (long)Threads * (TxnsPerThread - TxnsPerThread / 7)),
        })
        {
            var db = read.OpenDatabase(name);
            long reachable = 0;
            using var cur = read.CreateCursor(db);
            if (cur.TryGet(CursorOp.First, default, out var k, out var v))
            {
                do
                {
                    reachable++;
                    Assert.StartsWith("value-", Encoding.UTF8.GetString(v));
                } while (cur.TryGet(CursorOp.Next, default, out k, out v));
            }

            Assert.Equal(expected, reachable);
            // The stat header must agree with what the tree actually contains —
            // the corruption showed entries=1704 with 11 reachable.
            Assert.Equal(reachable, (long)db.Entries);
        }

        // Every key readable point-wise, with the right value.
        var dbA = read.OpenDatabase("a");
        for (int t = 0; t < Threads; t++)
            for (int i = 0; i < TxnsPerThread; i++)
            {
                Assert.True(read.TryGet(dbA, Key(t, i), out var val), $"missing a/{t}/{i}");
                Assert.Equal(Val(t, i), val.ToArray());
            }
    }

    [Fact]
    public void ConcurrentWriters_TxnIdsNeverCollide()
    {
        const int Threads = 4;
        const int TxnsPerThread = 100;
        string dir = TmpDir("concurrent_txnids");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 26 });

        var ids = new System.Collections.Concurrent.ConcurrentBag<ulong>();
        var threads = new List<Thread>();
        for (int t = 0; t < Threads; t++)
        {
            int tid = t;
            threads.Add(new Thread(() =>
            {
                for (int i = 0; i < TxnsPerThread; i++)
                {
                    using var txn = env.BeginTransaction(false);
                    txn.Put(txn.OpenDefaultDatabase(), Key(tid, i), Val(tid, i));
                    ids.Add(txn.Id);
                    txn.Commit();
                }
            }));
        }
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        // Stale-snapshot writers computed TxnId before holding the lock —
        // duplicate txnids meant two commits fought over the same meta slot.
        Assert.Equal(Threads * TxnsPerThread, ids.Distinct().Count());
    }
}
