// LmdbTransaction: a snapshot view of the environment.
//
// Read-only transactions observe the meta page selected at open time (the newest
// committed snapshot). They perform no locking and never block a writer.
//
// Write transactions keep a dirty-page list (Id2l, ascending by pgno) of COW
// copies in native memory, a free-page list (Idl) of pages orphaned by COW, and
// mutable copies of the two core MDB_db records. On commit the dirty pages are
// flushed into the mmap, the file is fsynced, and the alternate meta page
// (toggle = txnid & 1) is written last — the atomic publish step.
using System.Runtime.InteropServices;

namespace Lmdb;

public sealed unsafe partial class LmdbTransaction : IDisposable
{
    internal readonly LmdbEnvironment Env;
    internal readonly bool ReadOnly;
    internal ulong TxnId;
    internal ulong NextPgno;

    // --- write-txn state ---
    internal Id2l? Dirty;
    internal Idl? FreePgs;
    private byte* _dbFreeRec;     // mutable MDB_db for FREE_DBI (native, 48 bytes)
    private byte* _dbMainRec;     // mutable MDB_db for MAIN_DBI
    internal bool Written;        // any dirty pages exist?
    private int _readerSlot = -1;  // lockfile reader slot (-1 if none)
    internal byte* _metaPtr;       // pinned meta page (snapshot at open time)
    private ulong _snapshotTxnId;
    private ulong _snapshotLastPg;
    // Reusable cursors to avoid per-call allocation (896B per LmdbCursor).
    private LmdbCursor? _cachedReadCursor;
    private LmdbCursor? _cachedWriteCursor;
    private System.Collections.Generic.List<(byte[] name, IntPtr dbRec)>? _subDbs;

    public LmdbEnvironment Environment => Env;
    public ulong Id => TxnId;

    internal LmdbTransaction(LmdbEnvironment env, bool readOnly)
    {
        Env = env;
        ReadOnly = readOnly;
        // Pin the meta page snapshot at open time. This ensures read txns see a
        // consistent snapshot even if a writer commits during the txn.
        _metaPtr = env.MetaPtr;
        _snapshotTxnId = env.TxnId;
        _snapshotLastPg = env.LastPg;
        TxnId = env.TxnId;
        NextPgno = env.LastPg + 1;

        if (readOnly)
        {
            // Register a reader slot so the writer knows our snapshot txnid.
            var lf = env.Lockfile;
            if (lf != null)
            {
                _readerSlot = lf.ClaimReaderSlot(env.Pid);
                if (_readerSlot >= 0)
                    lf.SetReaderTxnid(_readerSlot, TxnId);
            }
        }
        else
        {
            Dirty = new Id2l(1024);   // grows on demand; avoids 2MB pre-allocation
            FreePgs = new Idl(64);
            // Mutable copies of the snapshot's core DB records.
            _dbFreeRec = AllocDbRec(env.MetaPtr, Const.FREE_DBI);
            _dbMainRec = AllocDbRec(env.MetaPtr, Const.MAIN_DBI);
            TxnId = env.TxnId + 1;
            // Acquire the exclusive writer lock (blocks if another writer is active).
            env.Lockfile?.LockWrite();
            // Eagerly load reusable pages from the free-DB so AllocPage can draw
            // from PgHead during the txn (mdb_page_alloc reads the free-DB lazily;
            // we load up-front for simplicity).
            LoadPgHead();
        }
    }

    private static byte* AllocDbRec(byte* metaPtr, uint dbi)
    {
        byte* src = Meta.DbPtr(metaPtr, dbi);
        byte* p = (byte*)NativeMemory.Alloc((nuint)Db.Size48);
        Buffer.MemoryCopy(src, p, Db.Size48, Db.Size48);
        return p;
    }

