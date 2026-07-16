// Page rebalance after delete: when a page becomes underfull (below FILL_THRESHOLD
// for leaves, or below 2 keys for branches), either borrow a node from a sibling
// or merge with one. If the root becomes empty, drop the tree; if it has one branch
// child, collapse it (replace root with child, decrement depth).
//
// Ports mdb_rebalance / mdb_node_move / mdb_page_merge / mdb_update_key from mdb.c.
//
// Simplifications vs. C:
//  - No LEAF2 / sub-page support (DUPSORT layer).
//  - No multi-cursor tracking (single cursor per DB — cursor fixup omitted).
//  - No loose-page optimization (merged pages go to the free list, not loose list).
//  - update_key's split path (rare: key grows and page is full) falls back to del+add.
using System.Runtime.CompilerServices;

namespace Lmdb;

public sealed unsafe partial class LmdbCursor
{
    private const int FillThreshold = Const.FILL_THRESHOLD; // 250 = 25.0%

    /// <summary>Page fill in tenths of a percent (PAGEFILL macro).</summary>
    private static int PageFill(byte* page, int psize)
        => 1000 * (psize - Const.PAGEHDRSZ - Page.SizeLeft(page)) / (psize - Const.PAGEHDRSZ);

    /// <summary>Rebalance the tree after a node was deleted from the cursor's top
    /// page (mdb_rebalance). Decides between no-op, root collapse, node move, or
    /// page merge. Recurses upward when a merge leaves the parent underfull.</summary>
    private int Rebalance()
    {
        byte* mp = _pg[_top];
        bool isBranch = Page.IsBranch(mp);
        int minkeys = isBranch ? 2 : 1;
        int thresh = isBranch ? 1 : FillThreshold;

        // Page is full enough and has enough keys → nothing to do.
        if (PageFill(mp, PageSize) >= thresh && Page.NumKeys(mp) >= minkeys)
            return 0;

        // --- Root page (no parent) ---
        if (_snum < 2)
        {
            if (Page.NumKeys(mp) == 0)
            {
                // Tree is completely empty: drop root, reset depth/pages.
                Db.SetRoot(_db.DbRec, Const.P_INVALID);
                Db.SetDepth(_db.DbRec, 0);
                Db.SetEntries(_db.DbRec, 0);
                if (isBranch) Db.AddBranchPages(_db.DbRec, -1);
                else Db.AddLeafPages(_db.DbRec, -1);
                _txn.FreePgs!.Append(Page.Pgno(mp));
                _snum = 0; _top = -1;
                _flags &= ~CursorFlags.Initialized;
                return 0;
            }
            if (isBranch && Page.NumKeys(mp) == 1)
            {
                // Collapse root: replace with the single child. Shift the stack
                // levels ABOVE _snum down too — PageMerge saved them and will
                // re-expose them via savedSnum after this returns (C bounds the
                // shift by the new md_depth for exactly this reason).
                _txn.FreePgs!.Append(Page.Pgno(mp));
                ulong childPgno = Node.Pgno(Page.NodePtr(mp, 0));
                Db.SetRoot(_db.DbRec, childPgno);
                Db.SetDepth(_db.DbRec, (ushort)(Db.Depth(_db.DbRec) - 1));
                Db.AddBranchPages(_db.DbRec, -1);
                _pg[0] = _txn.GetPage(childPgno);
                _ki[0] = _ki[1];
                int newDepth = Db.Depth(_db.DbRec);
                for (int i = 1; i < newDepth && i + 1 < MaxDepth; i++)
                {
                    _pg[i] = _pg[i + 1];
                    _ki[i] = _ki[i + 1];
                }
            }
            return 0;
        }

        // --- Non-root: find a sibling and move or merge ---
        int ptop = _top - 1;
        if (Page.NumKeys(_pg[ptop]) <= 1)
            return (int)LmdbErr.Corrupted;   // parent must have ≥2 pointers

        // Build a temp cursor for the sibling.
        using var mn = new LmdbCursor(_txn, _db);
        CopyStackTo(mn);
        mn._top = _top;
        mn._snum = _snum;

        int oldki = _ki[_top];
        bool fromleft;

        if (_ki[ptop] == 0)
        {
            // Leftmost child: read right neighbor.
            mn._ki[ptop]++;
            byte* node = Page.NodePtr(_pg[ptop], mn._ki[ptop]);
            mn._pg[_top] = _txn.GetPage(Node.Pgno(node));
            mn._ki[_top] = 0;
            _ki[_top] = Page.NumKeys(_pg[_top]);   // pretend we're past the end
            fromleft = false;
        }
        else
        {
            // Read left neighbor.
            mn._ki[ptop]--;
            byte* node = Page.NodePtr(_pg[ptop], mn._ki[ptop]);
            mn._pg[_top] = _txn.GetPage(Node.Pgno(node));
            mn._ki[_top] = Page.NumKeys(mn._pg[_top]) - 1;
            _ki[_top] = 0;
            fromleft = true;
        }

        // If the neighbor is full enough, move one node; otherwise merge.
        if (PageFill(mn._pg[mn._top], PageSize) >= thresh && Page.NumKeys(mn._pg[mn._top]) > minkeys)
        {
            int rc = mn.NodeMove(this, fromleft);
            if (fromleft) oldki++;
            if (rc != 0) return rc;
        }
        else
        {
            int rc;
            if (!fromleft)
            {
                // Merge right neighbor (mn) into us (this).
                rc = mn.PageMerge(this);
            }
            else
            {
                // Merge us (this) into left neighbor (mn), then copy mn back.
                oldki += Page.NumKeys(mn._pg[_top]);
                mn._ki[_top] += _ki[_top] + 1;
                rc = this.PageMerge(mn);
                CopyStackFrom(mn);
            }
            _flags &= ~CursorFlags.Eof;
            if (rc != 0) return rc;
        }
        _ki[_top] = oldki;
        return 0;
    }

