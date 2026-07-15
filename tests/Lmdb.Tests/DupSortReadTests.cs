using System.Text;
using Lmdb;
using Xunit;
using Xunit.Abstractions;

namespace Lmdb.Tests;

/// <summary>
/// DUPSORT read-path cross-validation: reads databases with sorted duplicate
/// values per key, produced by the Python lmdb wheel (real liblmdb).
/// </summary>
public class DupSortReadTests
{
    private readonly ITestOutputHelper _out;
    public DupSortReadTests(ITestOutputHelper out_) => _out = out_;

    private static string FixturesDir => CrossCheckFixture.EnsureFixtures();
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void DupSort_SetReturnsFirstDup()
    {
        using var env = LmdbEnvironment.Open(FixturesDir + "/dupsort", new EnvOpenOptions { NoLock = true });
        using var txn = env.BeginTransaction();
        var db = txn.OpenDatabase("dups");
        Assert.True((db.Flags & DatabaseFlags.DupSort) != 0);

        // Set on 'fruits' should return the first dup (apple).
        Assert.True(txn.TryGet(db, B("fruits"), out var data));
        Assert.Equal(B("apple"), data.ToArray());
    }

    [Fact]
    public void DupSort_IterateAllKeyValuePairs()
    {
        using var env = LmdbEnvironment.Open(FixturesDir + "/dupsort", new EnvOpenOptions { NoLock = true });
        using var txn = env.BeginTransaction();
        var db = txn.OpenDatabase("dups");
        using var cur = txn.CreateCursor(db);

        var pairs = new System.Collections.Generic.List<(string, string)>();
        Assert.True(cur.TryGet(CursorOp.First, default, out var k, out var v));
        do { pairs.Add((Encoding.UTF8.GetString(k), Encoding.UTF8.GetString(v))); }
        while (cur.TryGet(CursorOp.Next, default, out k, out v));

        // fruits: apple, banana, cherry, date; nums: one, three, two; single: only
        Assert.Equal(8, pairs.Count);
        Assert.Equal(("fruits", "apple"), pairs[0]);
        Assert.Equal(("fruits", "date"), pairs[3]);
        Assert.Equal(("nums", "one"), pairs[4]);
        Assert.Equal(("nums", "two"), pairs[6]);
        Assert.Equal(("single", "only"), pairs[7]);
    }

    [Fact]
    public void DupSort_NextDupIteratesWithinKey()
    {
        using var env = LmdbEnvironment.Open(FixturesDir + "/dupsort", new EnvOpenOptions { NoLock = true });
        using var txn = env.BeginTransaction();
        var db = txn.OpenDatabase("dups");
        using var cur = txn.CreateCursor(db);

        // Position at 'fruits' (first dup = apple).
        Assert.True(cur.TryGet(CursorOp.Set, B("fruits"), out _, out var v));
        Assert.Equal(B("apple"), v.ToArray());

        // NEXT_DUP should iterate through banana, cherry, date.
        Assert.True(cur.TryGet(CursorOp.NextDup, default, out _, out v));
        Assert.Equal(B("banana"), v.ToArray());
        Assert.True(cur.TryGet(CursorOp.NextDup, default, out _, out v));
        Assert.Equal(B("cherry"), v.ToArray());
        Assert.True(cur.TryGet(CursorOp.NextDup, default, out _, out v));
        Assert.Equal(B("date"), v.ToArray());
        // One more NEXT_DUP should fail (end of dups for this key).
        Assert.False(cur.TryGet(CursorOp.NextDup, default, out _, out _));
    }

    [Fact]
    public void DupSort_NextNoDupSkipsToNextKey()
    {
        using var env = LmdbEnvironment.Open(FixturesDir + "/dupsort", new EnvOpenOptions { NoLock = true });
        using var txn = env.BeginTransaction();
        var db = txn.OpenDatabase("dups");
        using var cur = txn.CreateCursor(db);

        // Position at 'fruits' (first dup = apple).
        Assert.True(cur.TryGet(CursorOp.Set, B("fruits"), out _, out _));

        // NEXT_NODUP should skip to 'nums' (first dup = one).
        Assert.True(cur.TryGet(CursorOp.NextNoDup, default, out var k, out var v));
        Assert.Equal(B("nums"), k.ToArray());
        Assert.Equal(B("one"), v.ToArray());
    }

