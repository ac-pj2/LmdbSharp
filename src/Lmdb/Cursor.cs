// Cursor: B+tree traversal for a single database within a transaction.
//
// Ports the read-path of mdb.c: mdb_page_search / mdb_page_search_root /
// mdb_node_search / mdb_cursor_sibling and the cursor ops (first/last/next/prev/
// set/set_range). DUPSORT (xcursor) and write ops arrive later; this covers the
// default and INTEGERKEY/REVERSEKEY single-value trees, which is enough to read
// any standard LMDB main database.
//
// The cursor keeps a stack of (page, key-index) pairs from root to current leaf,
// exactly like MDB_cursor.mc_pg[] / mc_ki[]. Pages are pointers into the mmap.
using System.Runtime.CompilerServices;

namespace Lmdb;

public enum CursorOp
{
    First, Last, Next, Prev,
    Set,        // require exact key
    SetRange,   // position at first key >= given key
    // The remaining standard LMDB ops are reserved for the DUPSORT layer:
    GetBoth, GetBothRange, SetKey,
    NextDup, PrevDup, NextNoDup, PrevNoDup,
    GetMultiple, NextMultiple, FirstMultiple, LastMultiple,
}

internal static class PageSearchFlags
{
    public const int Modify = 1;
    public const int First  = 2;
    public const int Last   = 4;
    public const int RootOnly = 8;
}

[Flags]
internal enum CursorFlags : ushort
{
    None        = 0,
    Initialized = 0x01,
    Eof         = 0x02,
    Deleted     = 0x10,
}

public sealed unsafe partial class Cursor : IDisposable
{
    // B+tree depth is bounded: min 2 keys/node, max key 511 bytes, page 4096
    // → at most ~16 levels for a 2^64 entry tree. 32 is ample and saves 512B per Cursor.
    private const int MaxDepth = 32;

    private readonly Transaction _txn;
    private Database _db;
    private readonly byte*[] _pg = new byte*[MaxDepth];
    private readonly int[] _ki = new int[MaxDepth];
    private int _top = -1;       // index of current page; -1 when stack empty
    private int _snum;           // number of pages on the stack (_top+1)
    private CursorFlags _flags;

    public Transaction Transaction => _txn;
    public Database Database => _db;

    internal Cursor(Transaction txn, Database db)
    {
        _txn = txn;
        _db = db;
        // For write txns, use the txn's own DB record copy (not the Database's).
        // This ensures nested txns see their own COW state.
        if (!txn.ReadOnly && db.InWriteTxn)
            _db = new Database(txn.Env, db.Dbi)
            {
                DbRec = txn.ResolveDbRec(db),
                InWriteTxn = true,
                DbFlags = db.DbFlags,
                KeyCmp = db.KeyCmp,
                DupCmp = db.DupCmp,
            };
    }

    /// <summary>Position the cursor and read the current key/data. Returns false at EOF
    /// (MDB_NOTFOUND). Key/data spans point into the mmap and stay valid while the
    /// environment is open and the transaction is active.</summary>
    public bool TryGet(CursorOp op, ReadOnlySpan<byte> key,
        out ReadOnlySpan<byte> keyOut, out ReadOnlySpan<byte> data)
    {
        keyOut = default; data = default;

        switch (op)
        {
            case CursorOp.First: return First(out keyOut, out data);
            case CursorOp.Last:  return Last(out keyOut, out data);
            case CursorOp.Next:  return Next(out keyOut, out data);
            case CursorOp.Prev:  return Prev(out keyOut, out data);
            case CursorOp.Set:
            case CursorOp.SetRange:
            case CursorOp.SetKey:
                if (key.IsEmpty)
                    throw new LmdbException(LmdbErr.BadValsize, "key is empty");
                fixed (byte* kp = key)
                    return Set(op, kp, key.Length, out keyOut, out data) == 0;
            case CursorOp.NextDup:
                return NextDup(out keyOut, out data);
            case CursorOp.PrevDup:
                return PrevDup(out keyOut, out data);
            case CursorOp.NextNoDup:
                return NextNoDup(out keyOut, out data);
            case CursorOp.PrevNoDup:
                return PrevNoDup(out keyOut, out data);
            default:
                throw new NotSupportedException(
                    $"Cursor op {op} is not yet implemented.");
        }
    }

