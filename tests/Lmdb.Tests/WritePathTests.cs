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

    [Fact]
    public void Churn_DeletesFreePagesForReuse()
    {
        // Insert N keys, delete all, then after a 2-txn delay (so the free-DB
        // record becomes old enough to reuse), insert N keys again. The second
        // insert should reuse freed pages rather than doubling the file.
        // (Without a reader table, oldest = txnid - 1, so pages freed by txn N
        //  are reusable in txn N+2.)
        string dir = TmpDir("churn");
        const int N = 2000;
        long pagesAfterFirst, pagesAfterSecond;

        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 24 }))
        {
            // Txn 1: insert N keys.
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDefaultDatabase();
                for (int i = 0; i < N; i++) txn.Put(db, B($"k{i:D05}"), B($"v{i:D05}"));
                txn.Commit();
            }
            pagesAfterFirst = (long)env.Info.LastPgno;

            // Txn 2: delete all keys (freed pages saved to free-DB with key=2).
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDefaultDatabase();
                for (int i = 0; i < N; i++) txn.Delete(db, B($"k{i:D05}"));
                txn.Commit();
            }

            // Txn 3: dummy write to advance txnid (so oldest > 2 in txn 4).
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDefaultDatabase();
                txn.Put(db, B("_"), B("_"));
                txn.Commit();
            }

            // Txn 4: insert N keys again. oldest = 4-1 = 3 > 2, so the free-DB
            // record from txn 2 is consumed and its pages reused.
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDefaultDatabase();
                txn.Delete(db, B("_"));
                for (int i = 0; i < N; i++) txn.Put(db, B($"k{i:D05}"), B($"v{i:D05}"));
                txn.Commit();
            }
            pagesAfterSecond = (long)env.Info.LastPgno;
        }

        _out.WriteLine($"pages: afterFirst={pagesAfterFirst} afterSecond={pagesAfterSecond}");

        // Verify data is correct after churn.
        using (var env = LmdbEnvironment.Open(dir))
        {
            using var txn = env.BeginTransaction();
            var db = txn.OpenDefaultDatabase();
            Assert.Equal((ulong)N, db.Entries);
            Assert.Equal(B("v01234"), txn.Get(db, B("k01234")).ToArray());
        }

        // Without reuse, afterSecond would be ~3x afterFirst (insert + COW of delete + insert).
        // With reuse, the re-insert draws from PgHead instead of fresh pages.
        // We verify afterSecond is well under the no-reuse baseline.
        Assert.True(pagesAfterSecond < pagesAfterFirst * 3,
            $"expected page reuse: afterSecond={pagesAfterSecond} should be < 3*afterFirst={pagesAfterFirst * 3}");
        _out.WriteLine($"page reuse confirmed: {pagesAfterSecond} < {pagesAfterFirst * 3} (no-reuse baseline)");
    }

    [Fact]
    public void Churn_MultipleCycles_PageCountStabilizes()
    {
        // Repeatedly insert + delete the same keys. Without page reuse, the file
        // would grow linearly. With reuse, the page count should stabilize.
        string dir = TmpDir("churn_multi");
        const int N = 1000;
        const int Cycles = 6;
        long[] pageCounts = new long[Cycles + 1];

        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 24 }))
        {
            for (int cycle = 0; cycle < Cycles; cycle++)
            {
                // Insert
                using (var txn = env.BeginTransaction(false))
                {
                    var db = txn.OpenDefaultDatabase();
                    for (int i = 0; i < N; i++) txn.Put(db, B($"k{i:D04}"), B($"v{i:D04}"));
                    txn.Commit();
                }
                // Delete all
                using (var txn = env.BeginTransaction(false))
                {
                    var db = txn.OpenDefaultDatabase();
                    for (int i = 0; i < N; i++) txn.Delete(db, B($"k{i:D04}"));
                    txn.Commit();
                }
                pageCounts[cycle + 1] = (long)env.Info.LastPgno;
            }
        }

        _out.WriteLine("page counts per cycle: " + string.Join(", ", pageCounts));

        // Verify data after all churn.
        using (var env = LmdbEnvironment.Open(dir))
        {
            using var txn = env.BeginTransaction();
            var db = txn.OpenDefaultDatabase();
            Assert.Equal(0UL, db.Entries);   // all deleted in the last cycle
        }

        // The page count should NOT grow linearly. After the initial growth (COW +
        // free-DB setup), it should plateau as freed pages are recycled.
        long finalPages = pageCounts[Cycles];
        long midPages = pageCounts[Cycles / 2];
        Assert.True(finalPages < midPages * 2,
            $"page count should stabilize: final={finalPages}, mid={midPages}");
        Assert.True(finalPages < pageCounts[1] * 4,
            $"page count should not grow unboundedly: final={finalPages}, after1stCycle={pageCounts[1]}");
    }

    [Fact]
    public void Churn_PythonReadsAfterReuse()
    {
        // C# writes, deletes, re-writes with page reuse; Python reads the result.
        string dir = TmpDir("churn_pyread");
        const int N = 1000;
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 24 }))
        {
            using (var txn = env.BeginTransaction(false))
            {
                var db = txn.OpenDefaultDatabase();
                for (int i = 0; i < N; i++) txn.Put(db, B($"k{i:D04}"), B($"old{i:D04}"));
                txn.Commit();
            }
            using (var txn = env.BeginTransaction(false))
            {
                var db = txn.OpenDefaultDatabase();
                for (int i = 0; i < N; i++) txn.Delete(db, B($"k{i:D04}"));
                txn.Commit();
            }
            using (var txn = env.BeginTransaction(false))
            {
                var db = txn.OpenDefaultDatabase();
                for (int i = 0; i < N; i++) txn.Put(db, B($"k{i:D04}"), B($"new{i:D04}"));
                txn.Commit();
            }
        }
        string script = @"
