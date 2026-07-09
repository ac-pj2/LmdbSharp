// LmdbEnvironment: opens a memory-mapped LMDB data file and selects the newest
// valid committed meta page (the snapshot a transaction observes).
//
// Ported from mdb_env_open / mdb_env_read_header / mdb_env_pick_meta /
// mdb_env_init_meta / mdb_env_write_meta (mdb.c).
using System.Runtime.CompilerServices;

namespace Lmdb;

public sealed class EnvOpenOptions
{
    public bool ReadOnly { get; set; } = true;
    /// <summary>If true, <paramref name="path"/> is the data file itself and the
    /// lock file is <c>path + "-lock"</c>. If false (default), path is a directory
    /// holding <c>data.mdb</c> and <c>lock.mdb</c>. Auto-detected when unset.</summary>
    public bool? NoSubdir { get; set; }
    public bool NoLock { get; set; }
    public bool NoTls { get; set; }
    public long MapSize { get; set; } = Const.DEFAULT_MAPSIZE;
    public uint MaxDbs { get; set; } = 0;
    public uint MaxReaders { get; set; } = Const.DEFAULT_READERS;
    /// <summary>Persistent flags for the main (default) database, set at creation time.
    /// Use to create a DUPSORT/INTEGERKEY/etc. main DB. Only applied when the DB is
    /// first created; ignored when opening an existing DB.</summary>
    public DatabaseFlags MainDbFlags { get; set; } = DatabaseFlags.None;
}

public readonly record struct EnvInfo(
    long MapSize, ulong LastPgno, ulong LastTxnid, uint PageSize, uint MaxReaders);

public readonly record struct EnvStat(
    long PageSize, long Depth, ulong BranchPages, ulong LeafPages, ulong OverflowPages, ulong Entries);

public sealed unsafe partial class LmdbEnvironment : IDisposable
{
    private Platform.MappedFile? _map;
    private byte* _mapPtr;
    private byte* _meta0;
    private byte* _meta1;
    private byte* _meta;       // chosen snapshot meta page

    internal byte* MapPtr => _mapPtr;
    internal uint PageSize => _psize;
    internal ulong LastPg => _lastPg;
    internal ulong TxnId => _txnId;
    internal long MapSize => _mapSize;
    internal ulong MaxPg => _maxPg;
    internal uint NodeMax => _nodeMax;
    internal byte* MetaPtr => _meta;
    internal bool IsReadOnly => _readOnly;

    private uint _psize;
    private long _mapSize;
    private ulong _lastPg;
    private ulong _txnId;
    private uint _flags;       // persistent env flags from mm_flags
    private bool _readOnly;
    private ulong _maxPg;      // mapsize / psize
    private uint _nodeMax;     // max data size that stays inline (vs overflow)
    private uint _nextDbi = Const.CORE_DBS;   // next handle for named sub-DBs
    private DatabaseFlags _mainDbFlags;

    /// <summary>In-memory cache of reusable pages from the free-DB (me_pghead).</summary>
    internal Idl? PgHead;
    /// <summary>Highest free-DB txnid key consumed into PgHead (me_pglast).
    /// Set by LoadPgHead, cleared by FreelistSave after deleting consumed records.</summary>
    internal ulong PgLast;

    /// <summary>Allocate a DBI handle for a named sub-database.</summary>
    internal uint AllocDbi() => _nextDbi++;

    public string Path { get; }
    public string DataFilePath { get; }
    public string LockFilePath { get; }

    /// <summary>Open an LMDB environment at <paramref name="path"/>.</summary>
    public static LmdbEnvironment Open(string path, EnvOpenOptions? options = null)
    {
        var env = new LmdbEnvironment(path, options ?? new EnvOpenOptions());
        env.OpenCore();
        return env;
    }

    private LmdbEnvironment(string path, EnvOpenOptions options)
    {
        Path = path;
        _readOnly = options.ReadOnly;
        _mapSize = options.MapSize;
        _mainDbFlags = options.MainDbFlags;

        bool noSubdir = options.NoSubdir ?? !System.IO.Directory.Exists(path);
        if (noSubdir)
        {
            DataFilePath = path;
            LockFilePath = path + "-lock";
        }
        else
        {
            System.IO.Directory.CreateDirectory(path);
            DataFilePath = System.IO.Path.Combine(path, "data.mdb");
            LockFilePath = System.IO.Path.Combine(path, "lock.mdb");
        }
    }

