// mdb_page_split port. When a leaf/branch page is full, split it into two siblings
// and insert the separator key into the parent (allocating a new root branch if the
// split page was the root). The new node is inserted as part of the move.
//
// We use a "virtual node" view of the page (existing nodes + the new node at
// newindx) instead of LMDB's temp copy-page ptr linearization — equivalent, but
// avoids the indirect copy->mp_ptrs[] trick. A temp page is still used to rebuild
// the left sibling's contents, then memcpy'd back into the original page (which is
// the dirty COW copy).
using System.Runtime.CompilerServices;

namespace Lmdb;

public sealed unsafe partial class LmdbCursor
{
    private int PageSplit(byte* newkey, int newksize, byte* newdata, int newdsize, ulong newpgno, ushort nflags)
    {
        byte* mp = _pg[_top];
        int newindx = _ki[_top];
        int nkeys = Page.NumKeys(mp);
        bool isLeaf = Page.IsLeaf(mp);
        int psize = PageSize;
        int pmax = psize - Const.PAGEHDRSZ;
        int keythresh = psize >> 7;

        // 1) Allocate the right sibling (same flags as mp, minus P_DIRTY/P_LEAF2 extras kept).
        byte* rp = NewPage((ushort)(Page.Flags(mp) & (Const.P_BRANCH | Const.P_LEAF | Const.P_LEAF2)), 1);
        Page.SetPad(rp, Page.Pad(mp));

        int ptop;
        if (_top < 1)
        {
            // Splitting the root: allocate a new branch page as the root.
            byte* pp = NewPage(Const.P_BRANCH, 1);
            // Shift the stack up to make room for the new root at index 0.
            for (int i = _snum; i > 0; i--) { _pg[i] = _pg[i - 1]; _ki[i] = _ki[i - 1]; }
            _pg[0] = pp; _ki[0] = 0;
            Db.SetRoot(_db.DbRec, Page.Pgno(pp));
            Db.SetDepth(_db.DbRec, (ushort)(Db.Depth(_db.DbRec) + 1));
            // Add the left (keyless) branch node 0 pointing to the old root (mp, now at _pg[1]).
            _top = 0;
            int rc0 = NodeAdd(0, null, 0, null, 0, Page.Pgno(mp), 0);
            if (rc0 != 0) return rc0;
            _snum++; _top++;
            ptop = 0;
        }
        else
        {
            ptop = _top - 1;
        }

        // 2) Compute the split index. Default is the midpoint; adjust by accumulated
        //    size so neither half overflows (matters for large/variable-size nodes).
        int splitIndx = (nkeys + 1) / 2;
        int nsize = LeafOrBranchSize(isLeaf, newksize, newdsize);

        if (isLeaf && newindx == nkeys)
        {
            // Pure append (rightmost insert on a full LEAF page): send the new
            // node alone to the right sibling and leave this page byte-for-byte
            // untouched — no rebuild needed. Diverges from mdb_page_split's
            // copy-rebuild but produces an equally valid tree, with ~100% fill
            // for sequential loads. LEAF ONLY: a 1-node leaf is legal, but a
            // 1-child branch violates the min-fanout invariant (walker:
            // branch-underflow) and breaks SplitParent's stack fixups.
            splitIndx = nkeys;
        }
        else if (nkeys < keythresh || nsize > pmax / 16 || newindx >= nkeys)
        {
            int pacc = 0;
            int i, j, k;
            if (newindx <= splitIndx || newindx >= nkeys) { i = 0; j = 1; k = (newindx >= nkeys) ? nkeys : splitIndx + 1 + (isLeaf ? 1 : 0); }
            else { i = nkeys; j = -1; k = splitIndx - 1; }
            for (; i != k; i += j)
            {
                if (i == newindx) { pacc += nsize; }
                else
                {
                    int orig = i < newindx ? i : i - 1;
                    byte* node = Page.NodePtr(mp, orig);
                    int s = Const.NODESIZE + Node.KSize(node) + sizeof(ushort);
                    if (isLeaf) { s += (Node.Flags(node) & Const.F_BIGDATA) != 0 ? sizeof(ulong) : (int)Node.Dsz(node); }
                    pacc += Even(s);
                }
                if (pacc > pmax || i == k - j) { splitIndx = i + (j < 0 ? 1 : 0); break; }
            }
        }

        // 3) Separator key = the first key of the right page (virtual node splitIndx),
        //    or the new key if the new node lands exactly at the split point.
        SepKey sepkey = default;
        if (splitIndx == newindx) { sepkey = new SepKey(newkey, newksize); }
        else
        {
            int orig = splitIndx < newindx ? splitIndx : splitIndx - 1;
            byte* node = Page.NodePtr(mp, orig);
            sepkey = new SepKey(Node.Key(node), Node.KSize(node));
        }

        // 4) Insert the separator into the parent branch, pointing to rp.
        int rc;
        LmdbCursor? mn = null;
        int branchSize = Even(Const.NODESIZE + sepkey.Size + sizeof(ushort));
        if (Page.SizeLeft(_pg[ptop]) < branchSize)
        {
            // Parent is full: recursively split it, then fix OUR stack the way
            // mdb_page_split does after `rc = mdb_page_split(&mn, ...)`.
            rc = SplitParent(ref ptop, in sepkey, Page.Pgno(rp), out mn);
            if (rc != 0) return rc;
        }
        else
        {
            // node_add a branch node into the parent at index _ki[ptop]+1.
            int savedTop = _top;
            _top = ptop;
            int idx = _ki[ptop] + 1;
            rc = NodeAdd(idx, sepkey.Ptr, sepkey.Size, null, 0, Page.Pgno(rp), 0);
            _top = savedTop;
            if (rc != 0) return rc;
        }

        // 5) Move nodes: fill rp with virtual nodes [splitIndx..nkeys], then rebuild the
        //    left page (mp) with [0..splitIndx-1] via a temp copy page.
        // NOTE: the size-adjust loop can produce splitIndx == nkeys with newindx < nkeys
        // (the right page then receives the ORIGINAL last node, not the new one), so the
        // append shortcut must check both.
        if (isLeaf && newindx == nkeys && splitIndx == nkeys)
        {
            // Pure append split: the new node is rp's only node; mp is untouched.
            _pg[_top] = rp;
            rc = NodeAdd(0, newkey, newksize, newdata, newdsize, 0, nflags);
            if (rc != 0) return rc;
            _ki[_top] = 0;
        }
        else
        {
            byte* copy = AllocTempPage();
            Page.SetPgno(copy, Page.Pgno(mp));
            Page.SetFlags(copy, Page.Flags(mp));
            Page.SetLower(copy, (ushort)(Const.PAGEHDRSZ - Const.PAGEBASE));
            Page.SetUpper(copy, (ushort)(psize - Const.PAGEBASE));

            _pg[_top] = rp;
            int ii = splitIndx, jj = 0;
            do
            {
                GetVirtualNode(ii, newindx, nkeys, mp, newkey, newksize, newdata, newdsize, newpgno, nflags,
                    out byte* rk, out int rks, out byte* rd, out int rds, out ulong rpg, out ushort rflags);

                if (!isLeaf && jj == 0) rks = 0;   // first branch slot is keyless

                int curKi = jj;
                if (isLeaf)
                    rc = NodeAdd(curKi, rk, rks, rd, rds, 0, rflags);
                else
                    rc = NodeAdd(curKi, rk, rks, null, 0, rpg, rflags);
                if (rc != 0) { FreeTempPage(copy); return rc; }

                if (ii == newindx) _ki[_top] = curKi;   // cursor tracks the new node

                if (ii == nkeys)
                {
                    // Wrapped past the last virtual node: switch to rebuilding the left page.
                    ii = 0; jj = 0;
                    _pg[_top] = copy;
                }
                else { ii++; jj++; }
            } while (ii != splitIndx);

            // 6) Copy the rebuilt left page (copy) back into mp (the dirty original).
            int copyNkeys = Page.NumKeys(copy);
            for (int i = 0; i < copyNkeys; i++)
                Page.PtrAt(mp, i) = Page.PtrAt(copy, i);
            Page.SetLower(mp, Page.Lower(copy));
            Page.SetUpper(mp, Page.Upper(copy));
            byte* copyData = copy + Page.Upper(copy) + Const.PAGEBASE;
            byte* mpData = mp + Page.Upper(mp) + Const.PAGEBASE;
            int dataLen = psize - Page.Upper(mp) - Const.PAGEBASE;
            Buffer.MemoryCopy(copyData, mpData, dataLen, dataLen);
            FreeTempPage(copy);
        }

        // 7) Reposition the cursor onto the page holding the new node.
        if (newindx < splitIndx)
        {
            _pg[_top] = mp;
            mn?.Dispose();
        }
        else
        {
            _pg[_top] = rp;
            _ki[ptop]++;
            // If the parent itself split, our bumped index may run past the end
            // of the (left) parent half: rp's branch node then lives in the
            // right parent half, whose path mn tracked. (mdb_page_split: "Make
            // sure mc_ki is still valid.")
            if (mn != null && mn._pg[mn._top] != _pg[ptop]
                && _ki[ptop] >= Page.NumKeys(_pg[ptop]))
            {
                for (int i = 0; i <= ptop; i++)
                {
                    _pg[i] = mn._pg[i];
                    _ki[i] = mn._ki[i];
                }
            }
            mn?.Dispose();
        }
        return 0;
    }