    /// <summary>Resolve a page number to a pointer, consulting the dirty list for
    /// write transactions (mdb_page_get). Read txns read straight from the mmap.</summary>
    internal byte* GetPage(ulong pgno)
    {
        // Walk the txn chain: check this txn's dirty list, then the parent's, etc.
        for (var tx = this; tx != null; tx = tx.Parent)
        {
            if (tx.Dirty != null)
            {
                int x = tx.Dirty.Search(pgno);
                if (x <= tx.Dirty.Count && tx.Dirty[x].Id == pgno)
                    return tx.Dirty[x].Ptr;
            }
        }
        return Env.MapPage(pgno);
    }

    /// <summary>Open the default (unnamed) database. For write transactions the
    /// returned handle mutates the txn's DB record copy (committed at Commit()).</summary>
    public LmdbDatabase OpenDefaultDatabase()
    {
        if (ReadOnly)
            return LmdbDatabase.OpenCore(Env, Const.MAIN_DBI, _metaPtr);

        var db = new LmdbDatabase(Env, Const.MAIN_DBI)
        {
            DbRec = _dbMainRec,
            InWriteTxn = true,
        };
        db.DbFlags = Db.PersistentFlags(_dbMainRec);
        db.KeyCmp = Compare.PickKey(db.DbFlags);
        db.DupCmp = Compare.PickDup(db.DbFlags);
        return db;
    }

    /// <summary>Resolve a DBI handle to its native MDB_db record pointer for this
    /// transaction. For core DBs, returns the txn's own mutable copy. For named
    /// sub-DBs, searches the txn's sub-DB list.</summary>
    internal byte* ResolveDbRec(LmdbDatabase db)
    {
        if (db.Dbi == Const.MAIN_DBI) return _dbMainRec;
        if (db.Dbi == Const.FREE_DBI) return _dbFreeRec;
        // Named sub-DB: find it in _subDbs.
        if (_subDbs != null)
        {
            for (int i = 0; i < _subDbs.Count; i++)
            {
                if (Db.Root((byte*)_subDbs[i].dbRec) == Db.Root(db.DbRec))
                    return (byte*)_subDbs[i].dbRec;
            }
        }
        return db.DbRec;
    }

    /// <summary>Open the free-DB (FREE_DBI) for write — used internally by the
    /// freelist layer. Returns a LmdbDatabase whose DbRec points at the txn's mutable
    /// copy of mm_dbs[FREE_DBI].</summary>
    internal LmdbDatabase OpenFreeDatabase()
    {
        var db = new LmdbDatabase(Env, Const.FREE_DBI)
        {
            DbRec = _dbFreeRec,
            InWriteTxn = true,
        };
        db.DbFlags = Db.PersistentFlags(_dbFreeRec);
        db.KeyCmp = Compare.PickKey(db.DbFlags);
        db.DupCmp = Compare.PickDup(db.DbFlags);
        return db;
    }

    public LmdbDatabase OpenDatabase(string name, DatabaseFlags flags = 0)
    {
        if (string.IsNullOrEmpty(name))
            return OpenDefaultDatabase();
        if (ReadOnly)
            return LmdbDatabase.OpenNamed(this, name, flags);
        return OpenNamedWrite(name, flags);
    }

