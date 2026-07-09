using System.Text;
using Lmdb;
using Xunit;
using Xunit.Abstractions;

namespace Lmdb.Tests;

/// <summary>Tests for mdb_drop, env_copy, default-DB flags, and nested transactions.</summary>
public class AdvancedFeatureTests
{
    private readonly ITestOutputHelper _out;
    public AdvancedFeatureTests(ITestOutputHelper out_) => _out = out_;

    private static string TmpDir(string name)
    {
        string dir = $"/tmp/lmdb-cs/{name}";
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Drop_EmptiesDatabase()
    {
        string dir = TmpDir("drop_empty");
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 20 }))
        {
            using (var txn = env.BeginTransaction(false))
            {
                var db = txn.OpenDefaultDatabase();
                for (int i = 0; i < 100; i++) txn.Put(db, B($"k{i:D03}"), B($"v{i:D03}"));
                txn.Commit();
            }
            using (var txn = env.BeginTransaction(false))
            {
                var db = txn.OpenDefaultDatabase();
                txn.Drop(db, delete: false);
                txn.Commit();
            }
            using (var txn = env.BeginTransaction())
            {
                var db = txn.OpenDefaultDatabase();
                Assert.Equal(0UL, db.Entries);
                Assert.False(txn.TryGet(db, B("k000"), out _));
            }
        }
    }

    [Fact]
    public void Drop_PythonReadsEmptyDb()
    {
        string dir = TmpDir("drop_pyread");
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 20 }))
        {
            using (var txn = env.BeginTransaction(false))
            {
                var db = txn.OpenDefaultDatabase();
                for (int i = 0; i < 50; i++) txn.Put(db, B($"k{i:D03}"), B($"v{i:D03}"));
                txn.Commit();
            }
            using (var txn = env.BeginTransaction(false))
            {
                txn.Drop(txn.OpenDefaultDatabase(), delete: false);
                txn.Commit();
            }
        }
        var psi = new System.Diagnostics.ProcessStartInfo("python3")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("import lmdb;e=lmdb.open('" + dir + "');t=e.begin();assert sum(1 for _ in t.cursor())==0;t.abort();e.close();print('OK')");
        using var p = System.Diagnostics.Process.Start(psi)!;
        string stderr = p.StandardError.ReadToEnd(); p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr);
    }

    [Fact]
    public void EnvCopy_PreservesData()
    {
        string src = TmpDir("copy_src");
        string dst = TmpDir("copy_dst");
        using (var env = LmdbEnvironment.Open(src, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 20 }))
        {
            using (var txn = env.BeginTransaction(false))
            {
                var db = txn.OpenDefaultDatabase();
                for (int i = 0; i < 500; i++) txn.Put(db, B($"k{i:D04}"), B($"v{i:D04}"));
                txn.Commit();
            }
            env.Copy(dst);
        }
        using (var env = LmdbEnvironment.Open(dst))
        {
            using var txn = env.BeginTransaction();
            var db = txn.OpenDefaultDatabase();
            Assert.Equal(500UL, db.Entries);
            Assert.Equal(B("v0123"), txn.Get(db, B("k0123")).ToArray());
        }
    }

    [Fact]
    public void EnvCopy_PythonReadsCopy()
    {
        string src = TmpDir("copy_py_src");
        string dst = TmpDir("copy_py_dst");
        if (System.IO.Directory.Exists(dst)) System.IO.Directory.Delete(dst, true);
        using (var env = LmdbEnvironment.Open(src, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 20 }))
        {
            using (var txn = env.BeginTransaction(false))
            {
                var db = txn.OpenDefaultDatabase();
                for (int i = 0; i < 100; i++) txn.Put(db, B($"k{i:D03}"), B($"v{i:D03}"));
                txn.Commit();
            }
            env.Copy(dst);
        }
        var psi = new System.Diagnostics.ProcessStartInfo("python3")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("import lmdb;e=lmdb.open('" + dst + "');t=e.begin();c=t.cursor();n=sum(1 for _ in c);assert n==100,f'n={n}';assert t.get(b'k050')==b'v050';t.abort();e.close();print('OK',n)");
        using var p = System.Diagnostics.Process.Start(psi)!;
        string stderr = p.StandardError.ReadToEnd(); p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr);
    }

    [Fact]
    public void DefaultDb_DupSortFromCsOnly()
    {
        // Create a DUPSORT main DB entirely from C# — no Python bootstrap.
        string dir = TmpDir("dupsort_default");
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions
        {
            ReadOnly = false, MapSize = 1 << 20, MainDbFlags = DatabaseFlags.DupSort
        }))
        {
            using (var txn = env.BeginTransaction(false))
            {
                var db = txn.OpenDefaultDatabase();
                Assert.True((db.Flags & DatabaseFlags.DupSort) != 0,
                    $"expected DupSort, got {db.Flags}");
                txn.Put(db, B("k"), B("v1"));
                txn.Put(db, B("k"), B("v2"));
                txn.Put(db, B("k"), B("v3"));
                txn.Put(db, B("other"), B("x"));
                txn.Commit();
            }
            using (var txn = env.BeginTransaction())
            {
                var db = txn.OpenDefaultDatabase();
                using var cur = txn.CreateCursor(db);
                var pairs = new System.Collections.Generic.List<(string, string)>();
                Assert.True(cur.TryGet(CursorOp.First, default, out var k, out var v));
                do { pairs.Add((Encoding.UTF8.GetString(k), Encoding.UTF8.GetString(v))); }
                while (cur.TryGet(CursorOp.Next, default, out k, out v));
                Assert.Equal(4, pairs.Count);
                Assert.Equal(("k", "v1"), pairs[0]);
                Assert.Equal(("k", "v3"), pairs[2]);
                Assert.Equal(("other", "x"), pairs[3]);
            }
        }
        // Python verifies.
        var psi = new System.Diagnostics.ProcessStartInfo("python3")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(@"
import lmdb
e=lmdb.open('" + dir + @"')
t=e.begin()
c=t.cursor()
pairs=[(k,v) for k,v in c]
assert len(pairs)==4, pairs
assert pairs[0]==(b'k',b'v1'), pairs[0]
assert pairs[2]==(b'k',b'v3'), pairs[2]
assert pairs[3]==(b'other',b'x'), pairs[3]
t.abort();e.close()
print('OK')
");
        using var p = System.Diagnostics.Process.Start(psi)!;
        string stderr = p.StandardError.ReadToEnd(); p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr);
    }

    [Fact]
    public void NestedTxn_CommitPropagatesToParent()
    {
        string dir = TmpDir("nested_commit");
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 20 }))
        {
            using var parent = env.BeginTransaction(false);
            var db = parent.OpenDefaultDatabase();
            parent.Put(db, B("a"), B("1"));

            using var child = parent.BeginChild();
            child.Put(db, B("b"), B("2"));
            child.Commit();  // b should now be visible to parent

            Assert.Equal(B("2"), parent.Get(db, B("b")).ToArray());
            parent.Commit();
        }
        using (var env = LmdbEnvironment.Open(dir))
        {
            using var txn = env.BeginTransaction();
            var db = txn.OpenDefaultDatabase();
            Assert.Equal(2UL, db.Entries);
            Assert.Equal(B("1"), txn.Get(db, B("a")).ToArray());
            Assert.Equal(B("2"), txn.Get(db, B("b")).ToArray());
        }
    }

    [Fact]
    public void NestedTxn_AbortRollsBackFromParent()
    {
        string dir = TmpDir("nested_abort");
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 20 }))
        {
            using var parent = env.BeginTransaction(false);
            var db = parent.OpenDefaultDatabase();
            parent.Put(db, B("a"), B("1"));

            using var child = parent.BeginChild();
            child.Put(db, B("b"), B("2"));
            child.Abort();  // b should NOT be visible to parent

            Assert.False(parent.TryGet(db, B("b"), out _));
            parent.Commit();
        }
        using (var env = LmdbEnvironment.Open(dir))
        {
            using var txn = env.BeginTransaction();
            var db = txn.OpenDefaultDatabase();
            Assert.Equal(1UL, db.Entries);
            Assert.Equal(B("1"), txn.Get(db, B("a")).ToArray());
        }
    }

    [Fact]
    public void NestedTxn_DeepNesting()
    {
        string dir = TmpDir("nested_deep");
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 20 }))
        {
            using var t1 = env.BeginTransaction(false);
            var db = t1.OpenDefaultDatabase();
            t1.Put(db, B("k1"), B("v1"));

            using var t2 = t1.BeginChild();
            t2.Put(db, B("k2"), B("v2"));

            using var t3 = t2.BeginChild();
            t3.Put(db, B("k3"), B("v3"));
            t3.Commit();
            t2.Commit();
            t1.Commit();
        }
        using (var env = LmdbEnvironment.Open(dir))
        {
            using var txn = env.BeginTransaction();
            var db = txn.OpenDefaultDatabase();
            Assert.Equal(3UL, db.Entries);
            Assert.Equal(B("v1"), txn.Get(db, B("k1")).ToArray());
            Assert.Equal(B("v3"), txn.Get(db, B("k3")).ToArray());
        }
    }
} 