    /// <summary>Recursive parent split (parent branch was full). Ports the
    /// `mdb_page_split(&mn, &sepkey, NULL, rp->pgno, 0)` recursive call plus the
    /// stack fixups C performs via cursor tracking: root growth inserts a level
    /// UNDER our stack, and if our own branch node migrated to the parent's new
    /// right half we must follow it (it sits immediately left of the separator
    /// mn just inserted). The caller's parent index must keep pointing at the
    /// node that references the page being split — copying mn's position
    /// verbatim was off by one and dropped a level on root growth.</summary>
    private int SplitParent(ref int ptop, in SepKey sepkey, ulong childPgno, out LmdbCursor mn)
    {
        // Build a temp cursor whose top is the parent (ptop), then split it with the
        // separator key as the "new node" (a branch node pointing to childPgno).
        mn = new LmdbCursor(_txn, _db);
        // The ctor re-resolves DbRec by DBI — for an xcursor (sub-DB record under
        // the parent's DBI) that redirected mn to the MAIN record, so a dup
        // sub-tree's root split wrote its new root/depth into the main database's
        // record and stranded the sub-DB record on the stale root (silent loss of
        // all previously inserted dups). The temp cursor must mutate exactly the
        // record THIS cursor operates on.
        mn._db = _db;
        mn._snum = ptop + 1;
        mn._top = ptop;
        for (int i = 0; i <= ptop; i++) { mn._pg[i] = _pg[i]; mn._ki[i] = _ki[i]; }
        mn._ki[ptop] += 1;   // insert position in the parent = right of current
        mn._flags = CursorFlags.Initialized;

        // The "new node" for the parent split is a branch node (sepkey -> childPgno).
        int rc = mn.PageSplit(sepkey.Ptr, sepkey.Size, null, 0, childPgno, 0);
        if (rc != 0) return rc;

        // Root split during the recursion: a new level appears BELOW our whole
        // stack. Shift our levels up and adopt mn's new root. (C: cursor
        // tracking adjusts mc, then `if (mc->mc_snum > snum) ptop++`.)
        if (mn._snum > ptop + 1)
        {
            for (int i = _snum; i > 0; i--) { _pg[i] = _pg[i - 1]; _ki[i] = _ki[i - 1]; }
            _pg[0] = mn._pg[0];
            // Our parent (the rebuilt LEFT half) is child 0 of the new root;
            // mn._ki[0] tracks mn's separator, which may live in the right
            // half. The right-half adoption below overwrites this when our
            // node actually migrated. (Copying mn's index blindly left the
            // cached cursor pointing at the wrong subtree — the append fast
            // path then wrote right-subtree keys into the left leaf.)
            _ki[0] = 0;
            _snum++; _top++;
            ptop++;
        }

        // If the parent page split and OUR branch node moved past the end of the
        // rebuilt left half, it now lives in mn's right half — immediately left
        // of the separator node mn inserted.
        if (mn._pg[mn._top] != _pg[ptop] && _ki[ptop] >= Page.NumKeys(_pg[ptop]))
        {
            for (int i = 0; i < ptop; i++) { _pg[i] = mn._pg[i]; _ki[i] = mn._ki[i]; }
            _pg[ptop] = mn._pg[mn._top];
            if (mn._ki[mn._top] > 0)
            {
                _ki[ptop] = mn._ki[mn._top] - 1;
            }
            else
            {
                // The separator landed at slot 0 of the right half: our node is
                // the LAST node of the left sibling. (C: mdb_cursor_sibling.)
                rc = MoveToLeftSibling(ptop);
                if (rc != 0) return rc;
            }
        }
        return 0;
    }

