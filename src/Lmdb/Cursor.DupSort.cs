// DUPSORT: sorted duplicate values per key.
//
// When a DB has MDB_DUPSORT, each key can have multiple data values. The duplicates
// are stored either as a sub-page (P_SUBP — a small leaf page inline in the node's
// data) or as a sub-DB (F_SUBDATA — a full B+tree with its own root). The xcursor
// is a sub-cursor that traverses the duplicates for the current key.
//
// This file implements the xcursor infrastructure (init0/init1) and the dup-aware
// cursor read ops (First/Last/Next/Prev with DUPSORT, NextDup, NextNoDup, GetBoth).
//
// Ports mdb_xcursor_init0 / mdb_xcursor_init1 and the DUPSORT branches of
// mdb_cursor_first / mdb_cursor_last / mdb_cursor_next / mdb_cursor_prev /
// mdb_cursor_set from mdb.c.
using System.Runtime.InteropServices;

namespace Lmdb;

public sealed unsafe partial class LmdbCursor
{
    // --- xcursor (sub-cursor for DUPSORT) ---
    private LmdbCursor? _xc;           // sub-cursor for dupdata traversal
    private byte* _mxDbRec;        // native MDB_db for the xcursor's sub-DB
    private bool _isSub;           // true: this cursor operates on a sub-page (depth 1, no siblings)

    internal bool HasDupSort => _db.DupCmp != null;

    /// <summary>Lazy-allocate the xcursor and its native MDB_db record.</summary>
    private void EnsureXCursor()
    {
        if (_xc != null) return;
        _mxDbRec = (byte*)Mem.Alloc((nuint)Db.Size48);
        _xc = new LmdbCursor(_txn, _db) { _isSub = false };
        _xc._db = new LmdbDatabase(_txn.Env, _db.Dbi)
        {
            DbRec = _mxDbRec,
            InWriteTxn = _db.InWriteTxn,
        };
    }

    /// <summary>Initialize the xcursor's static fields (mdb_xcursor_init0).
    /// Called once when the xcursor is created.</summary>
    private void XCursorInit0()
    {
        EnsureXCursor();
        // The dup comparator comes from the parent DB's DupCmp.
        _xc!._db.KeyCmp = _db.DupCmp!;
        _xc._db.DupCmp = null;   // sub-cursors don't have their own xcursor
        _xc._db.DbFlags = _db.DbFlags;   // carry INTEGERDUP/REVERSEDUP for cmp selection
    }

    /// <summary>Initialize the xcursor from a F_DUPDATA leaf node (mdb_xcursor_init1).
    /// Sets up the sub-DB record (for F_SUBDATA) or the sub-page pointer (for inline
    /// sub-pages). After this, the xcursor is ready for cursor_first/last/next/prev.</summary>
    private void XCursorInit1(byte* leaf)
    {
        if (_xc == null) XCursorInit0();
        var xc = _xc!;

        ushort nodeFlags = Node.Flags(leaf);
        if ((nodeFlags & Const.F_SUBDATA) != 0)
        {
            // Sub-DB: copy the MDB_db record from the node's data.
            Buffer.MemoryCopy(Node.Data(leaf), _mxDbRec, Db.Size48, Db.Size48);
            xc._db.DbFlags = Db.PersistentFlags(_mxDbRec);
            // KeyCmp stays as set by XCursorInit0 (parent's DupCmp).
            xc._pg[0] = null;
            xc._snum = 0;
            xc._top = -1;
            xc._flags = CursorFlags.None;
            xc._isSub = false;
        }
        else
        {
            // Sub-page: the node's data IS the sub-page (P_SUBP leaf).
            byte* fp = Node.Data(leaf);
            // Build a fake MDB_db for the sub-page.
            *(uint*)(_mxDbRec + 0) = 0;                         // md_pad
            *(ushort*)(_mxDbRec + 4) = 0;                       // md_flags (set below for DUPFIXED)
            *(ushort*)(_mxDbRec + 6) = 1;                       // md_depth = 1
            *(ulong*)(_mxDbRec + 8) = 0;                        // md_branch_pages
            *(ulong*)(_mxDbRec + 16) = 1;                       // md_leaf_pages = 1
            *(ulong*)(_mxDbRec + 24) = 0;                       // md_overflow_pages
            *(ulong*)(_mxDbRec + 32) = (ulong)Page.NumKeys(fp); // md_entries
            // md_root = sub-page's pgno (= parent page's pgno, for COW tracking)
            *(ulong*)(_mxDbRec + 40) = Page.Pgno(fp);

            // DUPFIXED: carry the fixed value size in md_pad and set MDB_DUPFIXED.
            if ((_db.DbFlags & (ushort)Const.MDB_DUPFIXED) != 0)
            {
                *(uint*)(_mxDbRec + 0) = Page.Pad(fp);          // ksize for LEAF2
                *(ushort*)(_mxDbRec + 4) = (ushort)Const.MDB_DUPFIXED;
                if ((_db.DbFlags & (ushort)Const.MDB_INTEGERDUP) != 0)
                    *(ushort*)(_mxDbRec + 4) |= (ushort)Const.MDB_INTEGERKEY;
            }

            xc._db.DbFlags = Db.PersistentFlags(_mxDbRec);
            // KeyCmp stays as set by XCursorInit0 (parent's DupCmp).
            xc._pg[0] = fp;   // sub-cursor operates directly on the sub-page
            xc._snum = 1;
            xc._top = 0;
            xc._ki[0] = 0;
            xc._flags = CursorFlags.Initialized;
            xc._isSub = true;
        }
    }

