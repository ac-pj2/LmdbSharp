using Lmdb;
using Xunit;

namespace Lmdb.Tests;

// Property tests targeting the mutation-testing survivors: the comparators and
// IDL algorithms are checked exhaustively against reference models over small
// vectors, so any operator/boundary mutation changes an observable result.
public unsafe class ComparatorAndIdlPropertyTests
{
    private static int Sign(int v) => v < 0 ? -1 : v > 0 ? 1 : 0;

    private static int Invoke(CmpPtr cmp, byte[] a, byte[] b)
    {
        fixed (byte* pa = a, pb = b)
        {
            // fixed on an empty array yields null; comparators must only be
            // handed non-empty keys by the engine, so use a 1-byte backing
            // store with length 0 semantics.
            byte dummy = 0;
            byte* ra = pa == null ? &dummy : pa;
            byte* rb = pb == null ? &dummy : pb;
            return Sign(cmp(ra, a.Length, rb, b.Length));
        }
    }

    private static IEnumerable<byte[]> SmallVectors()
    {
        byte[] alphabet = { 0x00, 0x01, 0x7f, 0xfe, 0xff };
        yield return Array.Empty<byte>();
        foreach (var x in alphabet)
        {
            yield return new[] { x };
            foreach (var y in alphabet)
            {
                yield return new[] { x, y };
                yield return new[] { x, y, (byte)(x ^ y) };
            }
        }
        yield return new byte[] { 1, 2, 3, 4, 5 };
        yield return new byte[] { 1, 2, 3, 4, 6 };
        yield return new byte[] { 0xff, 0, 0, 0xff };
    }

    [Fact]
    public void Memn_matches_lexicographic_reference_exhaustively()
    {
        foreach (var a in SmallVectors())
            foreach (var b in SmallVectors())
            {
                int expected = Sign(a.AsSpan().SequenceCompareTo(b));
                Assert.Equal(expected, Invoke(Compare.Memn, a, b));
            }
    }

    [Fact]
    public void Memnr_matches_reversed_bytes_reference_exhaustively()
    {
        foreach (var a in SmallVectors())
            foreach (var b in SmallVectors())
            {
                // C semantics: compare the LAST min(alen,blen) bytes walking
                // backwards; ties break on length.
                int n = Math.Min(a.Length, b.Length);
                int expected = 0;
                for (int i = 1; i <= n && expected == 0; i++)
                    expected = Sign(a[^i].CompareTo(b[^i]));
                if (expected == 0) expected = Sign(a.Length.CompareTo(b.Length));
                Assert.Equal(expected, Invoke(Compare.Memnr, a, b));
            }
    }

    [Fact]
    public void Cint_matches_numeric_reference_for_equal_widths()
    {
        ulong[] values = { 0, 1, 2, 255, 256, 257, 0x1_0000, 0xFFFF_FFFF,
                           0x1_0000_0000UL, ulong.MaxValue - 1, ulong.MaxValue };
        foreach (var va in values)
            foreach (var vb in values)
            {
                var a = BitConverter.GetBytes(va);
                var b = BitConverter.GetBytes(vb);
                Assert.Equal(Sign(va.CompareTo(vb)), Invoke(Compare.Cint, a, b));
            }
        // Width tiebreak: equal prefix, longer wins.
        Assert.Equal(-1, Invoke(Compare.Cint, new byte[] { 5, 0 }, new byte[] { 5, 0, 0 }));
        Assert.Equal(1, Invoke(Compare.Cint, new byte[] { 5, 0, 0 }, new byte[] { 5, 0 }));
    }

    [Fact]
    public void Long_and_int_match_numeric_reference()
    {
        Assert.Equal(-1, Invoke(Compare.Long, BitConverter.GetBytes(255UL), BitConverter.GetBytes(256UL)));
        Assert.Equal(1, Invoke(Compare.Long, BitConverter.GetBytes(ulong.MaxValue), BitConverter.GetBytes(0UL)));
        Assert.Equal(0, Invoke(Compare.Long, BitConverter.GetBytes(77UL), BitConverter.GetBytes(77UL)));
        Assert.Equal(-1, Invoke(Compare.Int, BitConverter.GetBytes(255U), BitConverter.GetBytes(256U)));
        Assert.Equal(1, Invoke(Compare.Int, BitConverter.GetBytes(uint.MaxValue), BitConverter.GetBytes(1U)));
    }

