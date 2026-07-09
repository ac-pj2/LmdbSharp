// On-disk structure overlays for LMDB pages, nodes, meta pages and DB records.
//
// These mirror the C macros in mdb.c (MP_*, NODE*, etc.) but as unsafe static
// accessors over a `byte*` pointing into the memory-mapped data file. We do NOT
// marshal these as managed structs because (a) the data is unmanaged mmap memory
// and (b) LMDB relies on raw pointer arithmetic identical to the C code.
//
// All offsets assume MDB_DEVEL=0 (PAGEBASE=0). Layouts are little-endian on the
// reference platform (x86-64 Linux); reads use BitConverter-style little-endian
// access via direct pointer casts, which is correct on the target platform.
//
// 64-bit memory model: pgno_t = txnid_t = mdb_size_t = uint64, indx_t = uint16.
using System.Runtime.CompilerServices;

namespace Lmdb;

/// <summary>Unsafe accessors over a page header (16 bytes) in the mmap region.</summary>
internal static unsafe class Page
{
    // page header: pgno(8) pad(2) flags(2) lower(2) upper(2) | ptrs[]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte* Ptr(byte* page) => page;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Pgno(byte* page) => *(ulong*)page;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort Pad(byte* page) => *(ushort*)(page + 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort Flags(byte* page) => *(ushort*)(page + 10);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort Lower(byte* page) => *(ushort*)(page + 12);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort Upper(byte* page) => *(ushort*)(page + 14);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort* Ptrs(byte* page) => (ushort*)(page + Const.PAGEHDRSZ);

    // METADATA(p) = p + PAGEHDRSZ
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte* Data(byte* page) => page + Const.PAGEHDRSZ;

    // NUMKEYS(p) = (MP_LOWER(p) - (PAGEHDRSZ - PAGEBASE)) >> 1   (PAGEBASE=0)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NumKeys(byte* page) => (Lower(page) - (Const.PAGEHDRSZ - Const.PAGEBASE)) >> 1;

    // SIZELEFT(p) = upper - lower
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SizeLeft(byte* page) => Upper(page) - Lower(page);

    // NODEPTR(p,i) = (MDB_node*)(p + MP_PTRS(p)[i] + PAGEBASE)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte* NodePtr(byte* page, int i) => page + Ptrs(page)[i] + Const.PAGEBASE;

    // LEAF2KEY(p,i,ks) = p + PAGEHDRSZ + i*ks
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte* Leaf2Key(byte* page, int i, int ks) => page + Const.PAGEHDRSZ + i * ks;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Is(byte* page, ushort flag) => (Flags(page) & flag) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLeaf(byte* page)   => (Flags(page) & Const.P_LEAF)     != 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLeaf2(byte* page)  => (Flags(page) & Const.P_LEAF2)    != 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsBranch(byte* page) => (Flags(page) & Const.P_BRANCH)   != 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsOverflow(byte* page) => (Flags(page) & Const.P_OVERFLOW) != 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsMeta(byte* page)   => (Flags(page) & Const.P_META)     != 0;

    // For overflow pages, mp_pb.pb_pages (uint32 at offset 12) holds the page count.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint OverflowPages(byte* page) => *(uint*)(page + 12);

    // ---- setters (write path) ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetPgno(byte* page, ulong pgno) => *(ulong*)(page + 0) = pgno;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetPad(byte* page, ushort v) => *(ushort*)(page + 8) = v;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetFlags(byte* page, ushort v) => *(ushort*)(page + 10) = v;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void OrFlags(byte* page, ushort v) => *(ushort*)(page + 10) = (ushort)(*(ushort*)(page + 10) | v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AndFlags(byte* page, ushort v) => *(ushort*)(page + 10) = (ushort)(*(ushort*)(page + 10) & v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetLower(byte* page, ushort v) => *(ushort*)(page + 12) = v;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetUpper(byte* page, ushort v) => *(ushort*)(page + 14) = v;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetOverflowPages(byte* page, uint v) => *(uint*)(page + 12) = v;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetPtr(byte* page, int i, ushort v) => ((ushort*)(page + Const.PAGEHDRSZ))[i] = v;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref ushort PtrAt(byte* page, int i) => ref ((ushort*)(page + Const.PAGEHDRSZ))[i];
}

/// <summary>Unsafe accessors over a node header (8 bytes) within a leaf/branch page.</summary>
internal static unsafe class Node
{
    // node header: mn_lo(2) mn_hi(2) mn_flags(2) mn_ksize(2) | key | data

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort Lo(byte* node)    => *(ushort*)(node + 0);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort Hi(byte* node)    => *(ushort*)(node + 2);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort Flags(byte* node) => *(ushort*)(node + 4);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort KSize(byte* node) => *(ushort*)(node + 6);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte* Key(byte* node) => node + Const.NODESIZE;

    // NODEDATA(node) = node->mn_data + node->mn_ksize = node + 8 + ksize
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte* Data(byte* node) => node + Const.NODESIZE + KSize(node);

    // NODEPGNO(node): branch-node child page number. 64-bit packs 48 bits across
    // mn_lo | mn_hi<<16 | mn_flags<<32  (PGNO_TOPWORD=32, branch nodes have no flags).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Pgno(byte* node)
        => Lo(node) | ((ulong)Hi(node) << 16) | ((ulong)Flags(node) << Const.PGNO_TOPWORD);

    // NODEDSZ(node): leaf-node data size = mn_lo | mn_hi<<16 (32-bit).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Dsz(byte* node) => (uint)(Lo(node) | ((uint)Hi(node) << 16));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Is(byte* node, ushort flag) => (Flags(node) & flag) != 0;

