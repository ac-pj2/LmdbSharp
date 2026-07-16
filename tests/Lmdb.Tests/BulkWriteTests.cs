using System.Buffers.Binary;
using System.Text;
using Lmdb;
using Xunit;

namespace Lmdb.Tests;

/// <summary>MDB_MULTIPLE (PutMultiple) and MDB_RESERVE (PutReserve).</summary>
public class BulkWriteTests
{
    private static string TmpDir(string name)
    {
        string dir = $"/tmp/lmdb-cs/{name}";
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] Packed(int from, int count)
    {
        var b = new byte[count * 8];
        for (int i = 0; i < count; i++)
            BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(i * 8), from + i);
        return b;
    }

    private static LmdbEnvironment DupFixedEnv(string dir) => LmdbEnvironment.Open(dir,
        new EnvOpenOptions
        {
            ReadOnly = false, MapSize = 512L << 20,
            MainDbFlags = DatabaseFlags.DupSort | DatabaseFlags.DupFixed,
        });

    [Theory]
    [InlineData(3)]        // stays a plain/sub-page set
    [InlineData(2_000)]    // sub-DB, single page then splits
    [InlineData(200_000)]  // deep LEAF2 tree, root splits
    public void PutMultiple_EqualsPerValuePuts(int count)
    {
        string dirA = TmpDir($"bulk-a-{count}");
        string dirB = TmpDir($"bulk-b-{count}");
        var packed = Packed(0, count);

        using (var env = DupFixedEnv(dirA))
        using (var txn = env.BeginTransaction(readOnly: false))
        {
            var db = txn.OpenDefaultDatabase();
            int added = txn.PutMultiple(db, "k"u8, packed, 8);
            Assert.Equal(count, added);
            txn.Commit();
        }
        using (var env = DupFixedEnv(dirB))
        using (var txn = env.BeginTransaction(readOnly: false))
        {
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < count; i++)
                txn.Put(db, "k"u8, packed.AsSpan(i * 8, 8));
            txn.Commit();
        }

        Assert.True(LmdbIntegrityChecker.Check(dirA).Clean);
        // Both stores hold identical dup sets.
        foreach (var dir in new[] { dirA, dirB })
        {
            using var env = DupFixedEnv(dir);
            using var txn = env.BeginTransaction(readOnly: true);
            var db = txn.OpenDefaultDatabase();
            Assert.Equal((ulong)count, db.Entries);
            using var cur = txn.CreateCursor(db);
            long expect = 0;
            Assert.True(cur.TryGet(CursorOp.GetMultiple, "k"u8, out _, out var chunk));
            do
            {
                for (int o = 0; o < chunk.Length; o += 8)
                    Assert.Equal(expect++, BinaryPrimitives.ReadInt64BigEndian(chunk.Slice(o, 8)));
            } while (cur.TryGet(CursorOp.NextMultiple, default, out _, out chunk));
            Assert.Equal(count, expect);
        }
    }

    [Fact]
    public void PutMultiple_SkipsExistingDuplicates_CountsOnlyNew()
    {
        string dir = TmpDir("bulk-dups");
        using var env = DupFixedEnv(dir);
        using var txn = env.BeginTransaction(readOnly: false);
        var db = txn.OpenDefaultDatabase();
        Assert.Equal(500, txn.PutMultiple(db, "k"u8, Packed(0, 500), 8));
        // Overlapping range: 250 already present.
        Assert.Equal(250, txn.PutMultiple(db, "k"u8, Packed(250, 500), 8));
        Assert.Equal(750UL, db.Entries);
        txn.Commit();
    }

    [Fact]
    public void PutMultiple_Validation()
    {
        string dir = TmpDir("bulk-valid");
        using (var env = DupFixedEnv(dir))
        using (var txn = env.BeginTransaction(readOnly: false))
        {
            var db = txn.OpenDefaultDatabase();
            var ex = Assert.Throws<LmdbException>(() => txn.PutMultiple(db, "k"u8, new byte[12], 8));
            Assert.Equal(LmdbErr.BadValsize, ex.ErrorCode);
            Assert.Equal(0, txn.PutMultiple(db, "k"u8, ReadOnlySpan<byte>.Empty, 8));
        }
        string dir2 = TmpDir("bulk-valid2");
        using (var env = LmdbEnvironment.Open(dir2, new EnvOpenOptions
        { ReadOnly = false, MapSize = 64L << 20, MainDbFlags = DatabaseFlags.DupSort }))
        using (var txn = env.BeginTransaction(readOnly: false))
        {
            var db = txn.OpenDefaultDatabase();
            var ex = Assert.Throws<LmdbException>(() => txn.PutMultiple(db, "k"u8, new byte[16], 8));
            Assert.Equal(LmdbErr.Incompatible, ex.ErrorCode);
        }
    }

    [Fact]
    public void PutReserve_InlineAndOverflow_RoundTrip()
    {
        string dir = TmpDir("reserve");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 64L << 20 });
        using (var txn = env.BeginTransaction(readOnly: false))
        {
            var db = txn.OpenDefaultDatabase();

            var small = txn.PutReserve(db, "small"u8, 40);
            Assert.Equal(40, small.Length);
            for (int i = 0; i < 40; i++) small[i] = (byte)i;

            var big = txn.PutReserve(db, "big"u8, 9000);   // overflow chain
            Assert.Equal(9000, big.Length);
            for (int i = 0; i < 9000; i++) big[i] = (byte)(i * 7);

            // Same-size overwrite reserve: returns the existing slot.
            var again = txn.PutReserve(db, "small"u8, 40);
            for (int i = 0; i < 40; i++) again[i] = (byte)(i + 100);
            txn.Commit();
        }

        Assert.True(LmdbIntegrityChecker.Check(dir).Clean);
        using (var txn = env.BeginTransaction(readOnly: true))
        {
            var db = txn.OpenDefaultDatabase();
            Assert.True(txn.TryGet(db, "small"u8, out var s));
            Assert.Equal(40, s.Length);
            for (int i = 0; i < 40; i++) Assert.Equal((byte)(i + 100), s[i]);
            Assert.True(txn.TryGet(db, "big"u8, out var b));
            Assert.Equal(9000, b.Length);
            for (int i = 0; i < 9000; i++) Assert.Equal((byte)(i * 7), b[i]);
        }
    }

    [Fact]
    public void PutReserve_OnDupSortDb_ThrowsIncompatible()
    {
        string dir = TmpDir("reserve-dup");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions
        { ReadOnly = false, MapSize = 64L << 20, MainDbFlags = DatabaseFlags.DupSort });
        using var txn = env.BeginTransaction(readOnly: false);
        var db = txn.OpenDefaultDatabase();
        var ex = Assert.Throws<LmdbException>(() => txn.PutReserve(db, "k"u8, 8));
        Assert.Equal(LmdbErr.Incompatible, ex.ErrorCode);
    }

    [Fact]
    public void PutReserve_ManyKeys_SurvivesSplits()
    {
        string dir = TmpDir("reserve-many");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 256L << 20 });
        using (var txn = env.BeginTransaction(readOnly: false))
        {
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < 20_000; i++)
            {
                var span = txn.PutReserve(db, Encoding.UTF8.GetBytes($"key{i:D8}"), 32);
                BinaryPrimitives.WriteInt64LittleEndian(span, i);
                span[31] = (byte)i;
            }
            txn.Commit();
        }
        Assert.True(LmdbIntegrityChecker.Check(dir).Clean);
        using (var txn = env.BeginTransaction(readOnly: true))
        {
            var db = txn.OpenDefaultDatabase();
            Assert.Equal(20_000UL, db.Entries);
            foreach (int i in new[] { 0, 1, 9_999, 19_999 })
            {
                Assert.True(txn.TryGet(db, Encoding.UTF8.GetBytes($"key{i:D8}"), out var v));
                Assert.Equal(i, BinaryPrimitives.ReadInt64LittleEndian(v));
                Assert.Equal((byte)i, v[31]);
            }
        }
    }
}