    /// <summary>Move one node from this cursor (source) to <paramref name="cdst"/>
    /// (destination). <paramref name="fromleft"/> = true when source is the left
    /// sibling. Ports mdb_node_move.</summary>
    private int NodeMove(LmdbCursor cdst, bool fromleft)
    {
        byte* srcPage = _pg[_top];
        byte* dstPage = cdst._pg[cdst._top];
        bool srcIsBranch = Page.IsBranch(srcPage);

        // COW both pages.
        int rc = PageTouch();
        if (rc != 0) return rc;
        srcPage = _pg[_top];
        rc = cdst.PageTouch();
        if (rc != 0) return rc;
        dstPage = cdst._pg[cdst._top];

        // Read the source node (at src's ki[top]).
        byte* srcNode = Page.NodePtr(srcPage, _ki[_top]);
        ulong srcpg = Node.Pgno(srcNode);
        ushort flags = Node.Flags(srcNode);

        // Determine the key to use for the moved node.
        byte* keyPtr; int keyLen;
        if (_ki[_top] == 0 && srcIsBranch)
        {
            // Branch slot 0 has no key — descend to find the lowest key below.
            FindLowestKey(out keyPtr, out keyLen);
        }
        else
        {
            keyPtr = Node.Key(srcNode);
            keyLen = Node.KSize(srcNode);
        }

        // Read the data (for leaf nodes).
        byte* dataPtr = Node.Data(srcNode);
        int dataLen = (int)Node.Dsz(srcNode);

        bool dstIsBranch = Page.IsBranch(dstPage);
        bool dstSlot0 = cdst._ki[cdst._top] == 0 && dstIsBranch;

        // Inserting at slot 0 of a branch displaces the current keyless slot-0
        // node to slot 1, where its key MATTERS. Give it its real separator (the
        // lowest key under its subtree) BEFORE the move — otherwise searches
        // under it misroute and the parent separator below is published empty.
        // (mdb_node_move: mdb_page_search_lowest + mdb_update_key.)
        if (dstSlot0 && Page.NumKeys(dstPage) > 0)
        {
            byte* lowKey; int lowLen;
            cdst.FindLowestKey(out lowKey, out lowLen);
            int savedDstKi = cdst._ki[cdst._top];
            cdst._ki[cdst._top] = 0;
            rc = cdst.UpdateKey(lowKey, lowLen);
            cdst._ki[cdst._top] = savedDstKi;
            if (rc != 0) return rc;
        }

        // Add the node to the destination WITH its real key — branch slot 0's
        // key is ignored by searches, and keeping it lets the separator update
        // below read a real key from node 0. (C passes the key unconditionally.)
        rc = cdst.NodeAdd(cdst._ki[cdst._top], keyPtr, keyLen, dataPtr, dataLen, srcpg, flags);
        if (rc != 0) return rc;

        // Delete the node from the source.
        NodeDel(0);

        // --- Update parent separators ---
        //
        // If the source's first node changed (ki[top]==0 after deletion) and it's
        // not the leftmost child, update the parent separator for the source.
        if (_ki[_top] == 0 && _ki[_top - 1] != 0)
        {
            byte* np = _pg[_top];
            byte* newKey = Page.IsLeaf2(np) ? Page.Leaf2Key(np, 0, (int)Db.Pad(_db.DbRec))
                                            : Node.Key(Page.NodePtr(np, 0));
            int newLen = Page.IsLeaf2(np) ? (int)Db.Pad(_db.DbRec)
                                          : Node.KSize(Page.NodePtr(np, 0));
            rc = UpdateParentSeparator(this, newKey, newLen);
            if (rc != 0) return rc;
        }
        // Branch slot 0 must have an empty key.
        if (srcIsBranch && _ki[_top] == 0)
        {
            int savedKi = _ki[_top];
            _ki[_top] = 0;
            UpdateKey(null, 0);
            _ki[_top] = savedKi;
        }

        // If the destination's first node changed (ki[top]==0 after insert) and
        // it's not the leftmost child, update its separator.
        if (cdst._ki[cdst._top] == 0 && cdst._ki[cdst._top - 1] != 0)
        {
            byte* np = cdst._pg[cdst._top];
            byte* newKey = Node.Key(Page.NodePtr(np, 0));
            int newLen = Node.KSize(Page.NodePtr(np, 0));
            rc = UpdateParentSeparator(cdst, newKey, newLen);
            if (rc != 0) return rc;
        }
        if (dstIsBranch && cdst._ki[cdst._top] == 0)
        {
            cdst.UpdateKey(null, 0);
        }

        return 0;
    }