    /// <summary>TryGet overload for GET_BOTH / GET_BOTH_RANGE (requires a data value).</summary>
    public bool TryGet(CursorOp op, ReadOnlySpan<byte> key, ReadOnlySpan<byte> data,
        out ReadOnlySpan<byte> keyOut, out ReadOnlySpan<byte> dataOut)
    {
        keyOut = default; dataOut = default;
        if (op != CursorOp.GetBoth && op != CursorOp.GetBothRange)
            throw new ArgumentException($"op {op} does not take a data argument");
        if (key.IsEmpty)
            throw new LmdbException(LmdbErr.BadValsize, "key is empty");
        fixed (byte* kp = key, dp = data)
            return GetBoth(op, kp, key.Length, dp, data.Length, out keyOut, out dataOut) == 0;
    }

    // --- DUPSORT-specific cursor ops ---

    public bool NextDup(out ReadOnlySpan<byte> keyOut, out ReadOnlySpan<byte> data)
    {
        keyOut = default; data = default;
        if (!HasDupSort || _xc == null) return false;
        byte* leaf = Page.NodePtr(_pg[_top], _ki[_top]);
        if ((Node.Flags(leaf) & Const.F_DUPDATA) == 0) return false;
        if (!DupNext(out data)) return false;
        keyOut = new ReadOnlySpan<byte>(Node.Key(leaf), Node.KSize(leaf));
        return true;
    }

    public bool PrevDup(out ReadOnlySpan<byte> keyOut, out ReadOnlySpan<byte> data)
    {
        keyOut = default; data = default;
        if (!HasDupSort || _xc == null) return false;
        byte* leaf = Page.NodePtr(_pg[_top], _ki[_top]);
        if ((Node.Flags(leaf) & Const.F_DUPDATA) == 0) return false;
        if (!DupPrev(out data)) return false;
        keyOut = new ReadOnlySpan<byte>(Node.Key(leaf), Node.KSize(leaf));
        return true;
    }

    public bool NextNoDup(out ReadOnlySpan<byte> keyOut, out ReadOnlySpan<byte> data)
    {
        // Skip remaining dups and advance to the next main key directly.
        keyOut = default; data = default;
        if ((_flags & CursorFlags.Initialized) == 0)
            return First(out keyOut, out data);
        if (!AdvanceMain(forward: true)) return false;
        return ReadCurrent(out keyOut, out data, lastDup: false);
    }

    public bool PrevNoDup(out ReadOnlySpan<byte> keyOut, out ReadOnlySpan<byte> data)
    {
        keyOut = default; data = default;
        if ((_flags & CursorFlags.Initialized) == 0)
        {
            if (!Last(out keyOut, out data)) return false;
            _ki[_top]++;
        }
        _flags &= ~CursorFlags.Eof;
        if (!AdvanceMain(forward: false)) return false;
        return ReadCurrent(out keyOut, out data, lastDup: true);
    }

    /// <summary>Advance the main cursor to the next/prev key, ignoring dups.</summary>
    private bool AdvanceMain(bool forward)
    {
        byte* mp = _pg[_top];
        if (forward)
        {
            if (_ki[_top] + 1 >= Page.NumKeys(mp))
            {
                if (Sibling(moveRight: true) != 0) { _flags |= CursorFlags.Eof; return false; }
            }
            else _ki[_top]++;
        }
        else
        {
            if (_ki[_top] == 0)
            {
                if (Sibling(moveRight: false) != 0) return false;
                _ki[_top] = Page.NumKeys(_pg[_top]) - 1;
            }
            else _ki[_top]--;
        }
        return true;
    }

