// Cursor write path: page allocation, copy-on-write touch, node add/del, page
// split, and the Put/Delete entry points. Ports mdb_page_alloc / mdb_page_touch /
// mdb_page_new / mdb_node_add / mdb_node_del / mdb_page_split / _mdb_cursor_put
// / _mdb_cursor_del from mdb.c.
//
// Stage scope: single-value trees (no DUPSORT/DUPFIXED sub-pages). Overflow pages
// (F_BIGDATA) and the full split algorithm ARE supported; the free-DB is recorded
// into but not yet persisted (pages are allocated fresh — Stage D adds reuse).
using System.Runtime.CompilerServices;

namespace Lmdb;

public sealed unsafe partial class Cursor
{
    private LmdbEnvironment Env => _txn.Env;
    private int PageSize => (int)Env.PageSize;

    // ---------------- allocation ----------------

    /// <summary>Allocate num fresh or reused pages (mdb_page_alloc). Tries the env's
    /// PgHead (reusable pool from the free-DB) first; falls back to mt_next_pgno.
    /// Returns a pointer to native memory (zeroed header) marked P_DIRTY.</summary>
    private byte* AllocPage(int num)
    {
        ulong pgno;
        var pgHead = Env.PgHead;

        if (pgHead != null && pgHead.Count > 0)
        {
            if (num == 1)
            {
                // Fast path: pop the last (smallest) page.
                pgHead.TryPop(out pgno);
            }
            else
            {
                // Search for a contiguous run of num pages.
                int idx = pgHead.FindContiguous(num);
                if (idx > 0)
                {
                    pgno = pgHead[idx];
                    pgHead.RemoveRange(idx, num);
                }
                else
                {
                    // No contiguous run; fall back to fresh pages.
                    pgno = AllocFresh(num);
                }
            }
        }
        else
        {
            pgno = AllocFresh(num);
        }

        byte* np = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)(num * PageSize));
        for (int i = 0; i < num * PageSize; i++) np[i] = 0;
        Page.SetPgno(np, pgno);
        Page.OrFlags(np, Const.P_DIRTY);

        _txn.Dirty!.Append(new Id2l.Entry { Id = pgno, Ptr = np });
        return np;
    }

    private ulong AllocFresh(int num)
    {
        ulong pgno = _txn.NextPgno;
        _txn.NextPgno += (ulong)num;
        if (pgno + (ulong)num > Env.MaxPg)
            throw new LmdbException(LmdbErr.MapFull, $"need {num} pages, map full");
        return pgno;
    }

    /// <summary>Allocate and initialize a new page (mdb_page_new).</summary>
    private byte* NewPage(ushort flags, int num)
    {
        byte* np = AllocPage(num);
        Page.SetFlags(np, (ushort)(flags | Const.P_DIRTY));
        Page.SetLower(np, (ushort)(Const.PAGEHDRSZ - Const.PAGEBASE));
        Page.SetUpper(np, (ushort)(PageSize - Const.PAGEBASE));

        if ((flags & Const.P_BRANCH) != 0) Db.AddBranchPages(_db.DbRec, num);
        else if ((flags & Const.P_LEAF) != 0) Db.AddLeafPages(_db.DbRec, num);
        else if ((flags & Const.P_OVERFLOW) != 0)
        {
            Db.AddOverflowPages(_db.DbRec, num);
            Page.SetOverflowPages(np, (uint)num);
        }
        return np;
    }

    /// <summary>Copy-on-write: ensure the top cursor page is dirty (mdb_page_touch).
    /// Allocates a new page, copies the old one, fixes the parent branch pointer (or
    /// md_root), records the old page as freed, and repoints the cursor at the copy.</summary>
    private int PageTouch()
    {
        byte* mp = _pg[_top];
        if ((Page.Flags(mp) & Const.P_DIRTY) != 0)
            return 0;   // already dirty in this txn

        byte* np = AllocPage(1);
        ulong newPgno = Page.Pgno(np);

        // Old page becomes free (recorded; persistence arrives with the free-DB layer).
        _txn.FreePgs!.Append(Page.Pgno(mp));

        // Fix the parent's branch pointer, or the DB root if this is the root page.
        if (_top > 0)
        {
            byte* parent = _pg[_top - 1];
            byte* node = Page.NodePtr(parent, _ki[_top - 1]);
            Node.SetPgno(node, newPgno);
        }
        else
        {
            Db.SetRoot(_db.DbRec, newPgno);
        }

        // Copy old -> new, publish new pgno + dirty flag.
        Buffer.MemoryCopy(mp, np, PageSize, PageSize);
        Page.SetPgno(np, newPgno);
        Page.SetFlags(np, (ushort)(Page.Flags(mp) | Const.P_DIRTY));
        _pg[_top] = np;
        return 0;
    }

    /// <summary>COW every page on the cursor stack, root to leaf (mdb_cursor_touch).</summary>
    private int TouchPath()
    {
        if (_snum == 0) return 0;
        int savedTop = _top;
        _top = 0;
        int rc;
        do { rc = PageTouch(); } while (rc == 0 && ++_top < _snum);
        _top = _snum - 1;
        return rc;
    }

    // ---------------- node primitives ----------------

    /// <summary>Leaf size used for the overflow decision + room check
    /// (mdb_leaf_size = EVEN(LEAFSIZE + sizeof(indx_t)), capped to overflow form).</summary>
    private int LeafSize(int ksize, int dsize)
    {
        int sz = Const.NODESIZE + ksize + dsize;
        if (sz > Env.NodeMax) sz -= dsize - 8;   // data goes to overflow; node keeps a pgno (8 bytes)
        return Even(sz + sizeof(ushort));        // + sizeof(indx_t) for the ptr slot
    }

    private static int Even(int n) => (n + 1) & ~1;

    /// <summary>Add a node at index <paramref name="indx"/> (mdb_node_add).
    /// Supports leaf/branch, F_BIGDATA overflow, and the keyless branch slot 0.</summary>
    private int NodeAdd(int indx, byte* key, int ksize, byte* data, int dsize, ulong pgno, ushort flags)
    {
        byte* mp = _pg[_top];
        if (Page.IsLeaf2(mp))
            throw new LmdbException(LmdbErr.Incompatible, "LEAF2 (DUPFIXED) not supported in this stage");

        int nodeSize = Const.NODESIZE;
        int room = Page.SizeLeft(mp) - sizeof(ushort);

        byte* ofp = null;
        if (ksize != 0) nodeSize += ksize;   // key may be null for branch slot 0
        if (Page.IsLeaf(mp))
        {
            if ((flags & Const.F_BIGDATA) != 0)
            {
                nodeSize += sizeof(ulong);   // data already on overflow; node holds its pgno
            }
            else if (nodeSize + dsize > Env.NodeMax)
            {
                // Move data to a fresh overflow page (mdb_node_add overflow path).
                int ovpages = OvPages(dsize, PageSize);
                nodeSize = Even(nodeSize + sizeof(ulong));
                if (nodeSize > room) return (int)LmdbErr.PageFull;
                ofp = NewPage(Const.P_OVERFLOW, ovpages);
                flags |= Const.F_BIGDATA;
            }
            else
            {
                nodeSize += dsize;
            }
        }
        nodeSize = Even(nodeSize);
        if (nodeSize > room) return (int)LmdbErr.PageFull;

        // Shift higher ptrs up one slot.
        int nkeys = Page.NumKeys(mp);
        for (int i = nkeys; i > indx; i--)
            Page.PtrAt(mp, i) = Page.PtrAt(mp, i - 1);

        // Place the node at the top of the free space, growing downward.
        ushort ofs = (ushort)(Page.Upper(mp) - nodeSize);
        Page.PtrAt(mp, indx) = ofs;
        Page.SetUpper(mp, ofs);
        Page.SetLower(mp, (ushort)(Page.Lower(mp) + sizeof(ushort)));

        byte* node = Page.NodePtr(mp, indx);
        Node.SetKSize(node, (ushort)(key == null ? 0 : ksize));
        Node.SetFlags(node, flags);
        if (Page.IsLeaf(mp))
            Node.SetDsz(node, (uint)dsize);
        else
            Node.SetPgno(node, pgno);

        if (key != null && ksize > 0)
            Buffer.MemoryCopy(key, Node.Key(node), ksize, ksize);

        if (Page.IsLeaf(mp))
        {
            byte* ndata = Node.Data(node);
            if (ofp == null)
            {
                if ((flags & Const.F_BIGDATA) != 0)
                    *(ulong*)ndata = *(ulong*)data;   // caller supplied the overflow pgno
                else if (data != null && dsize > 0)
                    Buffer.MemoryCopy(data, ndata, dsize, dsize);
            }
            else
            {
                // Store the overflow page's pgno in the node, copy data into the overflow page.
                *(ulong*)ndata = Page.Pgno(ofp);
                Buffer.MemoryCopy(data, Page.Data(ofp), dsize, dsize);
            }
        }
        return 0;
    }

    /// <summary>Delete the node at the cursor's top index (mdb_node_del).</summary>
    private void NodeDel(int ksize)
    {
        byte* mp = _pg[_top];
        int indx = _ki[_top];
        int numkeys = Page.NumKeys(mp);
        if (indx >= numkeys) return;

        byte* node = Page.NodePtr(mp, indx);
        int sz = Const.NODESIZE + Node.KSize(node);
        if (Page.IsLeaf(mp))
        {
            if ((Node.Flags(node) & Const.F_BIGDATA) != 0) sz += sizeof(ulong);
            else sz += (int)Node.Dsz(node);
        }
        sz = Even(sz);

        ushort ptr = Page.PtrAt(mp, indx);
        // Compact the ptr array, shifting nodes below `ptr` up by sz.
        int j = 0;
        for (int i = 0; i < numkeys; i++)
        {
            if (i == indx) continue;
            ushort p = Page.PtrAt(mp, i);
            Page.PtrAt(mp, j) = (ushort)(p < ptr ? p + sz : p);
            j++;
        }
        // Shift the freed data slot closed (move [upper..ptr) down by sz).
        byte* basePtr = mp + Page.Upper(mp) + Const.PAGEBASE;
        int moveLen = ptr - Page.Upper(mp);
        if (moveLen > 0) Buffer.MemoryCopy(basePtr, basePtr + sz, moveLen, moveLen);

        Page.SetLower(mp, (ushort)(Page.Lower(mp) - sizeof(ushort)));
        Page.SetUpper(mp, (ushort)(Page.Upper(mp) + sz));
    }

    private static int OvPages(int size, int psize)
        => ((Const.PAGEHDRSZ - 1 + size) / psize) + 1;

    // ---------------- Put / Delete entry points ----------------

    /// <summary>Insert or update a key/value pair (_mdb_cursor_put, simplified for
    /// single-value trees: no DUPSORT/MULTIPLE/RESERVE/APPEND fast path).</summary>
    public void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, PutFlags flags)
    {
        if (key.IsEmpty) throw new LmdbException(LmdbErr.BadValsize, "key is empty");
        if ((uint)key.Length - 1 >= Env.NodeMax) throw new LmdbException(LmdbErr.BadValsize, "key too large");

        bool noOverwrite = (flags & PutFlags.NoOverwrite) != 0;
        bool isUpdate = (flags & PutFlags.Current) != 0;

        fixed (byte* kp = key, dp = data)
        {
            int rc;
            bool insertKey;

            if (_db.Root == Const.P_INVALID)
            {
                // Empty tree: allocate a root leaf.
                byte* np = NewPage(Const.P_LEAF, 1);
                Push(np);
                Db.SetRoot(_db.DbRec, Page.Pgno(np));
                Db.SetDepth(_db.DbRec, (ushort)(Db.Depth(_db.DbRec) + 1));
                insertKey = true;
                _flags |= CursorFlags.Initialized;
            }
            else if (isUpdate)
            {
                if ((_flags & CursorFlags.Initialized) == 0)
                    throw new LmdbException(LmdbErr.Invalid, "MDB_CURRENT requires a positioned cursor");
                int t = TouchPath();
                if (t != 0) throw new LmdbException(LmdbErr.Problem, "touch failed");
                insertKey = false;
            }
            else
            {
                // Position (MDB_SET). Exact match => update; otherwise insert at the slot.
                rc = SetPosition(kp, key.Length, out bool exact);
                if (rc != 0 && rc != (int)LmdbErr.NotFound)
                    throw new LmdbException((LmdbErr)rc);

                if (exact)
                {
                    if (noOverwrite)
                        throw new LmdbException(LmdbErr.KeyExist);
                    int t = TouchPath();
                    if (t != 0) throw new LmdbException(LmdbErr.Problem, "touch failed");
                    insertKey = false;
                }
                else
                {
                    int t = TouchPath();
                    if (t != 0) throw new LmdbException(LmdbErr.Problem, "touch failed");
                    insertKey = true;
                }
            }

            if (!insertKey)
            {
                // Update in place: if the new data size differs, del + re-add.
                byte* leaf = Page.NodePtr(_pg[_top], _ki[_top]);
                uint oldDsz = Node.Dsz(leaf);
                bool wasBig = (Node.Flags(leaf) & Const.F_BIGDATA) != 0;
                // Simplest correct path: delete + re-insert at the same index.
                NodeDel(0);
            }

            // Compute node size and either add in place or split.
            byte* top = _pg[_top];
            int nsize = LeafSize(key.Length, data.Length);
            if (Page.SizeLeft(top) < nsize)
            {
                rc = PageSplit(kp, key.Length, dp, data.Length, 0, 0);
                if (rc != 0) throw new LmdbException((LmdbErr)rc);
            }
            else
            {
                rc = NodeAdd(_ki[_top], kp, key.Length, dp, data.Length, 0, 0);
                if (rc == (int)LmdbErr.PageFull)
                {
                    rc = PageSplit(kp, key.Length, dp, data.Length, 0, 0);
                }
                if (rc != 0) throw new LmdbException((LmdbErr)rc);
            }

            Db.SetEntries(_db.DbRec, Db.Entries(_db.DbRec) + (insertKey ? 1UL : 0UL));
        }
    }

    /// <summary>Delete the node at <paramref name="key"/> (_mdb_cursor_del, no DUPSORT).</summary>
    public bool Delete(ReadOnlySpan<byte> key)
    {
        if (_db.Root == Const.P_INVALID) return false;
        fixed (byte* kp = key)
        {
            int rc = SetPosition(kp, key.Length, out bool exact);
            if (rc != 0 || !exact) return false;
            int t = TouchPath();
            if (t != 0) throw new LmdbException(LmdbErr.Problem, "touch failed");

            byte* leaf = Page.NodePtr(_pg[_top], _ki[_top]);
            // Free overflow pages (recorded; persistence with the free-DB layer).
            if ((Node.Flags(leaf) & Const.F_BIGDATA) != 0)
            {
                ulong pg = ReadU64(Node.Data(leaf));
                _txn.FreePgs!.Append(pg);
                // and any additional overflow pages beyond the first
                byte* omp = _txn.GetPage(pg);
                uint npages = Page.OverflowPages(omp);
                for (uint i = 1; i < npages; i++) _txn.FreePgs!.Append(pg + i);
            }
            NodeDel(0);
            Db.SetEntries(_db.DbRec, Db.Entries(_db.DbRec) - 1);
            // Rebalance the tree (may merge with a sibling or collapse the root).
            int rc2 = Rebalance();
            if (rc2 != 0) throw new LmdbException((LmdbErr)rc2, "rebalance failed");
            return true;
        }
    }

    /// <summary>Position the cursor via MDB_SET semantics, returning whether the key
    /// matched exactly. Thin wrapper over the read-path Set that reports exactness.</summary>
    private int SetPosition(byte* kp, int klen, out bool exact)
    {
        _pg[0] = null;
        int rc = PageSearch(kp, klen, 0);
        if (rc != 0) { exact = false; return rc; }
        NodeSearch(kp, klen, out exact);
        return 0;
    }
}