    /// <summary>Merge all nodes from this cursor (source) into <paramref name="cdst"/>
    /// (destination). Frees the source page, deletes the parent branch pointer, and
    /// recursively rebalances the parent. Ports mdb_page_merge.</summary>
    private int PageMerge(LmdbCursor cdst)
    {
        byte* psrc = _pg[_top];
        byte* pdst = cdst._pg[cdst._top];
        bool srcIsBranch = Page.IsBranch(psrc);

        // COW the destination.
        int rc = cdst.PageTouch();
        if (rc != 0) return rc;
        pdst = cdst._pg[cdst._top];

        // Move all nodes from src to dst.
        int j = Page.NumKeys(pdst);
        int srcNkeys = Page.NumKeys(psrc);
        for (int i = 0; i < srcNkeys; i++, j++)
        {
            byte* srcNode = Page.NodePtr(psrc, i);
            byte* keyPtr; int keyLen;
            if (i == 0 && srcIsBranch)
            {
                // Branch slot 0: find lowest key below.
                _ki[_top] = 0;
                FindLowestKey(out keyPtr, out keyLen);
            }
            else
            {
                keyPtr = Node.Key(srcNode);
                keyLen = Node.KSize(srcNode);
            }
            byte* dataPtr = Node.Data(srcNode);
            int dataLen = (int)Node.Dsz(srcNode);
            ulong pgno = Node.Pgno(srcNode);
            ushort flags = Node.Flags(srcNode);

            // First branch slot is keyless.
            if (Page.IsBranch(pdst) && j == 0) { keyPtr = null; keyLen = 0; }

            rc = cdst.NodeAdd(j, keyPtr, keyLen, dataPtr, dataLen, pgno, flags);
            if (rc != 0) return rc;
        }

        // Unlink src from parent: pop to parent, delete the branch node.
        _top--;
        _ki[_top] = _ki[_top];   // ki[ptop] still points at the src's branch node
        NodeDel(0);

        // If parent's first node changed, clear its key (branch slot 0 = empty).
        if (_ki[_top] == 0)
            UpdateKey(null, 0);
        _top++;

        // Free the source page.
        psrc = _pg[_top];
        _txn.FreePgs!.Append(Page.Pgno(psrc));
        if (srcIsBranch) Db.AddBranchPages(_db.DbRec, -1);
        else Db.AddLeafPages(_db.DbRec, -1);

        // Pop dst up to parent and recursively rebalance.
        int savedSnum = cdst._snum;
        ushort oldDepth = Db.Depth(_db.DbRec);
        cdst.PopStack();
        rc = cdst.Rebalance();
        // If tree height changed, adjust snum.
        if (oldDepth != Db.Depth(_db.DbRec))
            savedSnum += Db.Depth(_db.DbRec) - oldDepth;
        cdst._snum = savedSnum;
        cdst._top = savedSnum - 1;
        return rc;
    }