    // ---- setters (write path) ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetLo(byte* node, ushort v) => *(ushort*)(node + 0) = v;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetHi(byte* node, ushort v) => *(ushort*)(node + 2) = v;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetFlags(byte* node, ushort v) => *(ushort*)(node + 4) = v;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetKSize(byte* node, ushort v) => *(ushort*)(node + 6) = v;

    // SETPGNO: branch-node child page number (packs 48 bits across lo|hi<<16|flags<<32).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetPgno(byte* node, ulong pgno)
    {
        *(ushort*)(node + 0) = (ushort)(pgno & 0xffff);
        *(ushort*)(node + 2) = (ushort)(pgno >> 16);
        *(ushort*)(node + 4) = (ushort)(pgno >> Const.PGNO_TOPWORD);  // top word (branch nodes: no flags)
    }

    // SETDSZ: leaf-node data size (32-bit, into lo|hi<<16; leaves flags untouched).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetDsz(byte* node, uint size)
    {
        *(ushort*)(node + 0) = (ushort)(size & 0xffff);
        *(ushort*)(node + 2) = (ushort)(size >> 16);
    }
}

/// <summary>Unsafe accessors over a meta page (page flags=P_META; MDB_meta at +16).</summary>
/// <remarks>
/// MDB_meta layout (64-bit, no MDB_VL32):
///   +0   uint32 mm_magic
///   +4   uint32 mm_version
///   +8   void*  mm_address   (8)
///   +16  size_t mm_mapsize   (8)
///   +24  MDB_db mm_dbs[2]    (2 * 48 = 96)
///   +120 pgno_t mm_last_pg   (8)
///   +128 txnid_t mm_txnid    (8)
/// mm_psize overlaps mm_dbs[FREE_DBI].md_pad; mm_flags overlaps mm_dbs[FREE_DBI].md_flags.
/// </remarks>
internal static unsafe class Meta
{
    public const int Offset = Const.PAGEHDRSZ;          // meta struct starts after page header
    public const int DbsOffset = Offset + 24;           // mm_dbs[0]
    public const int DbSize = 48;
    public const int LastPgOffset = DbsOffset + Const.CORE_DBS * DbSize; // +120
    public const int TxnIdOffset = LastPgOffset + 8;    // +128

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte* Struct(byte* metaPage) => metaPage + Offset;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Magic(byte* metaPage) => *(uint*)(metaPage + Offset);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Version(byte* metaPage) => *(uint*)(metaPage + Offset + 4);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Address(byte* metaPage) => *(ulong*)(metaPage + Offset + 8);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong MapSize(byte* metaPage) => *(ulong*)(metaPage + Offset + 16);

    // mm_psize = mm_dbs[FREE_DBI].md_pad (uint32)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Psize(byte* metaPage) => Db.Pad(DbPtr(metaPage, Const.FREE_DBI));
    // mm_flags = mm_dbs[FREE_DBI].md_flags (uint16) — persistent env flags
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort EnvFlags(byte* metaPage) => Db.Flags(DbPtr(metaPage, Const.FREE_DBI));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte* DbPtr(byte* metaPage, uint dbi) => metaPage + DbsOffset + dbi * DbSize;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong LastPg(byte* metaPage) => *(ulong*)(metaPage + LastPgOffset);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong TxnId(byte* metaPage) => *(ulong*)(metaPage + TxnIdOffset);

    /// <summary>True if this page looks like a valid committed meta page.</summary>
    public static bool IsValid(byte* metaPage)
        => Page.IsMeta(metaPage)
           && Magic(metaPage) == Const.MDB_MAGIC
           && Version(metaPage) == Const.MDB_DATA_VERSION;
}

/// <summary>Unsafe accessors over an MDB_db record (48 bytes).</summary>
/// <remarks>
/// +0  uint32 md_pad        (also ksize for LEAF2 pages)
/// +4  uint16 md_flags
/// +6  uint16 md_depth
/// +8  pgno_t md_branch_pages (8)
/// +16 pgno_t md_leaf_pages   (8)
/// +24 pgno_t md_overflow_pages (8)
/// +32 size_t md_entries      (8)
/// +40 pgno_t md_root         (8)
/// </remarks>
internal static unsafe class Db
{
    public const int Size48 = 48;  // sizeof(MDB_db)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint  Pad(byte* db)            => *(uint*)(db + 0);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort Flags(byte* db)         => *(ushort*)(db + 4);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort Depth(byte* db)         => *(ushort*)(db + 6);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong BranchPages(byte* db)    => *(ulong*)(db + 8);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong LeafPages(byte* db)      => *(ulong*)(db + 16);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong OverflowPages(byte* db)  => *(ulong*)(db + 24);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Entries(byte* db)        => *(ulong*)(db + 32);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Root(byte* db)           => *(ulong*)(db + 40);

    /// <summary>Persistent DBI flags (strip the MDB_VALID runtime bit).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort PersistentFlags(byte* db) => (ushort)(Flags(db) & (ushort)Const.PERSISTENT_FLAGS);

    // ---- setters (write path) ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetPad(byte* db, uint v) => *(uint*)(db + 0) = v;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetFlags(byte* db, ushort v) => *(ushort*)(db + 4) = v;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetDepth(byte* db, ushort v) => *(ushort*)(db + 6) = v;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetRoot(byte* db, ulong v) => *(ulong*)(db + 40) = v;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetEntries(byte* db, ulong v) => *(ulong*)(db + 32) = v;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddBranchPages(byte* db, long d) => *(ulong*)(db + 8) = (ulong)((long)*(ulong*)(db + 8) + d);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddLeafPages(byte* db, long d) => *(ulong*)(db + 16) = (ulong)((long)*(ulong*)(db + 16) + d);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddOverflowPages(byte* db, long d) => *(ulong*)(db + 24) = (ulong)((long)*(ulong*)(db + 24) + d);
}
