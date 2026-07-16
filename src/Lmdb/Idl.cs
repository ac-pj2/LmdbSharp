// ID List (IDL) management, ported from midl.c / midl.h.
//
// An IDL is a descending-sorted array of page-number IDs (MDB_ID = ulong).
// Following the C layout, _buf[0] holds the live count and elements live at
// indices [1..Count]; a separate Capacity field replaces the C trick of storing
// the allocated length at index [-1]. Keeping the count-at-[0] convention makes
// the eventual write-path callers (freelist, dirty-page list) near-verbatim ports.
//
// An ID2L (dirty-page list) is ASCENDING-sorted by id and carries a page pointer
// per entry; implemented as Id2l.
using System.Runtime.CompilerServices;

namespace Lmdb;

/// <summary>Descending-sorted ID list (free pages, etc.). Port of mdb_midl_*.</summary>
internal sealed class Idl
{
    public const int LogN = 16;
    public const int DbSize = 1 << LogN;     // 65536
    public const int UmSize = 1 << (LogN + 1); // 131072
    public const int DbMax = DbSize - 1;
    public const int UmMax = UmSize - 1;

    private ulong[] _buf;   // _buf[0] = count, _buf[1..Count] = ids (descending)
    public int Capacity { get; private set; }

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (int)_buf[0];
        [MethodImpl(MethodImplOptions.AggressiveInlining)] private set => _buf[0] = (ulong)value;
    }

    public ulong this[int i]   // 1-based, matching C
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _buf[i];
        [MethodImpl(MethodImplOptions.AggressiveInlining)] set => _buf[i] = value;
    }

    public Idl(int num)
    {
        Capacity = num;
        _buf = new ulong[num + 2];
        _buf[0] = 0;
    }

    public bool IsEmpty => Count == 0;
    public ulong First => _buf[1];
    public ulong Last  => _buf[Count];

    /// <summary>Deep copy (used to give transactions a private view of the
    /// environment's reusable-page pool).</summary>
    public Idl Clone()
    {
        var c = new Idl(Capacity);
        Array.Copy(_buf, c._buf, Count + 1);
        return c;
    }

    /// <summary>Scan a sorted list for an adjacent duplicate. Returns the
    /// duplicated id, or 0 when all ids are distinct.</summary>
    public ulong FindAdjacentDuplicate()
    {
        for (int i = 2; i <= Count; i++)
            if (_buf[i] == _buf[i - 1]) return _buf[i];
        return 0;
    }

    public int Append(ulong id) => Append(this, id);
    public int AppendList(Idl app) => AppendList(this, app);

    public static int Append(Idl idl, ulong id)
    {
        if (idl.Count >= idl.Capacity)
        {
            if (Grow(ref idl, UmMax) != 0) return -1;
        }
        idl.Count++;
        idl._buf[idl.Count] = id;
        return 0;
    }

    public static int AppendList(Idl idl, Idl app)
    {
        if (idl.Count + app.Count >= idl.Capacity)
        {
            if (Grow(ref idl, app.Count) != 0) return -1;
        }
        Array.Copy(app._buf, 1, idl._buf, idl.Count + 1, app.Count);
        idl.Count += app.Count;
        return 0;
    }


    /// <summary>Quicksort + insertion sort (mdb_midl_sort). Sorts DESCENDING.</summary>
    public void Sort()
    {
        int ir = Count;
        int l = 1;
        int jstack = 0;
        Span<int> istack = stackalloc int[sizeof(int) * 8 * 2];
        for (; ; )
        {
            const int Small = 8;
            if (ir - l < Small)
            {
                for (int j = l + 1; j <= ir; j++)
                {
                    ulong a = _buf[j];
                    int i;
                    for (i = j - 1; i >= 1; i--)
                    {
                        if (_buf[i] >= a) break;
                        _buf[i + 1] = _buf[i];
                    }
                    _buf[i + 1] = a;
                }
                if (jstack == 0) break;
                ir = istack[jstack--];
                l = istack[jstack--];
            }
            else
            {
                int k = (l + ir) >> 1;
                Swap(k, l + 1);
                if (_buf[l] < _buf[ir]) Swap(l, ir);
                if (_buf[l + 1] < _buf[ir]) Swap(l + 1, ir);
                if (_buf[l] < _buf[l + 1]) Swap(l, l + 1);
                int i = l + 1, j = ir;
                ulong a = _buf[l + 1];
                for (; ; )
                {
                    do i++; while (_buf[i] > a);
                    do j--; while (_buf[j] < a);
                    if (j < i) break;
                    Swap(i, j);
                }
                _buf[l + 1] = _buf[j];
                _buf[j] = a;
                jstack += 2;
                if (ir - i + 1 >= j - l)
                {
                    istack[jstack] = ir;
                    istack[jstack - 1] = i;
                    ir = j - 1;
                }
                else
                {
                    istack[jstack] = j - 1;
                    istack[jstack - 1] = l;
                    l = i;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Swap(int i, int j) => (_buf[i], _buf[j]) = (_buf[j], _buf[i]);

    /// <summary>Pop the last (smallest) page number from a descending list.
    /// Returns true if a page was available.</summary>
    public bool TryPop(out ulong id)
    {
        if (Count == 0) { id = 0; return false; }
        id = _buf[Count];
        Count--;
        return true;
    }

    /// <summary>Find a contiguous run of <paramref name="num"/> page numbers in the
    /// descending list. Returns the 1-based index of the last (smallest) entry in
    /// the run, or 0 if none found. (mdb_page_alloc's tail search.)</summary>
    public int FindContiguous(int num)
    {
        if (num <= 0) return 0;
        if (num == 1) return Count > 0 ? Count : 0;
        int n2 = num - 1;
        int i = Count;
        while (i > n2)
        {
            ulong pgno = _buf[i];
            if (_buf[i - n2] == pgno + (ulong)n2)
                return i;
            i--;
        }
        return 0;
    }

    /// <summary>Remove <paramref name="num"/> entries ending at 1-based index
    /// <paramref name="lastIdx"/>, shifting trailing entries down.</summary>
    public void RemoveRange(int lastIdx, int num)
    {
        int newLen = Count - num;
        for (int j = lastIdx - num, i = lastIdx; i <= Count; )
            _buf[++j] = _buf[++i];
        Count = newLen;
    }

    private static int Grow(ref Idl idl, int num)
    {
        int newCap = idl.Capacity + num + 2;
        var nb = new ulong[newCap + 2];
        Array.Copy(idl._buf, nb, idl._buf.Length);
        idl._buf = nb;
        idl.Capacity = newCap;
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Cmp(ulong x, ulong y) => x < y ? -1 : x > y ? 1 : 0;
}

/// <summary>Ascending-sorted ID/pointer list (dirty pages). Port of mdb_mid2l_*.</summary>
internal sealed class Id2l
{
    public unsafe struct Entry { public ulong Id; public byte* Ptr; }

    private Entry[] _buf;   // _buf[0].Id = count, entries at [1..Count] ascending
    public int Capacity { get; private set; }

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (int)_buf[0].Id;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] private set => _buf[0].Id = (ulong)value;
    }

    public ref Entry this[int i] => ref _buf[i];

    public Id2l(int num)
    {
        Capacity = num;
        _buf = new Entry[num + 2];
        _buf[0].Id = 0;
    }

    /// <summary>Binary search (mdb_mid2l_search, ascending). 1-based.</summary>
    public int Search(ulong id)
    {
        uint baseIdx = 0, cursor = 1;
        int val = 0;
        uint n = (uint)Count;
        while (0 < n)
        {
            uint pivot = n >> 1;
            cursor = baseIdx + pivot + 1;
            val = Cmp(id, _buf[cursor].Id);   // ascending: compare id vs element
            if (val < 0) { n = pivot; }
            else if (val > 0) { baseIdx = cursor; n -= pivot + 1; }
            else { return (int)cursor; }
        }
        if (val > 0) ++cursor;
        return (int)cursor;
    }

    public int Insert(in Entry id)
    {
        int x = Search(id.Id);
        if (x < 1) return -2;
        if (x <= Count && _buf[x].Id == id.Id) return -1;   // duplicate
        if (Count >= Idl.UmMax) return -2;
        Count++;
        for (int i = Count; i > x; i--)
            _buf[i] = _buf[i - 1];
        _buf[x] = id;
        return 0;
    }

    public int Append(in Entry id)
    {
        if (Count >= Capacity)
        {
            Grow(this, Idl.UmMax);
        }
        Count++;
        _buf[Count] = id;
        return 0;
    }

    private static void Grow(Id2l idl, int num)
    {
        int newCap = idl.Capacity + num + 2;
        var nb = new Entry[newCap + 2];
        Array.Copy(idl._buf, nb, idl._buf.Length);
        idl._buf = nb;
        idl.Capacity = newCap;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Cmp(ulong x, ulong y) => x < y ? -1 : x > y ? 1 : 0;
}