    /// <summary>Position at key+data (MDB_GET_BOTH) or key+data range (GET_BOTH_RANGE).</summary>
    private int GetBoth(CursorOp op, byte* keyPtr, int keyLen, byte* dataPtr, int dataLen,
        out ReadOnlySpan<byte> keyOut, out ReadOnlySpan<byte> dataOut)
    {
        keyOut = default; dataOut = default;
        _pg[0] = null;
        int rc = PageSearch(keyPtr, keyLen, 0);
        if (rc != 0) return rc;
        NodeSearch(keyPtr, keyLen, out bool exact);
        if (!exact) return (int)LmdbErr.NotFound;

        byte* leaf = Page.NodePtr(_pg[_top], _ki[_top]);
        _flags |= CursorFlags.Initialized;
        _flags &= ~CursorFlags.Eof;

        if ((Node.Flags(leaf) & Const.F_DUPDATA) == 0)
        {
            // No dups: compare data directly.
            byte* dp = Node.Data(leaf);
            int dl = (int)Node.Dsz(leaf);
            int cmp = _db.DupCmp!(dataPtr, dataLen, dp, dl);
            if (cmp != 0 && (op == CursorOp.GetBoth || cmp > 0))
                return (int)LmdbErr.NotFound;
            keyOut = new ReadOnlySpan<byte>(Node.Key(leaf), Node.KSize(leaf));
            dataOut = new ReadOnlySpan<byte>(dp, dl);
            return 0;
        }

        // DUPSORT: search the xcursor for the specific data value.
        XCursorInit1(leaf);
        var xc = _xc!;
        if (xc._isSub)
        {
            // Sub-page: binary search for the dup value.
            byte* sp = xc._pg[0];
            int nkeys = Page.NumKeys(sp);
            var dcmp = _db.DupCmp!;
            int lo = 0, hi = nkeys - 1, foundRc = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                byte* nk = Page.IsLeaf2(sp)
                    ? Page.Leaf2Key(sp, mid, (int)Db.Pad(xc._db.DbRec))
                    : Node.Key(Page.NodePtr(sp, mid));
                int nlen = Page.IsLeaf2(sp)
                    ? (int)Db.Pad(xc._db.DbRec)
                    : Node.KSize(Page.NodePtr(sp, mid));
                foundRc = dcmp(dataPtr, dataLen, nk, nlen);
                if (foundRc == 0) { xc._ki[0] = mid; break; }
                if (foundRc > 0) lo = mid + 1; else hi = mid - 1;
            }
            if (op == CursorOp.GetBoth && foundRc != 0)
                return (int)LmdbErr.NotFound;
            if (foundRc != 0) xc._ki[0] = lo;   // GET_BOTH_RANGE: position at first >= data
            if (xc._ki[0] >= nkeys) return (int)LmdbErr.NotFound;
        }
        else
        {
            // Sub-DB: use cursor_set on the xcursor. Allocate on stack to avoid GC.
            byte* dp = stackalloc byte[dataLen];
            System.Buffer.MemoryCopy(dataPtr, dp, dataLen, dataLen);
            if (!xc.TryGet(CursorOp.SetRange, new ReadOnlySpan<byte>(dp, dataLen), out _, out var _))
                return (int)LmdbErr.NotFound;
        }
        keyOut = new ReadOnlySpan<byte>(Node.Key(leaf), Node.KSize(leaf));
        ReadCurrentDup(out dataOut);
        return 0;
    }

    // ---------------- cursor ops ----------------

    public bool First(out ReadOnlySpan<byte> keyOut, out ReadOnlySpan<byte> data)
    {
        keyOut = default; data = default;
        int rc = PageSearch(null, 0, PageSearchFlags.First);
        if (rc != 0) return false;
        _ki[_top] = 0;
        _flags |= CursorFlags.Initialized;
        _flags &= ~CursorFlags.Eof;
        return ReadCurrent(out keyOut, out data, lastDup: false);
    }

    public bool Last(out ReadOnlySpan<byte> keyOut, out ReadOnlySpan<byte> data)
    {
        keyOut = default; data = default;
        int rc = PageSearch(null, 0, PageSearchFlags.Last);
        if (rc != 0) return false;
        _ki[_top] = Page.NumKeys(_pg[_top]) - 1;
        _flags |= CursorFlags.Initialized;
        _flags &= ~CursorFlags.Eof;
        return ReadCurrent(out keyOut, out data, lastDup: true);
    }

    public bool Next(out ReadOnlySpan<byte> keyOut, out ReadOnlySpan<byte> data)
    {
        keyOut = default; data = default;
        if ((_flags & CursorFlags.Initialized) == 0)
            return First(out keyOut, out data);

        // DUPSORT: try advancing the xcursor first (MDB_NEXT_DUP behavior within MDB_NEXT).
        if (HasDupSort)
        {
            byte* leaf = Page.NodePtr(_pg[_top], _ki[_top]);
            if ((Node.Flags(leaf) & Const.F_DUPDATA) != 0 && _xc != null)
            {
                if (DupNext(out data))
                {
                    keyOut = new ReadOnlySpan<byte>(Node.Key(leaf), Node.KSize(leaf));
                    return true;
                }
                // xcursor exhausted: fall through to advance main cursor.
            }
        }

        byte* mp = _pg[_top];
        if ((_flags & CursorFlags.Eof) != 0)
        {
            if (_ki[_top] >= Page.NumKeys(mp) - 1) return false;
            _flags &= ~CursorFlags.Eof;
        }

        if (_ki[_top] + 1 >= Page.NumKeys(mp))
        {
            if (Sibling(moveRight: true) != 0) { _flags |= CursorFlags.Eof; return false; }
            mp = _pg[_top];
        }
        else
        {
            _ki[_top]++;
        }
        return ReadCurrent(out keyOut, out data, lastDup: false);
    }

    public bool Prev(out ReadOnlySpan<byte> keyOut, out ReadOnlySpan<byte> data)
    {
        keyOut = default; data = default;
        if ((_flags & CursorFlags.Initialized) == 0)
        {
            if (!Last(out keyOut, out data)) return false;
            _ki[_top]++;
        }

        // DUPSORT: try advancing the xcursor backwards first.
        if (HasDupSort)
        {
            byte* leaf = Page.NodePtr(_pg[_top], _ki[_top]);
            if ((Node.Flags(leaf) & Const.F_DUPDATA) != 0 && _xc != null)
            {
                if (DupPrev(out data))
                {
                    keyOut = new ReadOnlySpan<byte>(Node.Key(leaf), Node.KSize(leaf));
                    return true;
                }
                // xcursor exhausted: fall through to advance main cursor backwards.
            }
        }

        _flags &= ~CursorFlags.Eof;

        if (_ki[_top] == 0)
        {
            if (Sibling(moveRight: false) != 0) return false;
            _ki[_top] = Page.NumKeys(_pg[_top]) - 1;
        }
        else
        {
            _ki[_top]--;
        }
        return ReadCurrent(out keyOut, out data, lastDup: true);
    }

    // mdb_cursor_set (simplified: always re-descend rather than reusing the cached page).
    private int Set(CursorOp op, byte* keyPtr, int keyLen,
        out ReadOnlySpan<byte> keyOut, out ReadOnlySpan<byte> data)
    {
        keyOut = default; data = default;
        _pg[0] = null;

        int rc = PageSearch(keyPtr, keyLen, 0);
        if (rc != 0) return rc;

        byte* leaf = NodeSearch(keyPtr, keyLen, out bool exact);
        if (op == CursorOp.Set && !exact)
            return (int)LmdbErr.NotFound;   // MDB_SET requires an exact match

        if (leaf == null)
        {
            // Inexact: step to the next leaf page to find the first larger key.
            if (Sibling(moveRight: true) != 0) { _flags |= CursorFlags.Eof; return (int)LmdbErr.NotFound; }
            leaf = Page.NodePtr(_pg[_top], 0);
        }

        _flags |= CursorFlags.Initialized;
        _flags &= ~CursorFlags.Eof;
        ReadCurrent(out keyOut, out data);
        return 0;
    }

    // ---------------- B+tree descent (mdb_page_search* / mdb_node_search) ----------------

    /// <summary>Search for the page containing key (or first/last page). Pushes the
    /// root-to-leaf path onto the stack. Returns 0 / MDB error.</summary>
    private int PageSearch(byte* keyPtr, int keyLen, int flags)
    {
        ulong root = _db.Root;
        if (root == Const.P_INVALID) return (int)LmdbErr.NotFound;   // empty tree

        byte* mp = _txn.GetPage(root);
        _pg[0] = mp;
        _snum = 1;
        _top = 0;

        return PageSearchRoot(keyPtr, keyLen, flags);
    }

    private int PageSearchRoot(byte* keyPtr, int keyLen, int flags)
    {
        byte* mp = _pg[_top];
        while (Page.IsBranch(mp))
        {
            int i;
            if ((flags & (PageSearchFlags.First | PageSearchFlags.Last)) != 0)
            {
                i = 0;
                if ((flags & PageSearchFlags.Last) != 0)
                    i = Page.NumKeys(mp) - 1;
            }
            else
            {
                NodeSearch(keyPtr, keyLen, out bool exact);
                i = _ki[_top];
                if (!exact)
                {
                    if (i == 0)
                        return (int)LmdbErr.Corrupted;   // branch invariant: i>0 for inexact
                    i--;
                }
            }

            if (i >= Page.NumKeys(mp))
                return (int)LmdbErr.Corrupted;

            byte* branchNode = Page.NodePtr(mp, i);
            byte* child = _txn.GetPage(Node.Pgno(branchNode));
            _ki[_top] = i;
            Push(child);
            mp = child;
        }

        if (!Page.IsLeaf(mp))
            return (int)LmdbErr.Corrupted;

        _flags |= CursorFlags.Initialized;
        _flags &= ~CursorFlags.Eof;
        return 0;
    }

    /// <summary>Binary search within the current page (mdb_node_search). Stores the
    /// resulting key index in _ki[_top] and returns the node pointer (null if the
    /// key is beyond all entries on the page).</summary>
    private byte* NodeSearch(byte* keyPtr, int keyLen, out bool exact)
    {
        byte* mp = _pg[_top];
        int nkeys = Page.NumKeys(mp);
        int low = Page.IsLeaf(mp) ? 0 : 1;   // branch pages skip index 0
        int high = nkeys - 1;
        CmpPtr cmp = _db.KeyCmp;

        int rc = 0;
        int i = 0;
        byte* node = null;

        if (Page.IsLeaf2(mp))
        {
            int ks = (int)Db.Pad(_db.DbRec);
            while (low <= high)
            {
                i = (low + high) >> 1;
                byte* nk = Page.Leaf2Key(mp, i, ks);
                rc = cmp(keyPtr, keyLen, nk, ks);
                if (rc == 0) break;
                if (rc > 0) low = i + 1; else high = i - 1;
            }
        }
        else
        {
            while (low <= high)
            {
                i = (low + high) >> 1;
                node = Page.NodePtr(mp, i);
                rc = cmp(keyPtr, keyLen, Node.Key(node), Node.KSize(node));
                if (rc == 0) break;
                if (rc > 0) low = i + 1; else high = i - 1;
            }
        }

        if (rc > 0)   // found entry is less than key; advance to next
        {
            i++;
            if (!Page.IsLeaf2(mp))
                node = i < nkeys ? Page.NodePtr(mp, i) : null;
        }

        exact = rc == 0 && nkeys > 0;
        _ki[_top] = i;
        return i >= nkeys ? null : node;
    }

    /// <summary>Move to a sibling leaf page (mdb_cursor_sibling). moveRight=true for
    /// NEXT, false for PREV. Returns 0 / MDB_NOTFOUND.</summary>
    private int Sibling(bool moveRight)
    {
        if (_snum < 2) return (int)LmdbErr.NotFound;   // root has no siblings

        Pop();

        int ki = _ki[_top];
        int nkeys = Page.NumKeys(_pg[_top]);
        if (moveRight ? (ki + 1 >= nkeys) : (ki == 0))
        {
            int rc = Sibling(moveRight);
            if (rc != 0) { _top++; _snum++; return rc; }   // undo the pop
        }
        else
        {
            _ki[_top] = moveRight ? ki + 1 : ki - 1;
        }

        byte* branchNode = Page.NodePtr(_pg[_top], _ki[_top]);
        byte* child = _txn.GetPage(Node.Pgno(branchNode));
        Push(child);
        if (!moveRight) _ki[_top] = Page.NumKeys(child) - 1;
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Push(byte* mp) { _top++; _pg[_top] = mp; _ki[_top] = 0; _snum = _top + 1; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Pop()
    {
        if (_snum > 0)
        {
            _snum--;
            if (_snum > 0) _top--;
            else { _top = -1; _flags &= ~CursorFlags.Initialized; }
        }
    }

    // ---------------- node reading ----------------

    /// <summary>Read the leaf node at the current cursor position (mdb_node_read +
    /// MDB_GET_KEY). Handles F_BIGDATA overflow pages and F_DUPDATA (via xcursor).
    /// <paramref name="lastDup"/>: for Prev, read the last dup instead of the first.</summary>
    private bool ReadCurrent(out ReadOnlySpan<byte> keyOut, out ReadOnlySpan<byte> data, bool lastDup = false)
    {
        byte* mp = _pg[_top];
        if (Page.IsLeaf2(mp))
        {
            int ks = (int)Db.Pad(_db.DbRec);
            byte* k = Page.Leaf2Key(mp, _ki[_top], ks);
            keyOut = new ReadOnlySpan<byte>(k, ks);
            data = default;
            return true;
        }

        byte* leaf = Page.NodePtr(mp, _ki[_top]);
        keyOut = new ReadOnlySpan<byte>(Node.Key(leaf), Node.KSize(leaf));

        // DUPSORT: init xcursor and read the first (or last) dup value.
        if ((Node.Flags(leaf) & Const.F_DUPDATA) != 0)
        {
            XCursorInit1(leaf);
            if (lastDup) return DupLast(out data);
            return DupFirst(out data);
        }

        byte* dp;
        int dl;
        if ((Node.Flags(leaf) & Const.F_BIGDATA) == 0)
        {
            dp = Node.Data(leaf);
            dl = (int)Node.Dsz(leaf);
        }
        else
        {
            // Data lives on a contiguous overflow page; NODEDATA holds its pgno.
            ulong pgno = ReadU64(Node.Data(leaf));
            byte* omp = _txn.GetPage(pgno);
            dp = omp + Const.PAGEHDRSZ;
            dl = (int)Node.Dsz(leaf);
        }
        data = new ReadOnlySpan<byte>(dp, dl);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadU64(byte* p)
        => (ulong)p[0] | ((ulong)p[1] << 8) | ((ulong)p[2] << 16) | ((ulong)p[3] << 24)
         | ((ulong)p[4] << 32) | ((ulong)p[5] << 40) | ((ulong)p[6] << 48) | ((ulong)p[7] << 56);

    /// <summary>Pointer to the leaf node at the current cursor position. Valid only
    /// after a successful positioning op, and only for non-LEAF2 pages.</summary>
    internal byte* CurrentLeafNode => Page.NodePtr(_pg[_top], _ki[_top]);

    public void Dispose() { /* read cursor: pages live in the mmap, nothing to free */ }
}
