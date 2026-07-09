// LmdbEnvironment: opens a memory-mapped LMDB data file and selects the newest
// valid committed meta page (the snapshot a read transaction observes).
//
// Ported from mdb_env_open / mdb_env_read_header / mdb_env_pick_meta (mdb.c).
// For the read milestone we map the data file read-only and do NOT touch the
// lock file (no reader-table registration). This is safe for reading a quiescent
// DB; concurrent-writer support arrives with the write path + lockfile layer.
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
}

public readonly record struct EnvInfo(
    long MapSize, ulong LastPgno, ulong LastTxnid, uint PageSize, uint MaxReaders);

public readonly record struct EnvStat(
    long PageSize, long Depth, ulong BranchPages, ulong LeafPages, ulong OverflowPages, ulong Entries);

public sealed unsafe class LmdbEnvironment : IDisposable
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
    internal byte* MetaPtr => _meta;

    private uint _psize;
    private long _mapSize;
    private ulong _lastPg;
    private ulong _txnId;
    private uint _flags;       // persistent env flags from mm_flags
    private bool _readOnly;
    private uint _nextDbi = Const.CORE_DBS;   // next handle for named sub-DBs

    /// <summary>Allocate a DBI handle for a named sub-database (read path).</summary>
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

        bool noSubdir = options.NoSubdir ?? !System.IO.Directory.Exists(path);
        if (noSubdir)
        {
            DataFilePath = path;
            LockFilePath = path + "-lock";
        }
        else
        {
            DataFilePath = System.IO.Path.Combine(path, "data.mdb");
            LockFilePath = System.IO.Path.Combine(path, "lock.mdb");
        }
    }

    private void OpenCore()
    {
        _map = _readOnly
            ? Platform.MappedFile.OpenReadOnly(DataFilePath)
            : Platform.MappedFile.OpenReadWrite(DataFilePath, _mapSize, create: false);
        _mapPtr = _map.Pointer;
        long fileSize = _map.Size;
        if (fileSize < Const.PAGEHDRSZ)
            throw new LmdbException(LmdbErr.Invalid, "file too small to be an LMDB database");

        // Page 0 is always a meta page; its mm_psize tells us the page geometry.
        _meta0 = _mapPtr;
        if (!Meta.IsValid(_meta0))
            throw new LmdbException(LmdbErr.Invalid, "page 0 is not a valid LMDB meta page");

        _psize = Meta.Psize(_meta0);
        if (_psize < Const.MIN_PAGESIZE || _psize > Const.MAX_PAGESIZE)
            throw new LmdbException(LmdbErr.Corrupted, $"page size {_psize} out of range");

        _meta1 = _mapPtr + _psize;
        bool v1 = fileSize >= 2L * _psize && Meta.IsValid(_meta1);

        // mdb_env_pick_meta: newer txnid wins; tie -> meta0.
        byte* chosen = _meta0;
        if (v1 && Meta.TxnId(_meta1) > Meta.TxnId(_meta0))
            chosen = _meta1;
        if (!Meta.IsValid(chosen))
            throw new LmdbException(LmdbErr.VersionMismatch, "no valid meta page found");

        _meta = chosen;
        _psize = Meta.Psize(_meta);            // page size of the chosen snapshot
        _mapSize = (long)Meta.MapSize(_meta);
        _lastPg = Meta.LastPg(_meta);
        _txnId = Meta.TxnId(_meta);
        _flags = Meta.EnvFlags(_meta);
    }

    /// <summary>Begin a transaction. Read-only transactions never block.</summary>
    public Transaction BeginTransaction(bool readOnly = true)
        => new(this, readOnly);

    /// <summary>Open the default (unnamed) database.</summary>
    public Database OpenDefaultDatabase()
        => Database.OpenCore(this, Const.MAIN_DBI);

    public EnvInfo Info => new(_mapSize, _lastPg, _txnId, _psize, 0);

    public EnvStat Stat(ulong dbiEntries, ulong branch, ulong leaf, ulong overflow, ushort depth)
        => new(_psize, depth, branch, leaf, overflow, dbiEntries);

    /// <summary>Pointer to the page with the given number in the mmap region.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte* Page(ulong pgno)
    {
        if (pgno > _lastPg)
            ThrowPageNotFound(pgno);
        return _mapPtr + _psize * pgno;
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
