// Constants ported verbatim from mdb.c / lmdb.h (mdb.master).
// C source line references are in comments for traceability during the port.
// MDB_DEVEL == 0 in the reference build, so MDB_DATA_VERSION=1, MDB_LOCK_VERSION=2,
// PAGEBASE=0, MDB_MAXKEYSIZE=511.
namespace Lmdb;

internal static class Const
{
    // mdb.c:664
    public const uint MDB_MAGIC = 0xBEEFC0DE;
    // mdb.c:667  (MDB_DEVEL ? 999 : 1)
    public const uint MDB_DATA_VERSION = 1;
    // mdb.c:669
    public const uint MDB_LOCK_VERSION = 2;
    public const int MDB_LOCK_VERSION_BITS = 12;

    // mdb.c:770
    public const long DEFAULT_MAPSIZE = 1048576;
    // mdb.c:816
    public const int DEFAULT_READERS = 126;

    // Hardcoded counts
    public const int NUM_METAS = 2;   // meta pages live at pgno 0 and 1
    public const int CORE_DBS = 2;    // free-DB + main-DB stored inline in each meta
    public const uint FREE_DBI = 0;   // dbi 0 = free-page tracking DB
    public const uint MAIN_DBI = 1;   // dbi 1 = default (unnamed) DB

    // Page geometry. PAGEHDRSZ = offsetof(MDB_page, mp_ptrs) = 16 on 64-bit.
    // NODESIZE = offsetof(MDB_node, mn_data) = 8.
    // PAGEBASE = MDB_DEVEL ? PAGEHDRSZ : 0  => 0. Node ptrs are absolute page offsets.
    public const int PAGEHDRSZ = 16;
    public const int NODESIZE = 8;
    public const int PAGEBASE = 0;
    public const int CACHELINE = 64;

    // PGNO_TOPWORD = ((pgno_t)-1 > 0xffffffffu ? 32 : 0)  => 32 on 64-bit.
    // Branch nodes pack a 48-bit pgno across mn_lo | mn_hi<<16 | mn_flags<<32.
    public const int PGNO_TOPWORD = 32;

    // mdb.c:644  MAX_PAGESIZE = PAGEBASE ? 0x10000 : 0x8000
    public const int MAX_PAGESIZE = 0x8000;   // 32768
    public const int MIN_PAGESIZE = 512;

    // mdb.h:692  MDB_MAXKEYSIZE = MDB_DEVEL ? 0 : 511
    public const int MDB_MAXKEYSIZE = 511;

    // FILL_THRESHOLD (tenths of a percent); pages emptier than this merge candidates.
    public const int FILL_THRESHOLD = 250;

    // (pgno_t)-1 — "no page" / invalid root sentinel.
    public const ulong P_INVALID = ulong.MaxValue;

    // ---- Page flags (mdb.c ~1016) ----
    public const ushort P_BRANCH   = 0x01;
    public const ushort P_LEAF     = 0x02;
    public const ushort P_OVERFLOW = 0x04;
    public const ushort P_META     = 0x08;
    public const ushort P_DIRTY    = 0x10;
    public const ushort P_LEAF2    = 0x20;
    public const ushort P_SUBP     = 0x40;
    public const ushort P_LOOSE    = 0x4000;
    public const ushort P_KEEP     = 0x8000;

    // ---- Node flags (mdb.c ~1127) ----
    public const ushort F_BIGDATA = 0x01;  // data lives on an overflow page
    public const ushort F_SUBDATA = 0x02;  // data is a sub-database
    public const ushort F_DUPDATA = 0x04;  // data has duplicates

    // ---- Environment flags (lmdb.h:314) ----
    public const uint MDB_FIXEDMAP    = 0x0000001;
    public const uint MDB_NOSUBDIR    = 0x0004000;
    public const uint MDB_NOSYNC      = 0x0010000;
    public const uint MDB_RDONLY      = 0x0020000;
    public const uint MDB_NOMETASYNC  = 0x0040000;
    public const uint MDB_WRITEMAP    = 0x0080000;
    public const uint MDB_MAPASYNC    = 0x0100000;
    public const uint MDB_NOTLS       = 0x0200000;
    public const uint MDB_NOLOCK      = 0x0400000;
    public const uint MDB_NORDAHEAD   = 0x0800000;
    public const uint MDB_NOMEMINIT   = 0x1000000;
    public const uint MDB_PREVSNAPSHOT= 0x2000000;

    // PERSISTENT_FLAGS = 0xffff & ~MDB_VALID  (stored in mm_flags)
    public const uint PERSISTENT_FLAGS = 0x00007fff;

    // ---- DBI open flags (lmdb.h:343) ----
    public const uint MDB_REVERSEKEY  = 0x02;
    public const uint MDB_DUPSORT     = 0x04;
    public const uint MDB_INTEGERKEY  = 0x08;
    public const uint MDB_DUPFIXED    = 0x10;
    public const uint MDB_INTEGERDUP  = 0x20;
    public const uint MDB_REVERSEDUP  = 0x40;
    public const uint MDB_CREATE      = 0x40000;
    public const uint MDB_VALID       = 0x8000;

    // ---- Write flags (lmdb.h:364) ----
    public const uint MDB_NOOVERWRITE = 0x10;
    public const uint MDB_NODUPDATA   = 0x20;
    public const uint MDB_CURRENT     = 0x40;
    public const uint MDB_RESERVE     = 0x10000;
    public const uint MDB_APPEND      = 0x20000;
    public const uint MDB_APPENDDUP   = 0x40000;
    public const uint MDB_MULTIPLE    = 0x80000;
}
