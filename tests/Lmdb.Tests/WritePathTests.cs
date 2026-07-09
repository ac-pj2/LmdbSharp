using System.Text;
using Lmdb;
using Xunit;
using Xunit.Abstractions;

namespace Lmdb.Tests;

/// <summary>
/// Write-path round-trip tests: the C# port writes databases and reads them back,
/// then confirms real liblmdb (Python) can read the C#-written files — proving
/// write-side binary compatibility.
/// </summary>
public class WritePathTests
{
    private readonly ITestOutputHelper _out;
    public WritePathTests(ITestOutputHelper out_) => _out = out_;

    private static string TmpDir(string name)
    {
        string dir = $"/tmp/lmdb-cs/{name}";
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void SingleKey_RoundTripsWithinCs()
    {
        string dir = TmpDir("single");
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 20 }))
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            txn.Put(db, B("hello"), B("world"));
            txn.Commit();
        }
        using (var env = LmdbEnvironment.Open(dir))
        {
            using var txn = env.BeginTransaction();
            var db = txn.OpenDefaultDatabase();
            Assert.Equal(B("world"), txn.Get(db, B("hello")).ToArray());
        }
    }

    [Fact]
    public void ManyKeys_RoundTripsAndIterates()
    {
        string dir = TmpDir("many");
        const int N = 1000;
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 24 }))
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < N; i++)
                txn.Put(db, B($"key{i:D5}"), B($"val{i:D5}"));
            txn.Commit();
        }
        using (var env = LmdbEnvironment.Open(dir))
        {
            using var txn = env.BeginTransaction();
            var db = txn.OpenDefaultDatabase();
            Assert.Equal((ulong)N, db.Entries);
            Assert.Equal(B("val00123"), txn.Get(db, B("key00123")).ToArray());
            Assert.Equal(B("val00999"), txn.Get(db, B("key00999")).ToArray());

            using var cur = txn.CreateCursor(db);
            int count = 0;
            Assert.True(cur.TryGet(CursorOp.First, default, out var k, out var v));
            do
            {
                Assert.Equal($"key{count:D5}", Encoding.UTF8.GetString(k));
                Assert.Equal($"val{count:D5}", Encoding.UTF8.GetString(v));
                count++;
            } while (cur.TryGet(CursorOp.Next, default, out k, out v));
            Assert.Equal(N, count);
            _out.WriteLine($"iterated {count} entries, depth={db.Depth}, leaves={db.LeafPages}");
        }
    }

    [Fact]
    public void Updates_OverwriteInPlace()
    {
        string dir = TmpDir("update");
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 20 }))
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            txn.Put(db, B("k"), B("old"));
            txn.Put(db, B("k"), B("new value that is longer"));
            txn.Commit();
        }
        using (var env = LmdbEnvironment.Open(dir))
        {
            using var txn = env.BeginTransaction();
            var db = txn.OpenDefaultDatabase();
            Assert.Equal(B("new value that is longer"), txn.Get(db, B("k")).ToArray());
            Assert.Equal(1UL, db.Entries);
        }
    }

    [Fact]
    public void PythonReads_CsWrittenFile()
    {
        string dir = TmpDir("pyread");
        const int N = 500;
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 24 }))
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < N; i++)
                txn.Put(db, B($"k{i:D04}"), B($"v{i:D04}"));
            txn.Commit();
        }
        // Hand the file to real liblmdb via Python and verify every key.
        string script = @"
import lmdb
env = lmdb.open('__DIR__', readonly=True, max_dbs=4)
t = env.begin()
c = t.cursor()
n = 0
for k, v in c:
    i = int(k[1:])
    assert v == b'v' + (b'%04d' % i), (k, v)
    n += 1