import lmdb
env = lmdb.open('__DIR__', readonly=True)
t = env.begin()
c = t.cursor()
n = 0
for k, v in c:
    i = int(k[1:])
    assert v == b'new' + (b'%04d' % i), (k, v)
    n += 1
assert n == __N__, n
assert t.get(b'k0123') == b'new0123'
t.abort(); env.close()
print('OK', n)
".Replace("__DIR__", dir).Replace("__N__", N.ToString());
        var psi = new System.Diagnostics.ProcessStartInfo("python3")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(script);
        using var p = System.Diagnostics.Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"python exited {p.ExitCode}\nstdout={stdout}\nstderr={stderr}");
        _out.WriteLine("Python read churned DB: " + stdout.Trim());
    }

    [Fact]
    public void Reopen_ExistingDbReusesPages()
    {
        // Open an existing DB (with free-DB records from previous commits), delete
        // some keys, insert new ones. Pages should be reused across env reopens.
        string dir = TmpDir("reopen_reuse");
        const int N = 1000;

        // First env: insert + delete (creates free-DB records).
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 24 }))
        {
            using (var txn = env.BeginTransaction(false))
            {
                var db = txn.OpenDefaultDatabase();
                for (int i = 0; i < N; i++) txn.Put(db, B($"k{i:D04}"), B($"v{i:D04}"));
                txn.Commit();
            }
            using (var txn = env.BeginTransaction(false))
            {
                var db = txn.OpenDefaultDatabase();
                for (int i = 0; i < N; i++) txn.Delete(db, B($"k{i:D04}"));
                txn.Commit();
            }
        }

        long pagesAfter;
        // Second env (reopen): insert keys — should reuse pages freed by the first env.
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 24 }))
        {
            using (var txn = env.BeginTransaction(false))
            {
                var db = txn.OpenDefaultDatabase();
                // First txn in this env: should consume old free-DB records into PgHead.
                // Insert N keys — reuses pages from the free-DB.
                for (int i = 0; i < N; i++) txn.Put(db, B($"k{i:D04}"), B($"v{i:D04}"));
                txn.Commit();
            }
            pagesAfter = (long)env.Info.LastPgno;
        }

        // Verify data.
        using (var env = LmdbEnvironment.Open(dir))
        {
            using var txn = env.BeginTransaction();
            var db = txn.OpenDefaultDatabase();
            Assert.Equal((ulong)N, db.Entries);
            Assert.Equal(B("v0567"), txn.Get(db, B("k0567")).ToArray());
        }
        _out.WriteLine($"pages after reopen+insert: {pagesAfter}");
    }

    [Fact]
    public void Rebalance_DeleteAllCollapsesTree()
    {
        // Insert many keys (multi-page tree), then delete all. The tree should
        // collapse to empty (root = P_INVALID, entries = 0).
        string dir = TmpDir("rebal_delall");
        const int N = 3000;
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 24 }))
        {
            using (var txn = env.BeginTransaction(false))
            {
                var db = txn.OpenDefaultDatabase();
                for (int i = 0; i < N; i++) txn.Put(db, B($"k{i:D05}"), B($"v{i:D05}"));
                txn.Commit();
            }
            using (var txn = env.BeginTransaction(false))
            {
                var db = txn.OpenDefaultDatabase();
                for (int i = 0; i < N; i++) txn.Delete(db, B($"k{i:D05}"));
                txn.Commit();
            }
            using (var txn = env.BeginTransaction())
            {
                var db = txn.OpenDefaultDatabase();
                Assert.Equal(0UL, db.Entries);
                Assert.Equal(ulong.MaxValue, db.Root);   // P_INVALID = empty tree
                Assert.Equal(0, db.Depth);
                _out.WriteLine($"after delete-all: entries={db.Entries} root={db.Root} depth={db.Depth}");
            }
        }
    }

    [Fact]
    public void Rebalance_AlternatingDeletesKeepTreeValid()
    {
        // Delete every other key — triggers node moves and merges as pages shrink.
        // Verify the surviving keys are all readable and correctly ordered.
        string dir = TmpDir("rebal_alt");
        const int N = 4000;
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 25 }))
        {
            using (var txn = env.BeginTransaction(false))
            {
                var db = txn.OpenDefaultDatabase();
                for (int i = 0; i < N; i++) txn.Put(db, B($"k{i:D05}"), B($"v{i:D05}"));
                txn.Commit();
            }
            // Delete even keys.
            using (var txn = env.BeginTransaction(false))
            {
                var db = txn.OpenDefaultDatabase();
                for (int i = 0; i < N; i += 2) txn.Delete(db, B($"k{i:D05}"));
                txn.Commit();
            }
            // Verify.
            using (var txn = env.BeginTransaction())
            {
                var db = txn.OpenDefaultDatabase();
                Assert.Equal((ulong)(N / 2), db.Entries);
                // Even keys gone, odd keys present.
                for (int i = 0; i < N; i += 2)
                    Assert.False(txn.TryGet(db, B($"k{i:D05}"), out _));
                for (int i = 1; i < N; i += 2)
                    Assert.Equal(B($"v{i:D05}"), txn.Get(db, B($"k{i:D05}")).ToArray());
                // Full forward iteration.
                using var cur = txn.CreateCursor(db);
                int count = 0; int expected = 1;
                Assert.True(cur.TryGet(CursorOp.First, default, out var k, out _));
                do
                {
                    int n = int.Parse(Encoding.UTF8.GetString(k)[1..]);
                    Assert.Equal(expected, n);
                    expected += 2; count++;
                } while (cur.TryGet(CursorOp.Next, default, out k, out _));
                Assert.Equal(N / 2, count);
                // Tree should be compact after rebalance: 2000 surviving keys in
                // ~22 leaves vs ~38 without rebalance (the original 4000-key tree).
                _out.WriteLine($"after alternating delete: entries={db.Entries} depth={db.Depth} " +
                    $"branch={db.BranchPages} leaf={db.LeafPages} overflow={db.OverflowPages}");
                Assert.True(db.LeafPages < 30, $"too many leaf pages after rebalance: {db.LeafPages}");
            }
        }
    }

    [Fact]
    public void Rebalance_PythonReadsAfterMerges()
    {
        // C# inserts, deletes half, Python reads the merged tree.
        string dir = TmpDir("rebal_pyread");
        const int N = 2000;
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 24 }))
        {
            using (var txn = env.BeginTransaction(false))
            {
                var db = txn.OpenDefaultDatabase();
                for (int i = 0; i < N; i++) txn.Put(db, B($"k{i:D05}"), B($"v{i:D05}"));
                txn.Commit();
            }
            using (var txn = env.BeginTransaction(false))
            {
                var db = txn.OpenDefaultDatabase();
                for (int i = 0; i < N; i += 2) txn.Delete(db, B($"k{i:D05}"));
                txn.Commit();
            }
        }
        string script = @"
import lmdb
env = lmdb.open('__DIR__', readonly=True)
t = env.begin()
c = t.cursor()
n = 0
prev = -1
for k, v in c:
    i = int(k[1:])
    assert i % 2 == 1, f'expected odd key, got {k}'
    assert i > prev, f'out of order: {i} <= {prev}'
    prev = i
    assert v == b'v' + (b'%05d' % i), (k, v)
    n += 1
assert n == __N__, n
assert t.get(b'k00000') is None
assert t.get(b'k00001') == b'v00001'
t.abort(); env.close()
print('OK', n)
".Replace("__DIR__", dir).Replace("__N__", (N / 2).ToString());
        var psi = new System.Diagnostics.ProcessStartInfo("python3")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(script);
        using var p = System.Diagnostics.Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"python exited {p.ExitCode}\nstdout={stdout}\nstderr={stderr}");
        _out.WriteLine("Python read merged tree: " + stdout.Trim());
    }
}
