// Lockfile / reader table: multi-process safety for LMDB.
//
// The lockfile (lock.mdb) is a memory-mapped file containing:
//   - Header (MDB_txbody): magic, format, last-committed txnid, numreaders
//   - Reader table: array of (txnid, pid) slots, one per active reader
//
// Read transactions register in a slot with the txnid they're observing. The
// writer scans the table to find the oldest live reader (find_oldest), ensuring
// it never reclaims pages freed by a txn newer than that. A byte-range file lock
// on byte 0 provides the single-writer mutex.
//
// Simplified vs C LMDB:
//   - No POSIX process-shared mutexes (use byte-range file locks instead)
//   - No thread-local slot caching (each read txn claims/releases a slot)
//   - No stale-reader detection (mdb_reader_check)
//   - Single-process: if no lockfile exists (MDB_NOLOCK), readers use txnid-1
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Lmdb.Platform;

internal sealed unsafe class Lockfile : IDisposable
{
    private FileStream? _fs;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private byte* _ptr;

    // Header layout (MDB_txbody): magic(4) format(4) txnid(8) numreaders(4) = 20 bytes,
    // padded to CACHELINE (64). Reader slots start at offset 64.
    public const int HeaderSize = 64;
    // MDB_reader: txnid(8) pid(8) tid(8), padded to CACHELINE (64) per slot.
    public const int ReaderSize = 64;

    internal byte* Ptr => _ptr;
    internal uint MaxReaders { get; }

    internal Lockfile(string path, uint maxReaders, bool create)
    {
        MaxReaders = maxReaders;
        long fileSize = HeaderSize + (long)maxReaders * ReaderSize;

        _fs = new FileStream(path, create ? FileMode.OpenOrCreate : FileMode.Open,
            FileAccess.ReadWrite, FileShare.ReadWrite);
        if (create && _fs.Length < fileSize) _fs.SetLength(fileSize);

        _mmf = MemoryMappedFile.CreateFromFile(
            _fs, null, fileSize, MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None, leaveOpen: false);
        _view = _mmf.CreateViewAccessor(0L, fileSize, MemoryMappedFileAccess.ReadWrite);
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _ptr);

        if (create && NeedInit())
            Init(maxReaders);
    }

    private bool NeedInit() => *(uint*)(_ptr + 0) != Const.MDB_MAGIC;

    private void Init(uint maxReaders)
    {
        *(uint*)(_ptr + 0) = Const.MDB_MAGIC;
        *(uint*)(_ptr + 4) = Const.MDB_LOCK_VERSION;
        *(ulong*)(_ptr + 8) = 0;       // mti_txnid
        *(uint*)(_ptr + 16) = 0;       // mti_numreaders
        _view!.Flush();
    }

    // --- header accessors ---
    internal ulong LastTxnid { get => *(ulong*)(_ptr + 8); set => *(ulong*)(_ptr + 8) = value; }
    internal uint NumReaders { get => *(uint*)(_ptr + 16); set => *(uint*)(_ptr + 16) = value; }

    // --- reader slot accessors ---
    internal ulong ReaderTxnid(int slot) => *(ulong*)(_ptr + HeaderSize + slot * ReaderSize);
    internal void SetReaderTxnid(int slot, ulong v) => *(ulong*)(_ptr + HeaderSize + slot * ReaderSize) = v;
    internal int ReaderPid(int slot) => *(int*)(_ptr + HeaderSize + slot * ReaderSize + 8);
    internal void SetReaderPid(int slot, int v) => *(int*)(_ptr + HeaderSize + slot * ReaderSize + 8) = v;

    /// <summary>Claim a reader slot. Returns the slot index, or -1 if the table is full.</summary>
    internal int ClaimReaderSlot(int pid)
    {
        uint nr = NumReaders;
        for (uint i = 0; i < nr; i++)
        {
            if (ReaderPid((int)i) == 0)
            {
                SetReaderPid((int)i, pid);
                SetReaderTxnid((int)i, ulong.MaxValue);
                return (int)i;
            }
        }
        if (nr < MaxReaders)
        {
            int slot = (int)nr;
            SetReaderPid(slot, pid);
            SetReaderTxnid(slot, ulong.MaxValue);
            NumReaders = nr + 1;
            return slot;
        }
        return -1;
    }

    /// <summary>Release a reader slot.</summary>
    internal void ReleaseReaderSlot(int slot)
    {
        SetReaderPid(slot, 0);
        SetReaderTxnid(slot, 0);
    }

    /// <summary>Find the oldest txnid any reader is still observing. Returns
    /// ulong.MaxValue if no readers are active.</summary>
    internal ulong FindOldestReader()
    {
        ulong oldest = ulong.MaxValue;
        uint nr = NumReaders;
        for (uint i = 0; i < nr; i++)
        {
            int pid = ReaderPid((int)i);
            if (pid != 0)
            {
                ulong t = ReaderTxnid((int)i);
                if (t < oldest) oldest = t;
            }
        }
        return oldest;
    }

    /// <summary>Update the last-committed txnid in the lockfile header.</summary>
    internal void UpdateLastTxnid(ulong txnid)
    {
        LastTxnid = txnid;
        _view!.Flush();
    }

    // --- writer mutex ---
    // Two layers, because they exclude different things:
    //   - _writerSem excludes THREADS in this process (fcntl byte-range locks
    //     do not conflict within a process, and the old "_writeLocked" early
    //     return let a second thread proceed without any lock at all; C LMDB's
    //     process-shared robust mutex excluded both threads and processes)
    //   - the byte-range file lock on byte 0 excludes other PROCESSES
    private readonly SemaphoreSlim _writerSem = new(1, 1);
    private bool _writeLocked;

    /// <summary>Acquire the exclusive writer lock (blocks until acquired).</summary>
    internal void LockWrite()
    {
        _writerSem.Wait();
        try
        {
            _fs!.Lock(0, 1);
        }
        catch
        {
            _writerSem.Release();
            throw;
        }
        _writeLocked = true;
    }

    /// <summary>Release the exclusive writer lock.</summary>
    internal void UnlockWrite()
    {
        if (!_writeLocked) return;
        _writeLocked = false;
        _fs!.Unlock(0, 1);
        _writerSem.Release();
    }

    public void Dispose()
    {
        if (_writeLocked) UnlockWrite();
        if (_ptr != null)
        {
            _view!.SafeMemoryMappedViewHandle.ReleasePointer();
            _ptr = null;
        }
        _view?.Dispose();
        _mmf?.Dispose();
        _fs?.Dispose();
    }
}
