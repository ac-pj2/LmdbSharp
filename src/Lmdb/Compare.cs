// Key/data comparators ported from mdb.c (mdb_cmp_memn / memnr / cint / long / int).
//
// Callers in mdb.c only ever test the sign of the result (<0 / ==0 / >0), so any
// sign-preserving int is acceptable; we return actual byte diffs where convenient.
//
// INTEGERKEY keys are stored in host (little-endian on the reference platform)
// byte order. We read them little-endian for portability regardless of host.
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Lmdb;

/// <summary>Pointer-based comparator: returns &lt;0, 0, or &gt;0.</summary>
internal unsafe delegate int CmpPtr(byte* a, int alen, byte* b, int blen);

internal static unsafe class Compare
{
    /// <summary>memcmp-style byte-wise compare (mdb_cmp_memn). Ascending.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Memn(byte* a, int alen, byte* b, int blen)
    {
        int len = alen;
        int lenDiff = alen - blen;
        if (lenDiff > 0) { len = blen; lenDiff = 1; }

        int diff = Memcmp(a, b, len);
        if (diff != 0) return diff;
        return lenDiff < 0 ? -1 : lenDiff;
    }

    /// <summary>Reverse byte-wise compare (mdb_cmp_memnr). For MDB_REVERSEKEY.</summary>
    public static int Memnr(byte* a, int alen, byte* b, int blen)
    {
        int lenDiff = alen - blen;
        // p1_lim offset within a: 0 normally, or (alen-blen)=blen when a is longer.
        int lim = lenDiff > 0 ? blen : 0;
        if (lenDiff > 0) lenDiff = 1;

        int ia = alen, ib = blen;
        while (ia > lim)
        {
            --ia; --ib;
            int diff = a[ia] - b[ib];
            if (diff != 0) return diff;
        }
        return lenDiff < 0 ? -1 : lenDiff;
    }

    /// <summary>
    /// Integer compare (mdb_cmp_cint, little-endian branch). Compares the key bytes
    /// from the most-significant byte (highest address for LE storage) down to the
    /// least-significant — i.e. numeric comparison of the stored integer. Used for
    /// MDB_INTEGERKEY (including the internal free-DB, whose keys are page numbers).
    /// </summary>
    public static int Cint(byte* a, int alen, byte* b, int blen)
    {
        int n = alen <= blen ? alen : blen;
        for (int i = n - 1; i >= 0; i--)
        {
            int diff = a[i] - b[i];
            if (diff != 0) return diff;
        }
        return alen < blen ? -1 : alen > blen ? 1 : 0;
    }

    /// <summary>Compare two 8-byte integers as uint64 (mdb_cmp_long).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Long(byte* a, int alen, byte* b, int blen)
    {
        ulong va = BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(a, 8));
        ulong vb = BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(b, 8));
        return va < vb ? -1 : va > vb ? 1 : 0;
    }

    /// <summary>Compare two 4-byte integers as uint32 (mdb_cmp_int).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Int(byte* a, int alen, byte* b, int blen)
    {
        uint va = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(a, 4));
        uint vb = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(b, 4));
        return va < vb ? -1 : va > vb ? 1 : 0;
    }

    /// <summary>Pick the key comparator for a DBI from its persistent flags (mdb.c).</summary>
    public static CmpPtr PickKey(ushort dbiFlags)
    {
        if ((dbiFlags & Const.MDB_INTEGERKEY) != 0) return Cint;
        if ((dbiFlags & Const.MDB_REVERSEKEY) != 0) return Memnr;
        return Memn;
    }

    /// <summary>Pick the dup-data comparator for a DBI (0 if not DUPSORT).</summary>
    public static CmpPtr? PickDup(ushort dbiFlags)
    {
        if ((dbiFlags & Const.MDB_DUPSORT) == 0) return null;
        if ((dbiFlags & Const.MDB_INTEGERDUP) != 0)
            return (dbiFlags & Const.MDB_DUPFIXED) != 0 ? Int : Cint;
        return (dbiFlags & Const.MDB_REVERSEDUP) != 0 ? Memnr : Memn;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Memcmp(byte* a, byte* b, int len)
    {
        // Use the BCL's optimized byte sequence comparison (SIMD on AVX2).
        // It returns true if equal, false if different — but we need the sign.
        int remaining = len;
        // Compare 8 bytes at a time using ulong reads.
        while (remaining >= 8)
        {
            ulong va = *(ulong*)a;
            ulong vb = *(ulong*)b;
            if (va != vb)
            {
                // Find the first differing byte within this 8-byte chunk.
                int diff = (int)(va - vb);  // wrong endianness — fall back to byte compare
                return ByteDiff(a, b, 8);
            }
            a += 8; b += 8; remaining -= 8;
        }
        // Handle the remaining 0-7 bytes.
        return ByteDiff(a, b, remaining);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ByteDiff(byte* a, byte* b, int len)
    {
        for (int i = 0; i < len; i++)
        {
            int d = a[i] - b[i];
            if (d != 0) return d;
        }
        return 0;
    }
}
