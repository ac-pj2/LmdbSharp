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
    /// <summary>Set when an operation threw part-way through a structural
    /// mutation. A broken transaction must not commit — a half-applied change
    /// (e.g. old node deleted, new node's allocation failed) would be persisted
    /// as silent data loss. (MDB_TXN_ERROR.)</summary>
    internal bool Broken;
    /// <summary>The currently active child transaction, if any. The parent must
    /// not issue operations while a child is live (LMDB: BAD_TXN) — both would
    /// allocate from the same page counter and pool.</summary>
    internal LmdbTransaction? ActiveChild;

    /// <summary>Reject writes on a broken transaction or on a parent whose
    /// child is still active.</summary>
    internal void EnsureWritable()
    {
        if (Broken)
            throw new LmdbException(LmdbErr.BadTxn,
                "transaction failed an earlier operation; abort it");
        if (ActiveChild != null)
            throw new LmdbException(LmdbErr.BadTxn,
                "transaction has an active child; commit or abort it first");
    }
    /// <summary>This txn's private reusable-page pool, loaded from the free-DB
    /// records (including the persisted remainder) at txn start. A written
    /// commit deletes the consumed records and persists the surviving remainder
    /// back to the free-DB; abort and no-write commit discard the pool and
    /// leave the records untouched (see Transaction.Freelist).</summary>
    internal Idl? PgHeadLocal;
    /// <summary>Highest free-DB record key merged into PgHeadLocal (me_pglast).</summary>
    internal ulong PgLastLocal;
    /// <summary>Set during FreelistSave's write loop: allocations must not draw
    /// from the pool while its serialized remainder is being persisted.</summary>
    internal bool NoPoolAlloc;
    /// <summary>Set by Drop while removing a named DB's record from the main
    /// tree — the only caller allowed past the F_SUBDATA delete guard.</summary>
    internal bool AllowNamedRecordDelete;
    private byte* _dbFreeRec;     // mutable MDB_db for FREE_DBI (native, 48 bytes)
    private byte* _dbMainRec;     // mutable MDB_db for MAIN_DBI
    internal bool Written;        // any dirty pages exist?
    private int _readerSlot = -1;  // lockfile reader slot (-1 if none)
    internal byte* _metaPtr;       // pinned meta page (snapshot at open time)
    /// <summary>Read txns: txn-owned copy of the snapshot's core MDB_db records
    /// (2 × 48 bytes). The meta page itself is recycled by the writer two
    /// commits later, so read state must never point into it.</summary>
    private byte* _dbRecsRO;
    private ulong _snapshotTxnId;
    private ulong _snapshotLastPg;
    // Reusable cursors to avoid per-call allocation (896B per LmdbCursor).
    private LmdbCursor? _cachedReadCursor;
    private LmdbCursor? _cachedWriteCursor;
    private System.Collections.Generic.List<(byte[] name, IntPtr dbRec)>? _subDbs;
    /// <summary>Record buffers of DBs dropped this txn — kept alive (zeroed)
    /// until txn end because live handles may still read them.</summary>
    private System.Collections.Generic.List<IntPtr>? _droppedRecs;

    public LmdbEnvironment Environment => Env;
    public ulong Id => TxnId;
    /// <summary>Last page of this txn's snapshot (used by Environment.Copy).</summary>
    internal ulong SnapshotLastPg => _snapshotLastPg;
    /// <summary>Read txns: the copied core DB records (used by Environment.Copy).</summary>
    internal byte* SnapshotDbRecs => _dbRecsRO;

    internal LmdbTransaction(LmdbEnvironment env, bool readOnly, LmdbTransaction? parent = null)
    {
        Env = env;
        ReadOnly = readOnly;
        Parent = parent;
        _metaPtr = env.MetaPtr;
        _snapshotTxnId = env.TxnId;
        _snapshotLastPg = env.LastPg;
        TxnId = env.TxnId;
        NextPgno = env.LastPg + 1;

        if (readOnly)
        {
            // Register the reader slot FIRST, then pin the snapshot, then verify
            // no writer committed in between. Publishing after snapshotting left
            // a window (GC pause) where a writer could recycle this snapshot's
            // pages before the slot became visible.
            var lf = env.Lockfile;
            if (lf != null)
            {
                _readerSlot = lf.ClaimReaderSlot(env.Pid);
                if (_readerSlot < 0)
                {
                    lf.SweepStaleReaders();
                    _readerSlot = lf.ClaimReaderSlot(env.Pid);
                }
                if (_readerSlot < 0)
                    throw new LmdbException(LmdbErr.ReadersFull);
            }
            try
            {
                // The meta page this snapshot lives in is OVERWRITTEN IN PLACE by
                // the writer two commits later (toggle = txnid & 1), so a read txn
                // must COPY the core DB records at begin (C keeps mt_dbs in the
                // txn). The retry loop makes registration + copy atomic vs
                // committing writers: only the T+2 writer can touch our page, and
                // it cannot commit without env.TxnId first becoming T+1.
                _dbRecsRO = (byte*)NativeMemory.Alloc((nuint)(Const.CORE_DBS * Db.Size48));
                do
                {
                    // Pick the snapshot from the SHARED mmap, not the cached env
                    // fields — commits by other processes are only visible there.
                    _metaPtr = env.PickNewestMeta();
                    TxnId = Meta.TxnId(_metaPtr);
                    if (lf != null)
                    {
                        lf.SetReaderTxnid(_readerSlot, TxnId);
                        System.Threading.Thread.MemoryBarrier();
                    }
                    _snapshotLastPg = Meta.LastPg(_metaPtr);
                    Buffer.MemoryCopy(Meta.DbPtr(_metaPtr, 0), _dbRecsRO,
                        Const.CORE_DBS * Db.Size48, Const.CORE_DBS * Db.Size48);
                    System.Threading.Thread.MemoryBarrier();
                } while (TxnId != Meta.TxnId(env.PickNewestMeta()));
                _snapshotTxnId = TxnId;
                if ((long)(_snapshotLastPg + 1) * env.PageSize > env.MapViewSize)
                    throw new LmdbException(LmdbErr.MapResized,
                        "environment was grown by another process; reopen it");
            }
            catch
            {
                ReleaseReadState();
                throw;
            }
        }
        else
        {
            if (parent == null)
            {
                // Acquire the exclusive writer lock FIRST. Everything below
                // snapshots environment state that a concurrent writer's commit
                // changes — taking the snapshot before owning the lock means
                // building on a stale meta and silently overwriting the other
                // writer's commit.
                env.Lockfile?.LockWrite();
            }
            // Child txns run under the PARENT's writer lock — never re-acquire.

            try
            {
                if (parent == null)
                {
                    // Re-read the meta snapshot now that we own the write lock (the
                    // values captured above may predate another writer's commit —
                    // including one made by ANOTHER PROCESS, which only the shared
                    // mmap knows about).
                    env.RefreshMetaAfterLock();
                    _metaPtr = env.MetaPtr;
                    _snapshotTxnId = env.TxnId;
                    _snapshotLastPg = env.LastPg;
                    NextPgno = env.LastPg + 1;
                }

                Dirty = new Id2l(1024);   // grows on demand; avoids 2MB pre-allocation
                FreePgs = new Idl(64);
                // Mutable copies of the snapshot's core DB records.
                _dbFreeRec = AllocDbRec(_metaPtr, Const.FREE_DBI);
                _dbMainRec = AllocDbRec(_metaPtr, Const.MAIN_DBI);
                TxnId = env.TxnId + 1;
                // Eagerly load reusable pages from the free-DB so AllocPage can draw
                // from the txn's private pool (mdb_page_alloc reads the free-DB
                // lazily; we load up-front for simplicity). Children inherit a copy
                // of the parent's pool instead — consuming the records twice is the
                // double-allocation bug (FreelistIntegrityTests).
                if (Env.ReuseFreePages)
                {
                    if (parent != null)
                    {
                        PgHeadLocal = parent.PgHeadLocal?.Clone();
                        PgLastLocal = parent.PgLastLocal;
                    }
                    else
                    {
                        LoadPgHead();
                    }
                }
            }
            catch
            {
                // Constructor failure (e.g. a poisoned freelist refused by
                // LoadPgHead) must not leak the writer lock — the caller never
                // receives a transaction to dispose.
                _finished = true;
                FreeWriteState();
                if (parent == null) env.Lockfile?.UnlockWrite();
                throw;
            }
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
            return LmdbDatabase.OpenCoreFromRecord(Env, Const.MAIN_DBI,
                _dbRecsRO + Const.MAIN_DBI * Db.Size48);

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
        // Named DBs resolve BY NAME to THIS transaction's mutable record —
        // a handle opened by an ancestor must not be mutated in place (a child
        // abort could not roll that back), and root pages are not identities
        // (every empty DB shares P_INVALID).
        if (!ReadOnly && db.Name != null)
            return ResolveNamedRec(db.Name, DatabaseFlags.None);
        return db.DbRec;
    }

    /// <summary>Find or create this transaction's mutable record for a named
    /// sub-DB: (1) already open here; (2) clone an ancestor's uncommitted
    /// record; (3) copy the committed record from this txn's main tree;
    /// (4) with Create, initialize an empty record. Throws NotFound otherwise.</summary>
    private byte* ResolveNamedRec(byte[] nameBytes, DatabaseFlags flags)
    {
        if (_subDbs != null)
        {
            for (int i = 0; i < _subDbs.Count; i++)
                if (_subDbs[i].name.AsSpan().SequenceEqual(nameBytes))
                    return (byte*)_subDbs[i].dbRec;
        }
        for (var t = Parent; t != null; t = t.Parent)
        {
            if (t._subDbs == null) continue;
            for (int i = 0; i < t._subDbs.Count; i++)
                if (t._subDbs[i].name.AsSpan().SequenceEqual(nameBytes))
                    return AddSubDbRecord(nameBytes, (byte*)t._subDbs[i].dbRec);
        }

        // Committed record from this txn's snapshot of the main tree.
        var mainDb = OpenDefaultDatabase();
        using (var cur = new LmdbCursor(this, mainDb))
        {
            if (cur.TryGet(CursorOp.Set, nameBytes, out _, out var data))
            {
                if (data.Length < Db.Size48)
                    throw new LmdbException(LmdbErr.Corrupted, "named DB record too small");
                fixed (byte* dp = data)
                    return AddSubDbRecord(nameBytes, dp);
            }
        }

        if ((flags & DatabaseFlags.Create) == 0)
            throw new LmdbException(LmdbErr.NotFound,
                $"database '{System.Text.Encoding.UTF8.GetString(nameBytes)}' does not exist");

        // CREATE: initialize an empty sub-DB record. Creation alone must be
        // committable, so mark the txn written.
        byte* rec = AddSubDbRecord(nameBytes, null);
        *(ulong*)(rec + 40) = Const.P_INVALID;   // md_root
        *(ushort*)(rec + 4) = (ushort)((uint)flags & Const.PERSISTENT_FLAGS);
        Written = true;
        return rec;
    }

    private byte* AddSubDbRecord(byte[] nameBytes, byte* source)
    {
        byte* rec = (byte*)NativeMemory.Alloc((nuint)Db.Size48);
        if (source != null) Buffer.MemoryCopy(source, rec, Db.Size48, Db.Size48);
        else for (int i = 0; i < Db.Size48; i++) rec[i] = 0;
        _subDbs ??= new();
        _subDbs.Add((nameBytes, (IntPtr)rec));
        return rec;
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

    /// <summary>Open a named sub-database for writing. Resolves the name to THIS
    /// transaction's mutable record (already-open record, ancestor clone,
    /// committed record, or a fresh empty one with Create). The record is
    /// written back to the main DB at commit time.</summary>
    internal LmdbDatabase OpenNamedWrite(string name, DatabaseFlags flags)
    {
        EnsureWritable();
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        byte* dbRec = ResolveNamedRec(nameBytes, flags);

        var db = new LmdbDatabase(Env, Env.AllocDbi())
        {
            DbRec = dbRec,
            InWriteTxn = true,
            Name = nameBytes,
        };
        db.DbFlags = Db.PersistentFlags(dbRec);
        db.KeyCmp = Compare.PickKey(db.DbFlags);
        db.DupCmp = Compare.PickDup(db.DbFlags);
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
        if (!ReadOnly && Broken)
        {
            // A structural operation failed part-way; committing would persist
            // a half-applied mutation. Roll back instead. (MDB_TXN_ERROR.)
            Abort();
            throw new LmdbException(LmdbErr.BadTxn,
                "transaction failed an earlier operation and was rolled back");
        }
        if (!ReadOnly && ActiveChild != null)
        {
            Abort();
            throw new LmdbException(LmdbErr.BadTxn,
                "cannot commit: transaction has an active child");
        }
        _finished = true;
        if (ReadOnly) { ReleaseReaderSlotNow(); return; }
        if (Parent != null) { CommitChild(); FreeWriteState(); return; }
        try
        {
            if (!Written) return;   // nothing to write; finally releases the lock

            // Write back dirty named sub-DB records to the main DB (mdb_txn_commit).
            WriteSubDbRecords();

            // Save freed pages to the free-DB and delete the records consumed at
            // txn start. The surviving pool is published only after the meta page
            // is written: publishing earlier would hand out pages for a commit
            // that never became durable if the flush throws.
            if (Env.ReuseFreePages) FreelistSave();

            Env.CommitHook?.Invoke("before-flush");

            // 1) Flush dirty pages into the mmap (mdb_page_flush).
            var dirty = Dirty!;
            for (int i = 1; i <= dirty.Count; i++)
            {
                if (i == dirty.Count / 2 + 1) Env.CommitHook?.Invoke("mid-flush");
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
                e.Ptr = null;
            }

            Env.CommitHook?.Invoke("after-flush");

            // 2) Durability barrier for the data pages BEFORE the meta write.
            // With a single combined sync the OS may persist the meta page ahead
            // of the data pages it references; a power failure in that window
            // leaves a winning meta pointing at never-written pages. (C LMDB:
            // flush data, fsync, write meta, fsync.)
            Env.FlushView();
            Env.SyncFile();

            // 3) Write the meta page directly to the mmap (WRITEMAP mode).
            int toggle = (int)(TxnId & 1);
            Env.WriteMetaNoSync(toggle, _dbFreeRec, _dbMainRec, NextPgno - 1, TxnId, Env.MapSize);

            Env.CommitHook?.Invoke("after-meta");

            // 4) Make the meta page durable.
            Env.FlushView();
            Env.SyncFile();

            // Publish the new txnid to the lockfile so readers can see it.
            Env.Lockfile?.UpdateLastTxnid(TxnId);
        }
        finally
        {
            FreeDirtyBuffers();
            FreeWriteState();
            // Release the writer lock on every path — a mid-commit exception must
            // not leave the cross-process lock held forever. The mmap may hold
            // flushed pages for the failed txn, but the meta page still points at
            // the previous snapshot, so the environment stays consistent.
            Env.Lockfile?.UnlockWrite();
        }
    }

    /// <summary>Free any dirty-page native buffers not yet released (abort, or an
    /// exception part-way through the commit flush loop).</summary>
    private void FreeDirtyBuffers()
    {
        var dirty = Dirty;
        if (dirty == null) return;
        for (int i = 1; i <= dirty.Count; i++)
        {
            if (dirty[i].Ptr != null)
            {
                NativeMemory.Free(dirty[i].Ptr);
                dirty[i].Ptr = null;
            }
        }
        Dirty = null;
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
        if (_droppedRecs != null)
        {
            foreach (var rec in _droppedRecs) NativeMemory.Free((void*)rec);
            _droppedRecs = null;
        }
        Dirty = null;
        FreePgs = null;
        PgHeadLocal = null;
        _cachedReadCursor = null;
        _cachedWriteCursor = null;
    }

    public void Abort()
    {
        if (_finished) return;
        _finished = true;
        if (ReadOnly) { ReleaseReaderSlotNow(); return; }
        // An abandoned child cannot outlive its parent's write state.
        ActiveChild?.Abort();
        if (Parent != null) { AbortChild(); FreeWriteState(); return; }
        // Free dirty-page native buffers; do not touch the mmap.
        FreeDirtyBuffers();
        FreeWriteState();
        // Release the writer lock.
        Env.Lockfile?.UnlockWrite();
    }

    public void Dispose()
    {
        if (!_finished) Abort();
        ReleaseReadState();
        GC.SuppressFinalize(this);
    }

    /// <summary>Release the reader slot immediately (Commit/Abort of a read txn
    /// must stop pinning `oldest` even when the caller never Disposes).</summary>
    private void ReleaseReaderSlotNow()
    {
        if (_readerSlot >= 0)
        {
            try { Env.Lockfile?.ReleaseReaderSlot(_readerSlot); } catch { }
            _readerSlot = -1;
        }
    }

    /// <summary>Release the reader slot and the snapshot record copy. The copy
    /// stays alive until Dispose so database handles remain readable after
    /// Commit within a using-scope.</summary>
    private void ReleaseReadState()
    {
        ReleaseReaderSlotNow();
        if (_dbRecsRO != null)
        {
            NativeMemory.Free(_dbRecsRO);
            _dbRecsRO = null;
        }
    }

    ~LmdbTransaction()
    {
        if (!_finished) { try { Abort(); } catch { } }
        ReleaseReadState();
    }
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