    /// <summary>Update the key of the branch node at the cursor's current position
    /// (mdb_update_key). If the EVEN-aligned key size changes, shifts page data.
    /// If there's not enough room, falls back to del + re-add (rare).</summary>
    private int UpdateKey(byte* key, int keyLen)
    {
        byte* mp = _pg[_top];
        int indx = _ki[_top];
        byte* node = Page.NodePtr(mp, indx);
        ushort ptr = Page.PtrAt(mp, indx);

        int ksize = Even(keyLen);
        int oksize = Even(Node.KSize(node));
        int delta = ksize - oksize;

        if (delta != 0)
        {
            if (delta > 0 && Page.SizeLeft(mp) < delta)
            {
                // Not enough room — fall back to delete + split (rare).
                ulong pgno = Node.Pgno(node);
                NodeDel(0);
                return PageSplit(key, keyLen, null, 0, pgno, 0);
            }
            // Shift ptrs for nodes at or below ptr.
            int numkeys = Page.NumKeys(mp);
            for (int i = 0; i < numkeys; i++)
            {
                if (Page.PtrAt(mp, i) <= ptr)
                    Page.PtrAt(mp, i) = (ushort)(Page.PtrAt(mp, i) - delta);
            }
            // Shift node data.
            byte* basePtr = mp + Page.Upper(mp) + Const.PAGEBASE;
            int len = ptr - Page.Upper(mp) + Const.NODESIZE;
            if (len > 0) Buffer.MemoryCopy(basePtr, basePtr - delta, len, len);
            Page.SetUpper(mp, (ushort)(Page.Upper(mp) - delta));
            node = Page.NodePtr(mp, indx);
        }

        Node.SetKSize(node, (ushort)keyLen);
        if (keyLen > 0 && key != null)
            Buffer.MemoryCopy(key, Node.Key(node), keyLen, keyLen);
        return 0;
    }

    /// <summary>Update the parent separator for the given cursor's current page.
    /// Descends to parent (top-1), positions at the branch node, and calls UpdateKey.</summary>
    private static int UpdateParentSeparator(LmdbCursor c, byte* key, int keyLen)
    {
        int savedTop = c._top;
        c._top = savedTop - 1;
        int rc = c.UpdateKey(key, keyLen);
        c._top = savedTop;
        return rc;
    }

    /// <summary>Find the lowest key below the current branch node (page_search_lowest).
    /// Descends the leftmost child pointers to a leaf and returns the first key.</summary>
    private void FindLowestKey(out byte* key, out int keyLen)
    {
        // Start from the current top (a branch page) and follow child[0] down.
        // Uses locals only — writing the descent into the cursor stack clobbered
        // levels above _top that PageMerge re-exposes afterwards (C uses a copy
        // cursor for mdb_page_search_lowest for the same reason).
        byte* mp = _pg[_top];
        int guard = 0;
        while (Page.IsBranch(mp) && guard++ < MaxDepth)
            mp = _txn.GetPage(Node.Pgno(Page.NodePtr(mp, 0)));
        if (Page.NumKeys(mp) > 0)
        {
            byte* firstNode = Page.NodePtr(mp, 0);
            key = Node.Key(firstNode);
            keyLen = Node.KSize(firstNode);
        }
        else
        {
            key = null; keyLen = 0;
        }
    }

    // --- cursor stack helpers ---

    private void CopyStackTo(LmdbCursor dst)
    {
        for (int i = 0; i < _snum; i++) { dst._pg[i] = _pg[i]; dst._ki[i] = _ki[i]; }
        dst._db = _db;
        dst._flags = _flags;
    }

    private void CopyStackFrom(LmdbCursor src)
    {
        _snum = src._snum; _top = src._top;
        for (int i = 0; i < _snum; i++) { _pg[i] = src._pg[i]; _ki[i] = src._ki[i]; }
        _flags = src._flags;
    }

    private void PopStack()
    {
        if (_snum > 0)
        {
            _snum--;
            if (_snum > 0) _top = _snum - 1;
            else { _top = -1; _flags &= ~CursorFlags.Initialized; }
        }
    }
}
