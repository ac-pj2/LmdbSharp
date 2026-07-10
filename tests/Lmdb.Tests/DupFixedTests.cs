using System.Buffers.Binary;
using System.Text;
using Lmdb;
using Xunit;
using Xunit.Abstractions;

namespace Lmdb.Tests;

/// <summary>DUPFIXED (LEAF2) tests: fixed-size duplicate values packed with no node headers.</summary>
public class DupFixedTests
{
    private readonly ITestOutputHelper _out;
    public DupFixedTests(ITestOutputHelper out_) => _out = out_;

    private static string FixturesDir => CrossCheckFixture.EnsureFixtures();
    private static string TmpDir(string name)
    {
        string dir = $"/tmp/lmdb-cs/{name}";
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static byte[] U64LE(ulong v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); return b; }

    // ---- Read path: read Python-created DUPFIXED databases ----

    [Fact]
    public void DupFixed_ReadFromPythonFixture()
    {
        using var env = LmdbEnvironment.Open(FixturesDir + "/dupfixed");
        using var txn = env.BeginTransaction();
        var db = txn.OpenDatabase("fixed");
        Assert.True((db.Flags & DatabaseFlags.DupFixed) != 0, $"expected DupFixed, got {db.Flags}");

        // Iterate all dups of 'nums' — should be sorted: 1,2,3,4,5,6,9.
        // (Python put [3,1,4,1,5,9,2,6] but DUPSORT dedups identical data values.)
        using var cur = txn.CreateCursor(db);
        Assert.True(cur.TryGet(CursorOp.Set, B("nums"), out _, out var v));
        ulong[] expected = { 1, 2, 3, 4, 5, 6, 9 };
        int idx = 0;
        do
        {
            Assert.Equal(expected[idx], BinaryPrimitives.ReadUInt64LittleEndian(v));
            idx++;
        } while (cur.TryGet(CursorOp.NextDup, default, out _, out v));
        Assert.Equal(expected.Length, idx);
    }

    // ---- Write path: C# creates and writes DUPFIXED databases ----

    [Fact]
    public void DupFixed_WriteFromCsOnly()
    {
        string dir = TmpDir("dupfixed_cs");
        // Create the DUPFIXED DB from C#.
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions
        { ReadOnly = false, MapSize = 1 << 20, MainDbFlags = DatabaseFlags.DupSort | DatabaseFlags.DupFixed }))
        {
            using (var txn = env.BeginTransaction(false))
            {
                var db = txn.OpenDefaultDatabase();
                Assert.True((db.Flags & DatabaseFlags.DupFixed) != 0);
                foreach (var i in new ulong[] { 7, 3, 1, 9, 5, 3 })
                    txn.Put(db, B("nums"), U64LE(i));
                txn.Commit();
            }
            // Read back.
            using (var txn = env.BeginTransaction())
            {
                var db = txn.OpenDefaultDatabase();
                using var cur = txn.CreateCursor(db);
                Assert.True(cur.TryGet(CursorOp.Set, B("nums"), out _, out var v));
                ulong[] expected = { 1, 3, 5, 7, 9 };
                int idx = 0;
                do { Assert.Equal(expected[idx++], BinaryPrimitives.ReadUInt64LittleEndian(v)); }
                while (cur.TryGet(CursorOp.NextDup, default, out _, out v));
                Assert.Equal(expected.Length, idx);
            }
        }
        // Python verifies.
        var psi = new System.Diagnostics.ProcessStartInfo("python3")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(@"
import lmdb, struct
e = lmdb.open('" + dir + @"')
t = e.begin()
c = t.cursor()
vals = []
for k, v in c:
    assert k == b'nums'
    vals.append(struct.unpack('<Q', v)[0])
assert vals == [1,3,5,7,9], vals  # DUPSORT dedups identical data values
t.abort(); e.close()
print('OK')
");
        using var p = System.Diagnostics.Process.Start(psi)!;
        string stderr = p.StandardError.ReadToEnd(); p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr);
    }

    [Fact]
    public void DupFixed_LargeDupCount()
    {
        // Insert 500 fixed-size dups — should trigger sub-page growth + sub-DB conversion.
        string dir = TmpDir("dupfixed_large");
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions
        { ReadOnly = false, MapSize = 2 << 20, MainDbFlags = DatabaseFlags.DupSort | DatabaseFlags.DupFixed | DatabaseFlags.IntegerDup }))
        {
            using (var txn = env.BeginTransaction(false))
            {
                var db = txn.OpenDefaultDatabase();
                var rng = new Random(42);
                var vals = new HashSet<ulong>();
                while (vals.Count < 500) vals.Add((ulong)rng.Next(10000));
                foreach (var v in vals) txn.Put(db, B("k"), U64LE(v));
                txn.Commit();
            }
            using (var txn = env.BeginTransaction())
            {
                var db = txn.OpenDefaultDatabase();
                using var cur = txn.CreateCursor(db);
                int count = 0; ulong prev = 0;
                Assert.True(cur.TryGet(CursorOp.Set, B("k"), out _, out var v));
                do
                {
                    ulong val = BinaryPrimitives.ReadUInt64LittleEndian(v);
                    Assert.True(val >= prev || count == 0);
                    prev = val; count++;
                } while (cur.TryGet(CursorOp.NextDup, default, out _, out v));
                Assert.Equal(500, count);
                _out.WriteLine($"Read {count} DUPFIXED dups");
            }
        }
        // Python verifies.
        var psi = new System.Diagnostics.ProcessStartInfo("python3")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(@"
import lmdb, struct
e = lmdb.open('" + dir + @"')
t = e.begin()
c = t.cursor(db=t.cursor().subdb() if hasattr(t.cursor(), 'subdb') else None)
c2 = t.cursor()
n = 0
prev = 0
for k, v in c2:
    val = struct.unpack('<Q', v)[0]
    assert val >= prev or n == 0
    prev = val
    n += 1
assert n == 500, n
t.abort(); e.close()
print('OK', n)
");
        using var p = System.Diagnostics.Process.Start(psi)!;
        string stderr = p.StandardError.ReadToEnd(); p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr);
    }
}
