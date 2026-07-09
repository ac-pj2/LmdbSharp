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

public sealed unsafe partial class Cursor
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
        bool newRoot = false;
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
            newRoot = true;
        }
        else
        {
            ptop = _top - 1;
        }

        // 2) Compute the split index. Default is the midpoint; adjust by accumulated
        //    size so neither half overflows (matters for large/variable-size nodes).
        int splitIndx = (nkeys + 1) / 2;
        int nsize = LeafOrBranchSize(isLeaf, newksize, newdsize);

        if (nkeys < keythresh || nsize > pmax / 16 || newindx >= nkeys)
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
        int branchSize = Even(Const.NODESIZE + sepkey.Size + sizeof(ushort));
        if (Page.SizeLeft(_pg[ptop]) < branchSize)
        {
            // Parent is full: recursively split it. (Deeper trees only; needs a temp cursor.)
            rc = SplitParent(ptop, in sepkey, Page.Pgno(rp));
            if (rc != 0) return rc;
            // After a recursive split the stack may have grown; ptop may have shifted.
            if (newRoot) ptop = _top - 1;
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

        // 7) Reposition the cursor onto the page holding the new node.
        if (newindx < splitIndx)
        {
            _pg[_top] = mp;
        }
        else
        {
            _pg[_top] = rp;
            _ki[ptop]++;
        }
        return 0;
    }

    /// <summary>Recursive parent split (parent branch was full). Ports the
    /// `mdb_page_split(&mn, &sepkey, NULL, rp->pgno, 0)` recursive call. Uses a
    /// throwaway cursor copied from this one, restricted to levels [0..ptop].</summary>
    private int SplitParent(int ptop, in SepKey sepkey, ulong childPgno)
    {
        // Build a temp cursor whose top is the parent (ptop), then split it with the
        // separator key as the "new node" (a branch node pointing to childPgno).
        var mn = new Cursor(_txn, _db);
        mn._snum = ptop + 1;
        mn._top = ptop;
        for (int i = 0; i <= ptop; i++) { mn._pg[i] = _pg[i]; mn._ki[i] = _ki[i]; }
        mn._ki[ptop] += 1;   // insert position in the parent = right of current
        mn._flags = CursorFlags.Initialized;

        // The "new node" for the parent split is a branch node (sepkey -> childPgno).
        int rc = mn.PageSplit(sepkey.Ptr, sepkey.Size, null, 0, childPgno, 0);
        if (rc != 0) return rc;

        // Propagate any stack growth (root split) back to this cursor.
        for (int i = 0; i <= mn._top && i <= ptop + 1; i++) { _pg[i] = mn._pg[i]; _ki[i] = mn._ki[i]; }
        if (mn._snum > _snum) { _snum = mn._snum; _top = mn._top; }
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
        => (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)PageSize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FreeTempPage(byte* p)
        => System.Runtime.InteropServices.NativeMemory.Free(p);
}
