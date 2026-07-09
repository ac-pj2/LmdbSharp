// Transaction: a snapshot view of the environment.
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

public sealed unsafe partial class Transaction : IDisposable
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
    private System.Collections.Generic.List<(byte[] name, IntPtr dbRec)>? _subDbs;

    public LmdbEnvironment Environment => Env;
    public ulong Id => TxnId;

    internal Transaction(LmdbEnvironment env, bool readOnly)
    {
        Env = env;
        ReadOnly = readOnly;
        TxnId = env.TxnId;
        NextPgno = env.LastPg + 1;

        if (!readOnly)
        {
            Dirty = new Id2l(Idl.UmMax);
            FreePgs = new Idl(64);
            // Mutable copies of the snapshot's core DB records.
            _dbFreeRec = AllocDbRec(env.MetaPtr, Const.FREE_DBI);
            _dbMainRec = AllocDbRec(env.MetaPtr, Const.MAIN_DBI);
            TxnId = env.TxnId + 1;
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
        if (!ReadOnly && Dirty != null)
        {
            int x = Dirty.Search(pgno);
            if (x <= Dirty.Count && Dirty[x].Id == pgno)
                return Dirty[x].Ptr;
        }
        return Env.Page(pgno);
    }

    /// <summary>Open the default (unnamed) database. For write transactions the
    /// returned handle mutates the txn's DB record copy (committed at Commit()).</summary>
    public Database OpenDefaultDatabase()
    {
        if (ReadOnly)
            return Database.OpenCore(Env, Const.MAIN_DBI);

        var db = new Database(Env, Const.MAIN_DBI)
        {
            DbRec = _dbMainRec,
            InWriteTxn = true,
        };
        db.DbFlags = Db.PersistentFlags(_dbMainRec);
        db.KeyCmp = Compare.PickKey(db.DbFlags);
        db.DupCmp = Compare.PickDup(db.DbFlags);
        return db;
    }

    /// <summary>Open the free-DB (FREE_DBI) for write — used internally by the
    /// freelist layer. Returns a Database whose DbRec points at the txn's mutable
    /// copy of mm_dbs[FREE_DBI].</summary>
    internal Database OpenFreeDatabase()
    {
        var db = new Database(Env, Const.FREE_DBI)
        {
            DbRec = _dbFreeRec,
            InWriteTxn = true,
        };
        db.DbFlags = Db.PersistentFlags(_dbFreeRec);
        db.KeyCmp = Compare.PickKey(db.DbFlags);
        db.DupCmp = Compare.PickDup(db.DbFlags);
        return db;
    }

    public Database OpenDatabase(string name, DatabaseFlags flags = 0)
    {
        if (string.IsNullOrEmpty(name))
            return OpenDefaultDatabase();
        if (ReadOnly)
            return Database.OpenNamed(this, name, flags);
        return OpenNamedWrite(name, flags);
    }

    /// <summary>Open a named sub-database for writing. Searches the main DB for the
    /// name; if found, copies the MDB_db record into a native buffer. If not found
    /// and MDB_CREATE is set, initializes an empty sub-DB. The native buffer is
    /// written back to the main DB at commit time.</summary>
    internal Database OpenNamedWrite(string name, DatabaseFlags flags)
    {
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        byte* dbRec = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)Db.Size48);

        var mainDb = OpenDefaultDatabase();
        using (var cur = new Cursor(this, mainDb))
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

        var db = new Database(Env, Env.AllocDbi())
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

    public bool TryGet(Database db, ReadOnlySpan<byte> key, out ReadOnlySpan<byte> data)
    {
        using var cursor = new Cursor(this, db);
        return cursor.TryGet(CursorOp.Set, key, out _, out data);
    }

    public ReadOnlySpan<byte> Get(Database db, ReadOnlySpan<byte> key)
    {
        if (!TryGet(db, key, out var data))
            throw new LmdbException(LmdbErr.NotFound);
        return data;
    }

    /// <summary>Insert or update a key/value pair (mdb_put). Only for write txns.</summary>
    public void Put(Database db, ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, PutFlags flags = 0)
    {
        if (ReadOnly) throw new LmdbException(LmdbErr.Invalid, "read-only transaction");
        using var cursor = new Cursor(this, db);
        cursor.Put(key, data, flags);
        Written = true;
    }

    /// <summary>Delete a key (mdb_del). Only for write txns.</summary>
    public bool Delete(Database db, ReadOnlySpan<byte> key)
    {
        if (ReadOnly) throw new LmdbException(LmdbErr.Invalid, "read-only transaction");
        using var cursor = new Cursor(this, db);
        bool deleted = cursor.Delete(key);
        if (deleted) Written = true;
        return deleted;
    }

    public Cursor CreateCursor(Database db) => new(this, db);

    public void Commit()
    {
        if (ReadOnly) { return; }
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
        Env.FlushView();
        Env.SyncFile();

        // 2) Publish the new meta page (mdb_env_write_meta). toggle = txnid & 1.
        int toggle = (int)(TxnId & 1);
        Env.WriteMeta(toggle, _dbFreeRec, _dbMainRec, NextPgno - 1, TxnId, Env.MapSize);

        FreeWriteState();
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
    }

    public void Abort()
    {
        if (ReadOnly) return;
        // Free dirty-page native buffers; do not touch the mmap.
        var dirty = Dirty;
        if (dirty != null)
        {
            for (int i = 1; i <= dirty.Count; i++)
                NativeMemory.Free(dirty[i].Ptr);
        }
        FreeWriteState();
    }

    public void Dispose()
    {
        if (!ReadOnly) Abort();
        GC.SuppressFinalize(this);
    }

    ~Transaction() => Abort();
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