assert n == __N__, n
assert t.get(b'k0123') == b'v0123'
t.abort(); env.close()
print('OK', n)
".Replace("__DIR__", dir).Replace("__N__", N.ToString());
        var psi = new System.Diagnostics.ProcessStartInfo("python3")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        using var p = System.Diagnostics.Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"python exited {p.ExitCode}\nstdout={stdout}\nstderr={stderr}");
        Assert.Contains("OK", stdout);
        _out.WriteLine("Python (real liblmdb) read the C#-written file: " + stdout.Trim());
    }

    [Fact]
    public void RandomOrderInserts_RoundTrip()
    {
        string dir = TmpDir("random");
        var rng = new Random(42);
        var order = Enumerable.Range(0, 2000).OrderBy(_ => rng.Next()).ToArray();
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 24 }))
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            foreach (int i in order)
                txn.Put(db, B($"k{i:D06}"), B($"v{i:D06}"));
            txn.Commit();
        }
        using (var env = LmdbEnvironment.Open(dir))
        {
            using var txn = env.BeginTransaction();
            var db = txn.OpenDefaultDatabase();
            Assert.Equal(2000UL, db.Entries);
            for (int i = 0; i < 2000; i += 37)
                Assert.Equal(B($"v{i:D06}"), txn.Get(db, B($"k{i:D06}")).ToArray());
        }
    }

    [Fact]
    public void LargeValues_OverflowPages()
    {
        string dir = TmpDir("overflow");
        var big = new byte[20000];
        for (int i = 0; i < big.Length; i++) big[i] = (byte)(i & 0xFF);
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 24 }))
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            txn.Put(db, B("big"), big);
            txn.Put(db, B("tiny"), B("x"));
            txn.Commit();
        }
        using (var env = LmdbEnvironment.Open(dir))
        {
            using var txn = env.BeginTransaction();
            var db = txn.OpenDefaultDatabase();
            var got = txn.Get(db, B("big"));
            Assert.Equal(20000, got.Length);
            for (int i = 0; i < got.Length; i++) Assert.Equal((byte)(i & 0xFF), got[i]);
            Assert.Equal(B("x"), txn.Get(db, B("tiny")).ToArray());
            Assert.True(db.OverflowPages > 0, $"expected overflow pages, got {db.OverflowPages}");
        }
    }

    [Fact]
    public void Deletes_RemoveKeys()
    {
        string dir = TmpDir("delete");
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 24 }))
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < 500; i++)
                txn.Put(db, B($"k{i:D04}"), B($"v{i:D04}"));
            // delete every other key
            for (int i = 0; i < 500; i += 2)
                Assert.True(txn.Delete(db, B($"k{i:D04}")));
            txn.Commit();
        }
        using (var env = LmdbEnvironment.Open(dir))
        {
            using var txn = env.BeginTransaction();
            var db = txn.OpenDefaultDatabase();
            Assert.Equal(250UL, db.Entries);
            for (int i = 0; i < 500; i += 2)
                Assert.False(txn.TryGet(db, B($"k{i:D04}"), out _), $"k{i:D04} should be deleted");
            for (int i = 1; i < 500; i += 2)
                Assert.Equal(B($"v{i:D04}"), txn.Get(db, B($"k{i:D04}")).ToArray());
        }
    }

    [Fact]
    public void DeepTree_10000Keys()
    {
        string dir = TmpDir("deep");
        const int N = 10000;
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 25 }))
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < N; i++)
                txn.Put(db, B($"key{i:D06}"), B($"value{i:D06}"));
            txn.Commit();
            _out.WriteLine($"depth={db.Depth} branch={db.BranchPages} leaf={db.LeafPages} entries={db.Entries}");
        }
        using (var env = LmdbEnvironment.Open(dir))
        {
            using var txn = env.BeginTransaction();
            var db = txn.OpenDefaultDatabase();
            Assert.Equal((ulong)N, db.Entries);
            Assert.True(db.Depth >= 2, $"expected depth>=2, got {db.Depth}");
            using var cur = txn.CreateCursor(db);
            int count = 0; long prev = -1;
            Assert.True(cur.TryGet(CursorOp.First, default, out var k, out _));
            do { long n = int.Parse(Encoding.UTF8.GetString(k)[3..]); Assert.True(n > prev); prev = n; count++; }
            while (cur.TryGet(CursorOp.Next, default, out k, out _));
            Assert.Equal(N, count);
        }
    }
}