    /// <summary>Repoint stack level <paramref name="level"/> at its left sibling
    /// page, adjusting ancestors as needed (mdb_cursor_sibling, move_left).</summary>
    private int MoveToLeftSibling(int level)
    {
        if (level <= 0) return (int)LmdbErr.Corrupted;
        if (_ki[level - 1] == 0)
        {
            int rc = MoveToLeftSibling(level - 1);
            if (rc != 0) return rc;
        }
        else
        {
            _ki[level - 1]--;
        }
        byte* node = Page.NodePtr(_pg[level - 1], _ki[level - 1]);
        _pg[level] = _txn.GetPage(Node.Pgno(node));
        _ki[level] = Page.NumKeys(_pg[level]) - 1;
        return 0;
    }

    private readonly struct SepKey
    {
        public readonly byte* Ptr;
        public readonly int Size;
        public SepKey(byte* p, int s) { Ptr = p; Size = s; }
    }

    /// <summary>Return the i-th virtual node (existing nodes + new node at newindx).</summary>
    private static void GetVirtualNode(int i, int newindx, int nkeys, byte* mp,
        byte* newkey, int newksize, byte* newdata, int newdsize, ulong newpgno, ushort nflags,
        out byte* rk, out int rks, out byte* rd, out int rds, out ulong rpg, out ushort rflags)
    {
        if (i == newindx)
        {
            rk = newkey; rks = newksize; rd = newdata; rds = newdsize; rpg = newpgno; rflags = nflags;
            return;
        }
        int orig = i < newindx ? i : i - 1;
        byte* node = Page.NodePtr(mp, orig);
        rk = Node.Key(node); rks = Node.KSize(node);
        rd = Node.Data(node); rds = (int)Node.Dsz(node);
        rpg = Node.Pgno(node); rflags = Node.Flags(node);
    }

    private int LeafOrBranchSize(bool isLeaf, int ksize, int d_size)
    {
        int sz = Const.NODESIZE + ksize;
        if (isLeaf) sz += d_size > Env.NodeMax ? sizeof(ulong) : d_size;
        return Even(sz + sizeof(ushort));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte* AllocTempPage()
        => (byte*)Mem.Alloc((nuint)PageSize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FreeTempPage(byte* p)
        => Mem.Free(p);
}