    /// <summary>Read the current dup value from the xcursor into the data output.
    /// For DUPSORT, the "data" is the xcursor's current "key" (dup values are stored
    /// as keys in the sub-tree). Returns false if the xcursor is empty.</summary>
    private bool ReadCurrentDup(out ReadOnlySpan<byte> data)
    {
        var xc = _xc!;
        if (xc._pg[0] == null || xc._snum == 0)
        {
            data = default;
            return false;
        }
        byte* mp = xc._pg[xc._top];
        if (Page.IsLeaf2(mp))
        {
            int ks = (int)Db.Pad(xc._db.DbRec);
            byte* k = Page.Leaf2Key(mp, xc._ki[xc._top], ks);
            data = new ReadOnlySpan<byte>(k, ks);
        }
        else
        {
            byte* node = Page.NodePtr(mp, xc._ki[xc._top]);
            data = new ReadOnlySpan<byte>(Node.Key(node), Node.KSize(node));
        }
        return true;
    }

    // --- DUPSORT-aware cursor ops ---

    /// <summary>Position the xcursor at the first dup of the current key.</summary>
    private bool DupFirst(out ReadOnlySpan<byte> data)
    {
        var xc = _xc!;
        if (!xc.FirstDup(out var k)) { data = default; return false; }
        data = k;
        return true;
    }

    /// <summary>Position the xcursor at the last dup of the current key.</summary>
    private bool DupLast(out ReadOnlySpan<byte> data)
    {
        var xc = _xc!;
        if (!xc.LastDup(out var k)) { data = default; return false; }
        data = k;
        return true;
    }

    /// <summary>Advance the xcursor to the next dup. Returns false at end of dups.</summary>
    private bool DupNext(out ReadOnlySpan<byte> data)
    {
        var xc = _xc!;
        if (!xc.NextDupInternal(out var k)) { data = default; return false; }
        data = k;
        return true;
    }

    /// <summary>Advance the xcursor to the previous dup. Returns false at start of dups.</summary>
    private bool DupPrev(out ReadOnlySpan<byte> data)
    {
        var xc = _xc!;
        if (!xc.PrevDupInternal(out var k)) { data = default; return false; }
        data = k;
        return true;
    }

    // --- sub-cursor leaf ops (operate on the sub-page or sub-DB) ---