    private void OpenCore()
    {
        bool exists = System.IO.File.Exists(DataFilePath);
        bool needsCreate = !exists && !_readOnly;

        // Pick a page size: default to the OS page size, capped at MAX_PAGESIZE
        // (mdb.c uses sysconf(_SC_PAGESIZE)). For created DBs we use the OS size;
        // for existing DBs the meta page supplies it.
        _psize = needsCreate ? OsPageSize() : 0;

        _map = _readOnly
            ? Platform.MappedFile.OpenReadOnly(DataFilePath)
            : Platform.MappedFile.OpenReadWrite(DataFilePath, _mapSize, create: needsCreate);
        _mapPtr = _map.Pointer;

        if (needsCreate)
        {
            InitMeta();
            _meta0 = _mapPtr;
            _meta1 = _mapPtr + _psize;
        }
        else
        {
            long fileSize = _map.Size;
            if (fileSize < Const.PAGEHDRSZ)
                throw new LmdbException(LmdbErr.Invalid, "file too small to be an LMDB database");
            _meta0 = _mapPtr;
            if (!Meta.IsValid(_meta0))
                throw new LmdbException(LmdbErr.Invalid, "page 0 is not a valid LMDB meta page");
            _psize = Meta.Psize(_meta0);
            if (_psize < Const.MIN_PAGESIZE || _psize > Const.MAX_PAGESIZE)
                throw new LmdbException(LmdbErr.Corrupted, $"page size {_psize} out of range");
        }

        _meta1 = _mapPtr + _psize;
        bool v1 = _map.Size >= 2L * _psize && Meta.IsValid(_meta1);

        // mdb_env_pick_meta: newer txnid wins; tie -> meta0.
        byte* chosen = _meta0;
        if (v1 && Meta.TxnId(_meta1) > Meta.TxnId(_meta0))
            chosen = _meta1;
        if (!Meta.IsValid(chosen))
            throw new LmdbException(LmdbErr.VersionMismatch, "no valid meta page found");

        _meta = chosen;
        _psize = Meta.Psize(_meta);
        _mapSize = (long)Meta.MapSize(_meta);
        _lastPg = Meta.LastPg(_meta);
        _txnId = Meta.TxnId(_meta);
        _flags = Meta.EnvFlags(_meta);
        RecomputeDerived();
    }

    /// <summary>Write the initial two meta pages for a freshly created environment
    /// (mdb_env_init_meta). Both start at txnid 0, last_pg = NUM_METAS-1, empty roots.</summary>
    private void InitMeta()
    {
        _psize = OsPageSize();
        if (_psize > Const.MAX_PAGESIZE) _psize = Const.MAX_PAGESIZE;
        if (_psize < Const.MIN_PAGESIZE) _psize = Const.MIN_PAGESIZE;

        RecomputeDerived();
        // mm_dbs[FREE_DBI].md_flags carries MDB_INTEGERKEY (free-DB keys are pgno).
        // mm_psize overlaps md_pad of FREE_DBI; mm_flags overlaps md_flags of FREE_DBI.
        for (int i = 0; i < Const.NUM_METAS; i++)
        {
            byte* mp = _mapPtr + (long)_psize * i;
            *(ulong*)(mp + 0) = (ulong)i;                 // mp_pgno
            *(ushort*)(mp + 8) = 0;                        // mp_pad
            *(ushort*)(mp + 10) = Const.P_META;            // mp_flags
            // lower/upper unused for meta pages

            byte* meta = mp + Const.PAGEHDRSZ;
            *(uint*)(meta + 0) = Const.MDB_MAGIC;
            *(uint*)(meta + 4) = Const.MDB_DATA_VERSION;
            *(ulong*)(meta + 8) = 0UL;                     // mm_address (no fixed mapping)
            *(ulong*)(meta + 16) = (ulong)_mapSize;        // mm_mapsize

            byte* dbFree = Meta.DbPtr(mp, Const.FREE_DBI);
            byte* dbMain = Meta.DbPtr(mp, Const.MAIN_DBI);
            // md_pad(FREE) = psize; md_flags(FREE) = MDB_INTEGERKEY
            *(uint*)(dbFree + 0) = _psize;
            *(ushort*)(dbFree + 4) = (ushort)Const.MDB_INTEGERKEY;
            // md_flags(MAIN) = user-requested persistent flags (e.g. MDB_DUPSORT)
            *(ushort*)(dbMain + 4) = (ushort)((uint)_mainDbFlags & (uint)Const.PERSISTENT_FLAGS);
            // roots P_INVALID, everything else zero
            *(ulong*)(dbFree + 40) = Const.P_INVALID;
            *(ulong*)(dbMain + 40) = Const.P_INVALID;

            // last_pg / txnid at page-relative offsets (Meta.*Offset are page-relative).
            *(ulong*)(mp + Meta.LastPgOffset) = (ulong)(Const.NUM_METAS - 1); // last_pg = 1
            *(ulong*)(mp + Meta.TxnIdOffset) = 0UL;      // txnid 0
        }
        _map?.Flush();
        _map?.Stream?.Flush(true);   // fsync
    }

