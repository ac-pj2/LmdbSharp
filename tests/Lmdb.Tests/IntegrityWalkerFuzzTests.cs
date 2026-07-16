using Lmdb;
using Xunit;

namespace Lmdb.Tests;

// The integrity walker is the verification oracle for this project — it must
// TERMINATE with a report (never crash, never hang) on arbitrary garbage,
// truncated files, and randomly mutated valid databases.
public class IntegrityWalkerFuzzTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Walker_survives_random_garbage_files(int seed)
    {
        var rng = new Random(seed * 104729);
        var dir = $"/tmp/lmdb-cs/walker-fuzz-{seed}-{Guid.NewGuid():N}";
        Directory.CreateDirectory(dir);
        try
        {
            for (int i = 0; i < 40; i++)
            {
                int len = rng.Next(4) switch
                {
                    0 => rng.Next(64),                 // tiny/truncated
                    1 => 4096 + rng.Next(64),          // one page ± slack
                    _ => rng.Next(20) * 4096,          // page multiples
                };
                var bytes = new byte[len];
                rng.NextBytes(bytes);
                var file = Path.Combine(dir, "data.mdb");
                File.WriteAllBytes(file, bytes);
                var report = LmdbIntegrityChecker.Check(dir);   // must not throw
                Assert.NotNull(report);
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    public void Walker_survives_mutations_of_a_valid_database(int seed)
    {
        var rng = new Random(seed * 7919);
        var dir = $"/tmp/lmdb-cs/walker-mut-{seed}-{Guid.NewGuid():N}";
        Directory.CreateDirectory(dir);
        try
        {
            using (var env = LmdbEnvironment.Open(dir,
                new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 22, MaxDbs = 4 }))
            {
                using var txn = env.BeginTransaction(readOnly: false);
                var db = txn.OpenDefaultDatabase();
                var named = txn.OpenDatabase("n", DatabaseFlags.Create | DatabaseFlags.DupSort);
                for (int i = 0; i < 40; i++)
                {
                    txn.Put(db, System.Text.Encoding.UTF8.GetBytes($"k{i:D3}"),
                        new byte[50 + i * 60]);   // mix inline + overflow
                    txn.Put(named, "d"u8, System.Text.Encoding.UTF8.GetBytes($"v{i:D3}"));
                }
                txn.Commit();
            }
            var pristine = File.ReadAllBytes(Path.Combine(dir, "data.mdb"));

            for (int round = 0; round < 60; round++)
            {
                var mutated = (byte[])pristine.Clone();
                // Flip 1-16 random bytes anywhere in the file.
                int flips = 1 + rng.Next(16);
                for (int f = 0; f < flips; f++)
                    mutated[rng.Next(mutated.Length)] ^= (byte)(1 + rng.Next(255));
                File.WriteAllBytes(Path.Combine(dir, "data.mdb"), mutated);
                var report = LmdbIntegrityChecker.Check(dir);   // must not throw/hang
                Assert.NotNull(report);
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
