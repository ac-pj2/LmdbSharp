using System.Text;
using Lmdb;
using Xunit;
using Xunit.Abstractions;

namespace Lmdb.Tests;

/// <summary>
/// Exercises the C#-idiomatic API surface to prove the new convenience methods
/// work end-to-end and feel natural to a .NET developer.
/// </summary>
public class IdiomaticApiTests
{
    private readonly ITestOutputHelper _out;
    public IdiomaticApiTests(ITestOutputHelper out_) => _out = out_;

    private static string TmpDir(string name)
    {
        string dir = $"/tmp/lmdb-idiom/{name}";
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void StringOverloads_PutGetString()
    {
        string dir = TmpDir("strings");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 20, NoLock = true });
        using (var txn = env.BeginWriteTransaction())
        {
            var db = txn.OpenDefaultDatabase();
            txn.PutString(db, "name", "Alice");
            txn.PutString(db, "city", "London");
            txn.Commit();
        }
        using (var txn = env.BeginTransaction())
        {
            var db = txn.OpenDefaultDatabase();
            Assert.Equal("Alice", txn.GetString(db, "name"));
            Assert.Equal("London", txn.GetString(db, "city"));
            Assert.Null(txn.GetString(db, "missing"));
            Assert.True(txn.TryGetString(db, "name", out var val));
            Assert.Equal("Alice", val);
            Assert.False(txn.TryGetString(db, "nope", out _));
        }
    }

    [Fact]
    public void Scan_ForeachAndLinq()
    {
        string dir = TmpDir("scan");
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 20, NoLock = true }))
        using (var txn = env.BeginWriteTransaction())
        {
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < 100; i++) txn.PutString(db, $"k{i:D03}", $"v{i:D03}");
            txn.Commit();
        }

        using var env2 = LmdbEnvironment.Open(dir);
        using var txn2 = env2.BeginTransaction();
        var db2 = txn2.OpenDefaultDatabase();

        // foreach over byte[] pairs
        int count = 0;
        foreach (var (key, data) in txn2.Scan(db2))
        {
            Assert.Equal($"k{count:D03}", Encoding.UTF8.GetString(key));
            count++;
        }
        Assert.Equal(100, count);

        // LINQ over string pairs
        var pairs = txn2.ScanStrings(db2).Take(5).ToList();
        Assert.Equal(5, pairs.Count);
        Assert.Equal(("k000", "v000"), pairs[0]);
        Assert.Equal(("k004", "v004"), pairs[4]);

        // Count via LINQ
        Assert.Equal(100, txn2.ScanStrings(db2).Count());
    }

    [Fact]
    public void WriteScope_AutoCommitsOnSuccess()
    {
        string dir = TmpDir("scope_commit");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 20, NoLock = true });

        env.Write(txn =>
        {
            var db = txn.OpenDefaultDatabase();
            txn.PutString(db, "a", "1");
            txn.PutString(db, "b", "2");
        });  // auto-commits here

        env.Read(txn =>
        {
            var db = txn.OpenDefaultDatabase();
            Assert.Equal("1", txn.GetString(db, "a"));
            Assert.Equal("2", txn.GetString(db, "b"));
        });
    }

    [Fact]
    public void WriteScope_AbortsOnException()
    {
        string dir = TmpDir("scope_abort");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 20, NoLock = true });

        // First write succeeds
        env.Write(txn => txn.PutString(txn.OpenDefaultDatabase(), "keep", "yes"));

        // Second write throws → should abort (no "bad" key persisted)
        Assert.Throws<InvalidOperationException>(() =>
            env.Write(txn =>
            {
                var db = txn.OpenDefaultDatabase();
                txn.PutString(db, "bad", "no");
                throw new InvalidOperationException("boom");
            }));

        env.Read(txn =>
        {
            var db = txn.OpenDefaultDatabase();
            Assert.Equal("yes", txn.GetString(db, "keep"));
            Assert.Null(txn.GetString(db, "bad"));   // rolled back
        });
    }

    [Fact]
    public void MainStat_IsComputedProperty()
    {
        string dir = TmpDir("stat");
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 20, NoLock = true }))
        using (var txn = env.BeginWriteTransaction())
        {
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < 500; i++) txn.PutString(db, $"k{i:D03}", $"v{i:D03}");
            txn.Commit();
        }
        using var env2 = LmdbEnvironment.Open(dir);
        var stat = env2.MainStat;
        _out.WriteLine($"MainStat: entries={stat.Entries} depth={stat.Depth} leafPages={stat.LeafPages}");
        Assert.Equal((ulong)500, stat.Entries);
        Assert.True(stat.PageSize > 0);
        Assert.True(stat.LeafPages > 0);
    }

    [Fact]
    public void TypeNames_DontClashWithBcl()
    {
        // Verify the renamed types compile alongside System.* usings without aliases.
        // This would not compile before the rename (ambiguous with System.Transactions).
        string dir = TmpDir("naming");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 20, NoLock = true });
        using LmdbTransaction txn = env.BeginWriteTransaction();
        LmdbDatabase db = txn.OpenDefaultDatabase();
        using LmdbCursor cur = txn.CreateCursor(db);
        Assert.NotNull(cur);
    }
}