    [Fact]
    public void DupSort_GetBothFindsExactPair()
    {
        using var env = LmdbEnvironment.Open(FixturesDir + "/dupsort", new EnvOpenOptions { NoLock = true });
        using var txn = env.BeginTransaction();
        var db = txn.OpenDatabase("dups");
        using var cur = txn.CreateCursor(db);

        // GET_BOTH: find fruits + cherry.
        Assert.True(cur.TryGet(CursorOp.GetBoth, B("fruits"), B("cherry"), out _, out var v));
        Assert.Equal(B("cherry"), v.ToArray());

        // GET_BOTH with a non-existent dup should fail.
        Assert.False(cur.TryGet(CursorOp.GetBoth, B("fruits"), B("grape"), out _, out _));

        // GET_BOTH on a key with no dups (single value).
        Assert.True(cur.TryGet(CursorOp.GetBoth, B("single"), B("only"), out _, out v));
        Assert.Equal(B("only"), v.ToArray());
    }

    [Fact]
    public void DupSort_GetBothRangePositionsAtFirstGreaterOrEqual()
    {
        using var env = LmdbEnvironment.Open(FixturesDir + "/dupsort", new EnvOpenOptions { NoLock = true });
        using var txn = env.BeginTransaction();
        var db = txn.OpenDatabase("dups");
        using var cur = txn.CreateCursor(db);

        // GET_BOTH_RANGE: position at first dup >= 'c' for 'fruits' → cherry.
        Assert.True(cur.TryGet(CursorOp.GetBothRange, B("fruits"), B("c"), out _, out var v));
        Assert.Equal(B("cherry"), v.ToArray());

        // GET_BOTH_RANGE past all dups → false.
        Assert.False(cur.TryGet(CursorOp.GetBothRange, B("fruits"), B("z"), out _, out _));
    }

    [Fact]
    public void DupSort_PrevIteratesBackward()
    {
        using var env = LmdbEnvironment.Open(FixturesDir + "/dupsort", new EnvOpenOptions { NoLock = true });
        using var txn = env.BeginTransaction();
        var db = txn.OpenDatabase("dups");
        using var cur = txn.CreateCursor(db);

        // Last = (single, only), then work backwards.
        Assert.True(cur.TryGet(CursorOp.Last, default, out var k, out var v));
        Assert.Equal(("single", "only"), (Encoding.UTF8.GetString(k), Encoding.UTF8.GetString(v)));

        // PREV should go through nums (two, three, one), then fruits (date, cherry, ...).
        Assert.True(cur.TryGet(CursorOp.Prev, default, out k, out v));
        Assert.Equal(("nums", "two"), (Encoding.UTF8.GetString(k), Encoding.UTF8.GetString(v)));
        Assert.True(cur.TryGet(CursorOp.Prev, default, out k, out v));
        Assert.Equal(("nums", "three"), (Encoding.UTF8.GetString(k), Encoding.UTF8.GetString(v)));
        Assert.True(cur.TryGet(CursorOp.Prev, default, out k, out v));
        Assert.Equal(("nums", "one"), (Encoding.UTF8.GetString(k), Encoding.UTF8.GetString(v)));
        Assert.True(cur.TryGet(CursorOp.Prev, default, out k, out v));
        Assert.Equal(("fruits", "date"), (Encoding.UTF8.GetString(k), Encoding.UTF8.GetString(v)));
    }

    [Fact]
    public void DupSort_SingleValueKeyWorks()
    {
        using var env = LmdbEnvironment.Open(FixturesDir + "/dupsort", new EnvOpenOptions { NoLock = true });
        using var txn = env.BeginTransaction();
        var db = txn.OpenDatabase("dups");

        // A key with a single value (no F_DUPDATA) should work like a normal get.
        Assert.Equal(B("only"), txn.Get(db, B("single")).ToArray());
        Assert.False(txn.TryGet(db, B("single_missing"), out _));
    }
}
