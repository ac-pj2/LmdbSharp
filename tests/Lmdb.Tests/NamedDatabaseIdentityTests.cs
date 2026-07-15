using System.Text;
using Lmdb;
using Xunit;

namespace Lmdb.Tests;

public class NamedDatabaseIdentityTests
{
    [Fact]
    public void Distinct_empty_named_databases_do_not_alias_in_one_transaction()
    {
        var path = $"/tmp/lmdb-cs/named-identity-{Guid.NewGuid():N}";
        Directory.CreateDirectory(path);
        try
        {
            using var environment = LmdbEnvironment.Open(path,
                new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 24, MaxDbs = 8 });
            using (var transaction = environment.BeginTransaction(readOnly: false))
            {
                var first = transaction.OpenDatabase("first", DatabaseFlags.Create);
                var second = transaction.OpenDatabase("second", DatabaseFlags.Create);
                transaction.Put(second, "key"u8, "second-value"u8);
                transaction.Put(first, "key"u8, "first-value"u8);
                transaction.Commit();
            }

            using var read = environment.BeginTransaction(readOnly: true);
            Assert.Equal("first-value", Encoding.UTF8.GetString(
                read.Get(read.OpenDatabase("first"), "key"u8)));
            Assert.Equal("second-value", Encoding.UTF8.GetString(
                read.Get(read.OpenDatabase("second"), "key"u8)));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void Monotonic_allocation_preserves_named_databases_across_restarts()
    {
        var path = $"/tmp/lmdb-cs/monotonic-{Guid.NewGuid():N}";
        Directory.CreateDirectory(path);
        try
        {
            for (var cycle = 0; cycle < 100; cycle++)
            {
                using var environment = LmdbEnvironment.Open(path, new EnvOpenOptions
                {
                    ReadOnly = false,
                    ReuseFreePages = false,
                    MapSize = 1 << 26,
                    MaxDbs = 8
                });
                using var transaction = environment.BeginTransaction(readOnly: false);
                var records = transaction.OpenDatabase("records", DatabaseFlags.Create);
                var sequences = transaction.OpenDatabase("sequences", DatabaseFlags.Create);
                transaction.Put(records, BitConverter.GetBytes(cycle),
                    Encoding.UTF8.GetBytes($"record-{cycle}"));
                transaction.Put(sequences, BitConverter.GetBytes(cycle),
                    Encoding.UTF8.GetBytes($"sequence-{cycle}"));
                transaction.Commit();
            }

            using var reopened = LmdbEnvironment.Open(path);
            using var read = reopened.BeginTransaction();
            var recordsDb = read.OpenDatabase("records");
            var sequencesDb = read.OpenDatabase("sequences");
            Assert.Equal((ulong)100, recordsDb.Entries);
            Assert.Equal((ulong)100, sequencesDb.Entries);
            Assert.Equal("record-99", Encoding.UTF8.GetString(
                read.Get(recordsDb, BitConverter.GetBytes(99))));
            Assert.Equal("sequence-99", Encoding.UTF8.GetString(
                read.Get(sequencesDb, BitConverter.GetBytes(99))));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
