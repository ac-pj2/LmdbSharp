using System.Text;
using Lmdb;
using Xunit;

namespace Lmdb.Tests;

// Deterministic randomized soak for freed-page reuse. Every committed state is
// verified twice: structurally by the read-only integrity walker (no duplicate
// ownership, no reachable-and-free page, no duplicate freelist IDs) and
// functionally against a shadow model. Aborts, no-write commits, long-lived
// readers and environment reopens are interleaved because those are the paths
// that corrupted the P3 environments.
public class FreelistSoakTests
{
    private const int TxnsPerSeed = 60;

    [Theory]
    [InlineData(1234)]
    [InlineData(987654)]
    [InlineData(20260715)]
    public void Randomized_reuse_workload_stays_structurally_consistent(int seed)
    {
        var rng = new Random(seed);
        var path = $"/tmp/lmdb-cs/freelist-soak-{seed}-{Guid.NewGuid():N}";
        Directory.CreateDirectory(path);
        string[] dbNames = { "", "alpha", "beta" };
        var model = new Dictionary<(string db, string key), byte[]>();

        LmdbEnvironment Open() => LmdbEnvironment.Open(path, new EnvOpenOptions
        {
            ReadOnly = false,
            MapSize = 1 << 26,
            MaxDbs = 8,
            ReuseFreePages = true,
        });

        var env = Open();
        LmdbTransaction? heldReader = null;
        try
        {
            using (var init = env.BeginTransaction(readOnly: false))
            {
                init.OpenDatabase("alpha", DatabaseFlags.Create);
                init.OpenDatabase("beta", DatabaseFlags.Create);
                init.Put(init.OpenDefaultDatabase(), "init"u8, "init"u8);
                init.Commit();
            }
            model[("", "init")] = "init"u8.ToArray();

            for (int t = 0; t < TxnsPerSeed; t++)
            {
                // Occasionally hold or release a read transaction across writers
                // so oldest-reader gating actually participates.
                if (heldReader == null && rng.Next(5) == 0)
                    heldReader = env.BeginTransaction(readOnly: true);
                else if (heldReader != null && rng.Next(3) == 0)
                {
                    heldReader.Dispose();
                    heldReader = null;
                }

                int action = rng.Next(10);
                if (action == 0)
                {
                    // no-write commit
                    using var idle = env.BeginTransaction(readOnly: false);
                    idle.Commit();
                }
                else if (action == 1)
                {
                    // abort after building random changes
                    using var doomed = env.BeginTransaction(readOnly: false);
                    ApplyRandomOps(doomed, rng, dbNames, new Dictionary<(string, string), byte[]?>());
                    doomed.Abort();
                }
                else
                {
                    var pending = new Dictionary<(string, string), byte[]?>();
                    using var txn = env.BeginTransaction(readOnly: false);
                    ApplyRandomOps(txn, rng, dbNames, pending);
                    txn.Commit();
                    foreach (var (k, v) in pending)
                    {
                        if (v == null) model.Remove(k);
                        else model[k] = v;
                    }
                }

                var report = LmdbIntegrityChecker.Check(path);
                Assert.True(report.Clean, $"seed={seed} txn#{t}:\n{report.Render()}");

                // Occasionally reopen the environment (process-restart shape).
                if (rng.Next(12) == 0)
                {
                    heldReader?.Dispose();
                    heldReader = null;
                    env.Dispose();
                    env = Open();
                }
            }

            // Final functional verification against the shadow model.
            using var read = env.BeginTransaction(readOnly: true);
            foreach (var ((dbName, key), expected) in model)
            {
                var db = dbName.Length == 0 ? read.OpenDefaultDatabase() : read.OpenDatabase(dbName);
                Assert.True(read.TryGet(db, Encoding.UTF8.GetBytes(key), out var actual),
                    $"seed={seed}: key '{dbName}/{key}' missing after soak");
                Assert.Equal(expected, actual.ToArray());
            }
        }
        finally
        {
            heldReader?.Dispose();
            env.Dispose();
            Directory.Delete(path, recursive: true);
        }
    }

    private static void ApplyRandomOps(LmdbTransaction txn, Random rng, string[] dbNames,
        Dictionary<(string, string), byte[]?> pending)
    {
        int ops = 1 + rng.Next(6);
        for (int i = 0; i < ops; i++)
        {
            string dbName = dbNames[rng.Next(dbNames.Length)];
            var db = dbName.Length == 0 ? txn.OpenDefaultDatabase() : txn.OpenDatabase(dbName);
            string key = $"k{rng.Next(24)}";
            var keyBytes = Encoding.UTF8.GetBytes(key);

            if (rng.Next(4) == 0)
            {
                txn.Delete(db, keyBytes);
                pending[(dbName, key)] = null;
            }
            else
            {
                // Mix inline and overflow (F_BIGDATA) sizes; overflow allocation
                // is a multi-page draw from the reusable pool.
                int len = rng.Next(3) == 0 ? 2200 + rng.Next(6000) : 1 + rng.Next(120);
                var value = new byte[len];
                rng.NextBytes(value);
                txn.Put(db, keyBytes, value);
                pending[(dbName, key)] = value;
            }
        }
    }
}