    [Fact]
    public void PickKey_and_PickDup_select_by_flag_precedence()
    {
        Assert.Equal((CmpPtr)Compare.Memn, Compare.PickKey(0));
        Assert.Equal((CmpPtr)Compare.Cint, Compare.PickKey((ushort)Const.MDB_INTEGERKEY));
        Assert.Equal((CmpPtr)Compare.Memnr, Compare.PickKey((ushort)Const.MDB_REVERSEKEY));
        // C checks REVERSEKEY before INTEGERKEY.
        Assert.Equal((CmpPtr)Compare.Memnr,
            Compare.PickKey((ushort)(Const.MDB_REVERSEKEY | Const.MDB_INTEGERKEY)));

        Assert.Null(Compare.PickDup(0));
        Assert.NotNull(Compare.PickDup((ushort)Const.MDB_DUPSORT)!);
    }

    // ---- IDL algorithms vs reference models ----

    private static Idl Build(params ulong[] values)
    {
        var idl = new Idl(4);
        foreach (var v in values) idl.Append(v);
        return idl;
    }

    private static List<ulong> ToList(Idl idl)
    {
        var list = new List<ulong>();
        for (int i = 1; i <= idl.Count; i++) list.Add(idl[i]);
        return list;
    }

    [Fact]
    public void Sort_produces_descending_order_for_many_shapes()
    {
        var rng = new Random(42);
        for (int round = 0; round < 200; round++)
        {
            int n = rng.Next(0, 40);
            var values = Enumerable.Range(0, n).Select(_ => (ulong)rng.Next(0, 30)).ToArray();
            var idl = Build(values);
            idl.Sort();
            var expected = values.OrderByDescending(v => v).ToList();
            Assert.Equal(expected, ToList(idl));
        }
        // Shapes that specifically stress the insertion-sort threshold and
        // median-of-three pivoting.
        foreach (var shape in new[]
        {
            new ulong[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 },
            new ulong[] { 9, 8, 7, 6, 5, 4, 3, 2, 1 },
            new ulong[] { 5, 5, 5, 5, 5, 5, 5, 5, 5, 5 },
            Enumerable.Range(0, 64).Select(i => (ulong)(i * 37 % 64)).ToArray(),
        })
        {
            var idl = Build(shape);
            idl.Sort();
            Assert.Equal(shape.OrderByDescending(v => v).ToList(), ToList(idl));
        }
    }

    [Fact]
    public void TryPop_returns_smallest_and_shrinks()
    {
        var idl = Build(9, 7, 5, 3);
        idl.Sort();
        Assert.True(idl.TryPop(out var v1)); Assert.Equal(3UL, v1);
        Assert.True(idl.TryPop(out var v2)); Assert.Equal(5UL, v2);
        Assert.Equal(2, idl.Count);
        var empty = Build();
        Assert.False(empty.TryPop(out _));
    }

    [Fact]
    public void FindContiguous_and_RemoveRange_agree_with_model()
    {
        // Descending list with runs: {12,11,10, 7, 5,4,3}
        var idl = Build(12, 11, 10, 7, 5, 4, 3);
        idl.Sort();
        int idx3 = idl.FindContiguous(3);
        Assert.True(idx3 > 0);
        ulong start = idl[idx3];
        Assert.Equal(3UL, start);            // smallest run start preferred
        idl.RemoveRange(idx3, 3);
        Assert.Equal(new ulong[] { 12, 11, 10, 7 }, ToList(idl));

        Assert.Equal(0, idl.FindContiguous(4));      // no run of 4
        int idx2 = idl.FindContiguous(2);            // {11,10} run... via 12,11,10: runs of 3 exist
        Assert.True(idx2 > 0);
        Assert.Equal(0, Build(5, 3, 1).FindContiguous(2));
        Assert.Equal(0, Build().FindContiguous(1));

        // Single-element requests return the smallest entry.
        var single = Build(9, 4);
        single.Sort();
        Assert.Equal(single.Count, single.FindContiguous(1));
    }

    [Fact]
    public void AppendList_concatenates()
    {
        var a = Build(9, 5);
        var b = Build(4, 2);
        Idl.AppendList(a, b);
        Assert.Equal(new ulong[] { 9, 5, 4, 2 }, ToList(a));
    }

    [Fact]
    public void FindAdjacentDuplicate_detects_only_real_duplicates()
    {
        var dup = Build(9, 7, 7, 3); dup.Sort();
        Assert.Equal(7UL, dup.FindAdjacentDuplicate());
        var clean = Build(9, 7, 3); clean.Sort();
        Assert.Equal(0UL, clean.FindAdjacentDuplicate());
        Assert.Equal(0UL, Build().FindAdjacentDuplicate());
    }
}