    private void RecomputeDerived()
    {
        _maxPg = (ulong)(_mapSize / _psize);
        // me_nodemax = (((psize - PAGEHDRSZ) / MDB_MINKEYS) & -2), MDB_MINKEYS = 2
        _nodeMax = (uint)((((int)_psize - Const.PAGEHDRSZ) / 2) & ~1);
    }

    private static uint OsPageSize()
    {
        int s = Environment.SystemPageSize;
        return (uint)(s <= 0 ? 4096 : s);
    }

    /// <summary>Begin a transaction. Read-only transactions never block. A write
    /// transaction (<paramref name="readOnly"/>=false) allocates a dirty page list
    /// and observes a txnid = last committed + 1.</summary>
    public Transaction BeginTransaction(bool readOnly = true)
        => new(this, readOnly);

    public Database OpenDefaultDatabase() => Database.OpenCore(this, Const.MAIN_DBI);

    public EnvInfo Info => new(_mapSize, _lastPg, _txnId, _psize, 0);

    public EnvStat Stat(ulong dbiEntries, ulong branch, ulong leaf, ulong overflow, ushort depth)
        => new(_psize, depth, branch, leaf, overflow, dbiEntries);

    /// <summary>Pointer to the page with the given number in the mmap region
    /// (the committed snapshot). Write transactions consult their dirty list first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte* Page(ulong pgno)
    {
        if (pgno > _lastPg)
            ThrowPageNotFound(pgno);
        return _mapPtr + _psize * pgno;
    }

    /// <summary>Pointer to the page with the given number, no bounds check (for the
    /// write path, which allocates pages up to maxPg before lastPg is bumped).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte* MapPage(ulong pgno) => _mapPtr + _psize * pgno;

    /// <summary>Flush the mmap view to the OS (msync).</summary>
    internal void FlushView() => _map?.Flush();

    /// <summary>fsync the underlying file for durability.</summary>
    internal void SyncFile() => _map?.Stream?.Flush(true);

    /// <summary>Write the committed meta page for a transaction (mdb_env_write_meta).
    /// toggle = txnid & 1 selects page 0 or 1. Writes mapsize + mm_dbs + last_pg + txnid
    /// (the fields from mm_mapsize onward); magic/version/address are stable.</summary>
    internal void WriteMeta(int toggle, byte* dbFree, byte* dbMain, ulong lastPg, ulong txnid, long mapSize)
    {
        byte* mp = _mapPtr + (long)_psize * toggle;

        // Persist mapsize, the two core DB records, last_pg, txnid. Offsets are
        // page-relative (the same convention the Meta accessors use).
        *(ulong*)(mp + Const.PAGEHDRSZ + 16) = (ulong)mapSize;            // mm_mapsize (page+32)
        // mm_dbs[FREE_DBI] and mm_dbs[MAIN_DBI] (48 bytes each, at page+40)
        Buffer.MemoryCopy(dbFree, mp + Meta.DbsOffset, Db.Size48, Db.Size48);
        Buffer.MemoryCopy(dbMain, mp + Meta.DbsOffset + Db.Size48, Db.Size48, Db.Size48);
        *(ulong*)(mp + Meta.LastPgOffset) = lastPg;
        // Memory barrier: ensure the page body is visible before we publish txnid.
        System.Threading.Thread.MemoryBarrier();
        *(ulong*)(mp + Meta.TxnIdOffset) = txnid;

        _map?.Flush();
        _map?.Stream?.Flush(true);   // fdatasync — durability of the meta page

        // Update the env's in-memory snapshot to the freshly committed meta.
        _meta = mp;
        _lastPg = lastPg;
        _txnId = txnid;
        _mapSize = mapSize;
        RecomputeDerived();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowPageNotFound(ulong pgno)
        => throw new LmdbException(LmdbErr.PageNotFound, $"page {pgno} beyond last_pg");

    public void Dispose()
    {
        _map?.Dispose();
        _map = null;
        _mapPtr = null;
        _meta = _meta0 = _meta1 = null;
    }
}