    private bool FirstDup(out ReadOnlySpan<byte> key)
    {
        key = default;
        if (_isSub)
        {
            // Sub-page: directly position at node 0.
            byte* mp = _pg[0];
            if (Page.NumKeys(mp) == 0) return false;
            _ki[0] = 0;
            _flags |= CursorFlags.Initialized;
            _flags &= ~CursorFlags.Eof;
            return ReadSubKey(out key);
        }
        // Sub-DB: descend to first leaf.
        return First(out key, out _);
    }

    private bool LastDup(out ReadOnlySpan<byte> key)
    {
        key = default;
        if (_isSub)
        {
            byte* mp = _pg[0];
            int n = Page.NumKeys(mp);
            if (n == 0) return false;
            _ki[0] = n - 1;
            _flags |= CursorFlags.Initialized;
            _flags &= ~CursorFlags.Eof;
            return ReadSubKey(out key);
        }
        return Last(out key, out _);
    }

    private bool NextDupInternal(out ReadOnlySpan<byte> key)
    {
        key = default;
        if (_isSub)
        {
            if ((_flags & CursorFlags.Initialized) == 0) return false;
            byte* mp = _pg[0];
            if ((_flags & CursorFlags.Deleted) != 0)
            {
                // C_DEL on the dup position: the slot already holds the next dup.
                _flags &= ~CursorFlags.Deleted;
                if (_ki[0] < Page.NumKeys(mp)) return ReadSubKey(out key);
                return false;
            }
            if (_ki[0] + 1 >= Page.NumKeys(mp)) return false;
            _ki[0]++;
            return ReadSubKey(out key);
        }
        return Next(out key, out _);
    }

    private bool PrevDupInternal(out ReadOnlySpan<byte> key)
    {
        key = default;
        if (_isSub)
        {
            if ((_flags & CursorFlags.Initialized) == 0) return false;
            _flags &= ~CursorFlags.Deleted;   // deleted current: prev is ki-1 as usual
            if (_ki[0] == 0) return false;
            _ki[0]--;
            return ReadSubKey(out key);
        }
        return Prev(out key, out _);
    }

    /// <summary>Read the sub-cursor's current "key" (which is the dupdata value).</summary>
    /// <summary>Delete the current dup value from the xcursor's sub-tree.
    /// For sub-pages (inline), removes the specific entry. For sub-DBs, delegates
    /// to the full delete+rebalance path. Decrements the sub-DB entry count.</summary>
    internal void DeleteCurrentDup()
    {
        if (_isSub && _pg[0] != null)
        {
            // Sub-page: delete the specific dup value at the current index.
            NodeDel(0);
            // Other parked xcursors on the same sub-page slide/flag (the
            // sub-page pointer is shared within the txn's dirty leaf).
            FixupDelete(_pg[0], _ki[0]);
            // Use _db.DbRec which points to the xcursor's MDB_db record.
            Db.SetEntries(_db.DbRec, (ulong)Page.NumKeys(_pg[0]));
            return;
        }

        // Sub-DB: the xcursor's current position IS the dup value.
        if (_snum > 0 && _top >= 0)
        {
            // COW the sub-tree path first — the cursor was positioned on
            // committed pages, and NodeDel on those would mutate the durable
            // snapshot in place instead of this transaction's copy.
            int t = TouchPath();
            if (t != 0) throw new LmdbException(LmdbErr.Problem, "xcursor touch failed");
            NodeDel(0);
            FixupDelete(_pg[_top], _ki[_top]);
            Db.SetEntries(_db.DbRec, Db.Entries(_db.DbRec) - 1);
            int rc = Rebalance();
            if (rc != 0) throw new LmdbException((LmdbErr)rc, "xcursor rebalance failed");
        }
    }

    private bool ReadSubKey(out ReadOnlySpan<byte> key)
    {
        byte* mp = _pg[_top];
        if (Page.IsLeaf2(mp))
        {
            int ks = (int)Db.Pad(_db.DbRec);
            key = new ReadOnlySpan<byte>(Page.Leaf2Key(mp, _ki[_top], ks), ks);
        }
        else
        {
            byte* node = Page.NodePtr(mp, _ki[_top]);
            key = new ReadOnlySpan<byte>(Node.Key(node), Node.KSize(node));
        }
        return true;
    }
}
