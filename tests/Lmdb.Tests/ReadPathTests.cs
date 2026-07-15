using System.Buffers.Binary;
using System.Text;
using Lmdb;
using Xunit;
using Xunit.Abstractions;

namespace Lmdb.Tests;

/// <summary>
/// Cross-validation tests: the C# port reads databases produced by the real
/// liblmdb (via the Python `lmdb` wheel). Proves on-disk format compatibility.
/// </summary>
public class ReadPathTests
{
    private readonly ITestOutputHelper _out;
    public ReadPathTests(ITestOutputHelper out_) => _out = out_;

    private static string FixturesDir => CrossCheckFixture.EnsureFixtures();

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static byte[] U64LE(ulong v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); return b; }

    [Fact]
    public void Hello_GetReturnsWorld()
    {
        using var env = LmdbEnvironment.Open(FixturesDir + "/hello", new EnvOpenOptions { NoLock = true });
        using var txn = env.BeginTransaction();
        var db = txn.OpenDefaultDatabase();
        var data = txn.Get(db, B("hello"));
        Assert.Equal(B("world"), data.ToArray());
    }

    [Fact]
    public void Hello_MissingKeyReturnsFalse()
    {
        using var env = LmdbEnvironment.Open(FixturesDir + "/hello", new EnvOpenOptions { NoLock = true });
        using var txn = env.BeginTransaction();
        var db = txn.OpenDefaultDatabase();
        Assert.False(txn.TryGet(db, B("nope"), out _));
    }

    [Fact]
    public void Seq_GetSpecificKey()
    {
        using var env = LmdbEnvironment.Open(FixturesDir + "/seq", new EnvOpenOptions { NoLock = true });
        using var txn = env.BeginTransaction();
        var db = txn.OpenDefaultDatabase();
        Assert.Equal(B("val_00123_payload"), txn.Get(db, B("key00123")).ToArray());
        Assert.Equal(B("val_00000_payload"), txn.Get(db, B("key00000")).ToArray());
        Assert.Equal(B("val_00999_payload"), txn.Get(db, B("key00999")).ToArray());
    }

    [Fact]
    public void Seq_CursorIteratesAllForward()
    {
        using var env = LmdbEnvironment.Open(FixturesDir + "/seq", new EnvOpenOptions { NoLock = true });
        using var txn = env.BeginTransaction();
        var db = txn.OpenDefaultDatabase();
        using var cur = txn.CreateCursor(db);

        int count = 0;
        Assert.True(cur.TryGet(CursorOp.First, default, out var k, out var v));
        do
        {
            string key = Encoding.UTF8.GetString(k);
            Assert.Equal($"key{count:D5}", key);
            Assert.Equal($"val_{count:D5}_payload", Encoding.UTF8.GetString(v));
            count++;
        } while (cur.TryGet(CursorOp.Next, default, out k, out v));

        Assert.Equal(1000, count);
    }

    [Fact]
    public void Seq_CursorIteratesAllBackward()
    {
        using var env = LmdbEnvironment.Open(FixturesDir + "/seq", new EnvOpenOptions { NoLock = true });
        using var txn = env.BeginTransaction();
        var db = txn.OpenDefaultDatabase();
        using var cur = txn.CreateCursor(db);

        int count = 0;
        Assert.True(cur.TryGet(CursorOp.Last, default, out var k, out var v));
        do
        {
            Assert.Equal($"key{999 - count:D5}", Encoding.UTF8.GetString(k));
            count++;
        } while (cur.TryGet(CursorOp.Prev, default, out k, out v));

        Assert.Equal(1000, count);
    }

    [Fact]
    public void Seq_SetRangePositionsAtFirstGreaterOrEqual()
    {
        using var env = LmdbEnvironment.Open(FixturesDir + "/seq", new EnvOpenOptions { NoLock = true });
        using var txn = env.BeginTransaction();
        var db = txn.OpenDefaultDatabase();
        using var cur = txn.CreateCursor(db);

        // Exact key -> lands on it.
        Assert.True(cur.TryGet(CursorOp.SetRange, B("key00500"), out var k, out _));
        Assert.Equal("key00500", Encoding.UTF8.GetString(k));

        // In-between key -> lands on the next greater key.
        Assert.True(cur.TryGet(CursorOp.SetRange, B("key00500.5"), out k, out _));
        Assert.Equal("key00501", Encoding.UTF8.GetString(k));

        // Past the end -> false.
        Assert.False(cur.TryGet(CursorOp.SetRange, B("zzzzzz"), out _, out _));
    }

    [Fact]
    public void Big_OverflowValueReadsCorrectly()
    {
        using var env = LmdbEnvironment.Open(FixturesDir + "/big", new EnvOpenOptions { NoLock = true });
        using var txn = env.BeginTransaction();
        var db = txn.OpenDefaultDatabase();

        var big = txn.Get(db, B("bigkey"));
        Assert.Equal(20000, big.Length);
        for (int i = 0; i < big.Length; i++)
            Assert.Equal((byte)(i & 0xFF), big[i]);

        Assert.Equal(B("s"), txn.Get(db, B("small")).ToArray());
    }

    [Fact]
    public void IntKey_NamedSubDbGetAndIterate()
    {
        using var env = LmdbEnvironment.Open(FixturesDir + "/intkey", new EnvOpenOptions { NoLock = true });
        using var txn = env.BeginTransaction();
        var db = txn.OpenDatabase("ints");
        Assert.True((db.Flags & DatabaseFlags.IntegerKey) != 0);

        // Point lookups for every 7th value 0..499.
        for (int i = 0; i < 500; i += 7)
        {
            Assert.True(txn.TryGet(db, U64LE((ulong)i), out var got));
            Assert.Equal((ulong)(i * 1000 + 7), BinaryPrimitives.ReadUInt64LittleEndian(got));
        }
        // Missing key.
        Assert.False(txn.TryGet(db, U64LE(8), out _));

        // Iterate all 72 in ascending numeric order.
        using var cur = txn.CreateCursor(db);
        int count = 0; ulong prev = 0;
        Assert.True(cur.TryGet(CursorOp.First, default, out var k, out var v));
        do
        {
            ulong key = BinaryPrimitives.ReadUInt64LittleEndian(k);
            Assert.True(key >= prev);
            prev = key;
            Assert.Equal(key * 1000 + 7, BinaryPrimitives.ReadUInt64LittleEndian(v));
            count++;
        } while (cur.TryGet(CursorOp.Next, default, out k, out v));
        Assert.Equal(72, count);
    }

    [Fact]
    public void Empty_GetReturnsFalse()
    {
        using var env = LmdbEnvironment.Open(FixturesDir + "/empty", new EnvOpenOptions { NoLock = true });
        using var txn = env.BeginTransaction();
        var db = txn.OpenDefaultDatabase();
        Assert.False(txn.TryGet(db, B("anything"), out _));
        using var cur = txn.CreateCursor(db);
        Assert.False(cur.TryGet(CursorOp.First, default, out _, out _));
    }

    [Fact]
    public void EnvInfo_ReportsCorrectSnapshot()
    {
        using var env = LmdbEnvironment.Open(FixturesDir + "/seq", new EnvOpenOptions { NoLock = true });
        var info = env.Info;
        Assert.Equal(4096u, info.PageSize);
        Assert.Equal(16 * 1024 * 1024L, info.MapSize);
        Assert.True(info.LastTxnid >= 1);
        _out.WriteLine($"seq env: psize={info.PageSize} mapsize={info.MapSize} last_pg={info.LastPgno} txnid={info.LastTxnid}");
    }
}