    /// <summary>Open a named sub-database for writing. Searches the main DB for the
    /// name; if found, copies the MDB_db record into a native buffer. If not found
    /// and MDB_CREATE is set, initializes an empty sub-DB. The native buffer is
    /// written back to the main DB at commit time.</summary>
    internal LmdbDatabase OpenNamedWrite(string name, DatabaseFlags flags)
    {
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        byte* dbRec = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)Db.Size48);

        var mainDb = OpenDefaultDatabase();
        using (var cur = new LmdbCursor(this, mainDb))
        {
            if (cur.TryGet(CursorOp.Set, nameBytes, out _, out var data))
            {
                // Found: copy the MDB_db record from the main DB node data.
                if (data.Length >= Db.Size48)
                {
                    fixed (byte* dp = data)
                        System.Buffer.MemoryCopy(dp, dbRec, Db.Size48, Db.Size48);
                }
                else
                {
                    NativeMemory.Free(dbRec);
                    throw new LmdbException(LmdbErr.Corrupted, $"'{name}' DB record too small");
                }
            }
            else if ((flags & DatabaseFlags.Create) != 0)
            {
                // Not found + CREATE: initialize an empty sub-DB.
                for (int i = 0; i < Db.Size48; i++) dbRec[i] = 0;
                *(ulong*)(dbRec + 40) = Const.P_INVALID;  // md_root = P_INVALID
                *(ushort*)(dbRec + 4) = (ushort)((uint)flags & (uint)Const.PERSISTENT_FLAGS);
            }
            else
            {
                NativeMemory.Free(dbRec);
                throw new LmdbException(LmdbErr.NotFound, $"database '{name}' does not exist");
            }
        }

        var db = new LmdbDatabase(Env, Env.AllocDbi())
        {
            DbRec = dbRec,
            InWriteTxn = true,
        };
        db.DbFlags = Db.PersistentFlags(dbRec);
        db.KeyCmp = Compare.PickKey(db.DbFlags);
        db.DupCmp = Compare.PickDup(db.DbFlags);

        _subDbs ??= new();
        _subDbs.Add((nameBytes, (IntPtr)dbRec));
        return db;
    }

    /// <summary>
    /// Point lookup: find <paramref name="key"/> in <paramref name="db"/> and return
    /// its value as a <see cref="ReadOnlySpan{Byte}"/> that points directly into the
    /// memory map (zero allocation). Returns false if the key is absent.
    /// </summary>
    /// <remarks>The returned span is valid only while the transaction is active and
    /// the environment is open. Do not store it past transaction dispose.</remarks>
    public bool TryGet(LmdbDatabase db, ReadOnlySpan<byte> key, out ReadOnlySpan<byte> data)
    {
        // Reuse a cached cursor to avoid allocating a new LmdbCursor (896B) per call.
        var cur = _cachedReadCursor;
        if (cur == null || cur.Database.Dbi != db.Dbi)
        {
            cur?.Dispose();
            cur = new LmdbCursor(this, db);
            _cachedReadCursor = cur;
        }
        return cur.TryGet(CursorOp.Set, key, out _, out data);
    }

    public ReadOnlySpan<byte> Get(LmdbDatabase db, ReadOnlySpan<byte> key)
    {
        if (!TryGet(db, key, out var data))
            throw new LmdbException(LmdbErr.NotFound);
        return data;
    }

    /// <summary>Insert or update a key/value pair (mdb_put). Only for write txns.</summary>
    /// <summary>Insert or update a key/value pair. Only valid in a write transaction
    /// (see <see cref="LmdbEnvironment.BeginWriteTransaction"/>).</summary>
    public void Put(LmdbDatabase db, ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, PutFlags flags = 0)
    {
        if (ReadOnly) throw new LmdbException(LmdbErr.Invalid, "read-only transaction");
        // Reuse a cached cursor to avoid per-call allocation.
        var cur = _cachedWriteCursor;
        if (cur == null || cur.Database.Dbi != db.Dbi)
        {
            cur = new LmdbCursor(this, db);
            _cachedWriteCursor = cur;
        }
        cur.Put(key, data, flags);
        Written = true;
    }

    /// <summary>Delete a key (mdb_del). Only for write txns.</summary>
    /// <summary>Delete <paramref name="key"/>. Returns false if the key was not present.</summary>
    public bool Delete(LmdbDatabase db, ReadOnlySpan<byte> key)
    {
        if (ReadOnly) throw new LmdbException(LmdbErr.Invalid, "read-only transaction");
        var cur = _cachedWriteCursor;
        if (cur == null || cur.Database.Dbi != db.Dbi)
        {
            cur = new LmdbCursor(this, db);
            _cachedWriteCursor = cur;
        }
        bool deleted = cur.Delete(key);
        if (deleted) Written = true;
        return deleted;
    }

    public LmdbCursor CreateCursor(LmdbDatabase db) => new(this, db);

    public void Commit()
    {
        if (_finished) return;
        _finished = true;
        if (ReadOnly) { return; }
        if (Parent != null) { CommitChild(); FreeWriteState(); return; }
        if (!Written) { FreeWriteState(); return; }

        // Write back dirty named sub-DB records to the main DB (mdb_txn_commit).
        WriteSubDbRecords();

        // Save freed pages to the free-DB and consume old records into PgHead.
        FreelistSave();

        // 1) Flush dirty pages into the mmap (mdb_page_flush).
        var dirty = Dirty!;
        for (int i = 1; i <= dirty.Count; i++)
        {
            ref var e = ref dirty[i];
            byte* dst = Env.MapPage(e.Id);
            int bytes = (int)Env.PageSize;
            // Overflow pages occupy multiple contiguous pages in one dirty entry.
            if (Page.IsOverflow(e.Ptr))
                bytes = (int)Page.OverflowPages(e.Ptr) * (int)Env.PageSize;
            Buffer.MemoryCopy(e.Ptr, dst, bytes, bytes);
            // Clear P_DIRTY in the on-disk image (read pages must not carry P_DIRTY).
            PageFlags(dst) = (ushort)(PageFlags(dst) & ~(ushort)Const.P_DIRTY);
            NativeMemory.Free(e.Ptr);
        }
        Dirty = null;

        // 2) Write the meta page directly to the mmap (WRITEMAP mode).
        int toggle = (int)(TxnId & 1);
        Env.WriteMetaNoSync(toggle, _dbFreeRec, _dbMainRec, NextPgno - 1, TxnId, Env.MapSize);

        // 3) Single flush + fsync for both data pages and meta page.
        Env.FlushView();
        Env.SyncFile();

        // Publish the new txnid to the lockfile so readers can see it.
        Env.Lockfile?.UpdateLastTxnid(TxnId);

        FreeWriteState();
        // Release the writer lock.
        Env.Lockfile?.UnlockWrite();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static ref ushort PageFlags(byte* page) => ref *(ushort*)(page + 10);

    private void FreeWriteState()
    {
        if (_dbFreeRec != null) { NativeMemory.Free(_dbFreeRec); _dbFreeRec = null; }
        if (_dbMainRec != null) { NativeMemory.Free(_dbMainRec); _dbMainRec = null; }
        if (_subDbs != null)
        {
            foreach (var (_, dbRec) in _subDbs) NativeMemory.Free((void*)dbRec);
            _subDbs = null;
        }
        Dirty = null;
        FreePgs = null;
        _cachedReadCursor = null;
        _cachedWriteCursor = null;
    }

    public void Abort()
    {
        if (_finished) return;
        _finished = true;
        if (ReadOnly) return;
        if (Parent != null) { AbortChild(); FreeWriteState(); return; }
        // Free dirty-page native buffers; do not touch the mmap.
        var dirty = Dirty;
        if (dirty != null)
        {
            for (int i = 1; i <= dirty.Count; i++)
                NativeMemory.Free(dirty[i].Ptr);
        }
        FreeWriteState();
        // Release the writer lock.
        Env.Lockfile?.UnlockWrite();
    }

    public void Dispose()
    {
        if (!_finished) Abort();
        // Release the reader slot.
        if (_readerSlot >= 0)
        {
            Env.Lockfile?.ReleaseReaderSlot(_readerSlot);
            _readerSlot = -1;
        }
        GC.SuppressFinalize(this);
    }

    ~LmdbTransaction() { if (!_finished) Abort(); }
}

[Flags]
public enum PutFlags : uint
{
    None        = 0,
    NoOverwrite = Const.MDB_NOOVERWRITE,
    Current     = Const.MDB_CURRENT,
    Append      = Const.MDB_APPEND,
    Reserve     = Const.MDB_RESERVE,
}
