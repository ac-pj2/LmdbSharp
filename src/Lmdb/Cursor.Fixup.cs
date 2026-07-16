// Cursor shadowing: when one cursor of a write transaction mutates the tree,
// every OTHER cursor of the same transaction holding positions in affected
// pages must be adjusted — C LMDB's "Adjust other cursors pointing to mp"
// loops in mdb_page_touch / _mdb_cursor_put / mdb_cursor_del / mdb_page_split
// / mdb_node_move / mdb_page_merge / mdb_rebalance.
//
// The registry is LmdbTransaction.TrackedCursors (already maintained for page
// spill). Read transactions have no registry, so every helper is a no-op there
// and the read path pays nothing.
//
// Positions are matched by PAGE POINTER, which identifies a page at whatever
// stack level a cursor holds it and works for dup sub-trees without any DB
// identity checks. Sub-PAGE dup positions (xcursor pointers INTO a leaf node's
// data) are the exception and get explicit rebase/refresh handling.
namespace Lmdb;

public sealed unsafe partial class LmdbCursor
{
    /// <summary>Temp cursors (SplitParent's mn, Rebalance's sibling) manage
    /// their stacks explicitly mid-operation; fixups must never adjust them.</summary>
    internal bool NoFixup;

    private System.Collections.Generic.List<LmdbCursor>? Others => _txn.TrackedCursors;

    private bool FixupSkip(LmdbCursor c)
        => c == this || c.NoFixup || (c._flags & CursorFlags.Initialized) == 0 || c._snum == 0;

    /// <summary>Page `old` was COW'd to `np` (mdb_page_touch): repoint stack
    /// entries and rebase sub-page xcursor pointers into the old buffer.</summary>
    internal void FixupTouch(byte* old, byte* np)
    {
        var list = Others; if (list == null) return;
        long psize = PageSize;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (c.NoFixup) continue;
            if (c != this)
                for (int l = 0; l < c._snum; l++)
                    if (c._pg[l] == old) { c._pg[l] = np; break; }
            var xc = c._xc;
            if (xc != null && xc._isSub && xc._pg[0] != null
                && xc._pg[0] >= old && xc._pg[0] < old + psize)
                xc._pg[0] = np + (xc._pg[0] - old);
        }
    }

    /// <summary>An entry was inserted at (mp, indx): other cursors on mp with
    /// ki &gt;= indx slide right.</summary>
    internal void FixupInsert(byte* mp, int indx)
    {
        var list = Others; if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (FixupSkip(c)) continue;
            for (int l = 0; l < c._snum; l++)
                if (c._pg[l] == mp)
                {
                    if (c._ki[l] >= indx) c._ki[l]++;
                    break;
                }
        }
    }

    /// <summary>The entry at (mp, indx) was deleted: other cursors on mp above
    /// indx slide left; cursors ON the entry get the Deleted flag (C_DEL — Next
    /// then returns the entry that slid into the slot without advancing).
    /// Deleting a whole dup set additionally invalidates xcursors parked on it.
    /// NodeDel compaction moves node data, so sub-page xcursor pointers of every
    /// cursor on mp are refreshed from their (page, ki).</summary>
    internal void FixupDelete(byte* mp, int indx, bool invalidateXc = false)
    {
        var list = Others; if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (FixupSkip(c)) continue;
            for (int l = 0; l < c._snum; l++)
                if (c._pg[l] == mp)
                {
                    if (c._ki[l] > indx) c._ki[l]--;
                    else if (c._ki[l] == indx && l == c._top)
                    {
                        c._flags |= CursorFlags.Deleted;
                        if (invalidateXc && c._xc != null)
                            c._xc._flags &= ~CursorFlags.Initialized;
                    }
                    if (l == c._top) RefreshSubPageXc(c);
                    break;
                }
        }
    }

    /// <summary>Node data on mp was compacted/relocated without index changes
    /// (value replacement = delete + re-add at the same slot): re-base every
    /// sub-page xcursor parked anywhere on the page.</summary>
    internal void FixupPageNodesMoved(byte* mp)
    {
        var list = Others; if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (FixupSkip(c)) continue;
            if (c._pg[c._top] == mp) RefreshSubPageXc(c);
        }
    }

    /// <summary>The leaf node at (mp, indx) was replaced or resized in place
    /// (sub-page growth, value replacement): cursors whose xcursor points into
    /// the node's old data must be re-based from the current node.</summary>
    internal void FixupXcRefresh(byte* mp, int indx)
    {
        var list = Others; if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (FixupSkip(c)) continue;
            if (c._pg[c._top] == mp && c._ki[c._top] == indx)
                RefreshSubPageXc(c);
        }
    }

    /// <summary>A dup value was inserted at dupIdx inside the sub-page of the
    /// leaf node at (mp, indx): parked xcursors on that node re-base and their
    /// dup index slides right when at/after the insert.</summary>
    internal void FixupDupInsert(byte* mp, int indx, int dupIdx)
    {
        var list = Others; if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (FixupSkip(c)) continue;
            if (c._pg[c._top] != mp || c._ki[c._top] != indx) continue;
            RefreshSubPageXc(c);
            var xc = c._xc;
            if (xc != null && xc._isSub && (xc._flags & CursorFlags.Initialized) != 0
                && xc._ki[0] >= dupIdx)
                xc._ki[0]++;
        }
    }

    private byte[]? _xcReseek;        // captured dup value across a shape conversion
    private bool _xcReseekDeleted;    // the captured position carried C_DEL

    /// <summary>BEFORE a dup-storage shape conversion of the node at (mp, indx)
    /// (plain→sub-page or sub-page→sub-DB): capture each parked cursor's current
    /// dup VALUE while the old storage is still intact. The conversion preserves
    /// values, so the position can be re-established exactly afterwards —
    /// stronger than C's mdb_xcursor_init2, which teleports parked xcursors to
    /// the acting cursor's position.</summary>
    internal void FixupXcPreConvert(byte* mp, int indx)
    {
        var list = Others; if (list == null) return;
        byte* leaf = Page.NodePtr(mp, indx);
        bool plain = (Node.Flags(leaf) & Const.F_DUPDATA) == 0;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (FixupSkip(c)) continue;
            if (c._pg[c._top] != mp || c._ki[c._top] != indx) continue;
            c._xcReseek = null;
            c._xcReseekDeleted = false;
            if (plain)
            {
                // Single plain value: the only dup position is the value itself.
                c._xcReseek = new ReadOnlySpan<byte>(Node.Data(leaf), (int)Node.Dsz(leaf)).ToArray();
                continue;
            }
            var xc = c._xc;
            if (xc == null || !xc._isSub || (xc._flags & CursorFlags.Initialized) == 0)
                continue;
            byte* sp = xc._pg[0];
            int ki = xc._ki[0];
            if (sp == null || ki >= Page.NumKeys(sp)) continue;
            if (Page.IsLeaf2(sp))
            {
                int ks = (int)Page.Pad(sp);
                c._xcReseek = new ReadOnlySpan<byte>(Page.Leaf2Key(sp, ki, ks), ks).ToArray();
            }
            else
            {
                byte* n = Page.NodePtr(sp, ki);
                c._xcReseek = new ReadOnlySpan<byte>(Node.Key(n), Node.KSize(n)).ToArray();
            }
            c._xcReseekDeleted = (xc._flags & CursorFlags.Deleted) != 0;
        }
    }

    /// <summary>AFTER the shape conversion: re-establish parked xcursors on the
    /// new storage at their captured value. A position whose value is gone (or
    /// that carried C_DEL) re-seeks to the successor and keeps C_DEL so the next
    /// NextDup returns it without advancing.</summary>
    internal void FixupXcPostConvert(byte* mp, int indx)
    {
        var list = Others; if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (FixupSkip(c)) continue;
            if (c._pg[c._top] != mp || c._ki[c._top] != indx) continue;
            var val = c._xcReseek;
            c._xcReseek = null;
            if (c._xc == null && val == null) continue;
            if (val == null)
            {
                if (c._xc != null) c._xc._flags &= ~CursorFlags.Initialized;
                continue;
            }
            byte* leaf = Page.NodePtr(mp, indx);
            c.XCursorInit1(leaf);
            var xc = c._xc!;
            bool found;
            bool exact = false;
            fixed (byte* vp = val)
            {
                if (xc._isSub)
                {
                    // Sub-page: binary search (mirrors GetBoth's sub-page path).
                    byte* sp = xc._pg[0];
                    int nk = Page.NumKeys(sp);
                    var dcmp = c._db.DupCmp!;
                    int lo = 0, hi = nk - 1, pos = nk, rcCmp = -1;
                    while (lo <= hi)
                    {
                        int mid = (lo + hi) >> 1;
                        byte* k = Page.IsLeaf2(sp)
                            ? Page.Leaf2Key(sp, mid, (int)Page.Pad(sp))
                            : Node.Key(Page.NodePtr(sp, mid));
                        int kl = Page.IsLeaf2(sp) ? (int)Page.Pad(sp) : Node.KSize(Page.NodePtr(sp, mid));
                        rcCmp = dcmp(vp, val.Length, k, kl);
                        if (rcCmp == 0) { pos = mid; exact = true; break; }
                        if (rcCmp > 0) lo = mid + 1; else hi = mid - 1;
                    }
                    if (!exact) pos = lo;
                    found = pos < nk;
                    if (found) xc._ki[0] = pos;
                }
                else
                {
                    found = xc.TryGet(CursorOp.SetRange, val, out var fk, out _);
                    exact = found && fk.SequenceEqual(val);
                }
            }
            if (!found)
            {
                // Captured value (and everything after it) is past the end:
                // park at the last dup, exhausted (the next advance ends).
                if (xc._isSub)
                {
                    int nk2 = Page.NumKeys(xc._pg[0]);
                    if (nk2 > 0) xc._ki[0] = nk2 - 1;
                    else xc._flags &= ~CursorFlags.Initialized;
                }
                else if (!xc.Last(out _, out _))
                {
                    xc._flags &= ~CursorFlags.Initialized;
                }
                continue;
            }
            // Landed past a vanished value, or the position carried C_DEL:
            // the slot now holds the successor — flag it so the next advance
            // returns it in place.
            if (!exact || c._xcReseekDeleted)
                xc._flags |= CursorFlags.Deleted;
            c._xcReseekDeleted = false;
        }
    }

    /// <summary>Re-base a cursor's sub-page xcursor pointer from its current
    /// (page, ki) — the node may have moved within the page.</summary>
    private static void RefreshSubPageXc(LmdbCursor c)
    {
        var xc = c._xc;
        if (xc == null || !xc._isSub || (xc._flags & CursorFlags.Initialized) == 0)
            return;
        byte* mp = c._pg[c._top];
        if (Page.IsLeaf2(mp) || c._ki[c._top] >= Page.NumKeys(mp)) return;
        byte* leaf = Page.NodePtr(mp, c._ki[c._top]);
        if ((Node.Flags(leaf) & Const.F_DUPDATA) == 0
            || (Node.Flags(leaf) & Const.F_SUBDATA) != 0)
        {
            xc._flags &= ~CursorFlags.Initialized;   // shape changed under us
            return;
        }
        xc._pg[0] = Node.Data(leaf);
    }

    /// <summary>The tree grew a root (split of the old root `oldRoot` inserted
    /// `newRoot` beneath every stack): shift other cursors' stacks up.</summary>
    internal void FixupRootGrow(byte* oldRoot, byte* newRoot)
    {
        var list = Others; if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (FixupSkip(c)) continue;
            if (c._pg[0] != oldRoot || c._snum + 1 > MaxDepth) continue;
            for (int l = c._snum; l > 0; l--) { c._pg[l] = c._pg[l - 1]; c._ki[l] = c._ki[l - 1]; }
            c._pg[0] = newRoot;
            c._ki[0] = 0;
            c._snum++; c._top++;
        }
    }

    /// <summary>The root collapsed into its single child: shift other cursors'
    /// stacks down one level.</summary>
    internal void FixupRootCollapse(byte* oldRoot)
    {
        var list = Others; if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (FixupSkip(c)) continue;
            if (c._pg[0] != oldRoot) continue;
            if (c._snum == 1) { c._flags &= ~CursorFlags.Initialized; c._snum = 0; c._top = -1; continue; }
            for (int l = 0; l < c._snum - 1; l++) { c._pg[l] = c._pg[l + 1]; c._ki[l] = c._ki[l + 1]; }
            c._snum--; c._top--;
        }
    }

    /// <summary>The whole tree emptied (root dropped): uninitialize other
    /// cursors whose stacks start at the dropped root.</summary>
    internal void FixupTreeDropped(byte* oldRoot)
    {
        var list = Others; if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (FixupSkip(c)) continue;
            if (c._pg[0] != oldRoot) continue;
            c._flags &= ~CursorFlags.Initialized;
            c._snum = 0; c._top = -1;
        }
    }

    /// <summary>mdb_page_split's cursor adjustment: entries of `mp` (plus the
    /// new one at newindx) were redistributed at split point splitIndx; other
    /// cursors on mp remap their index and possibly migrate to rp, adopting
    /// rp's parent path. Runs AFTER step 7 (self repositioned), BEFORE mn is
    /// disposed. `rpPath` supplies rp's ancestor path: the self cursor when it
    /// ended on rp, else mn (parent split), else self's path with ki[ptop]+1
    /// (`bumpPtopKi`).</summary>
    internal void FixupSplit(byte* mp, byte* rp, int splitIndx, int newindx,
        LmdbCursor rpPath, int ptop, bool bumpPtopKi)
    {
        var list = Others; if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (FixupSkip(c)) continue;
            for (int l = 0; l < c._snum; l++)
            {
                if (c._pg[l] != mp) continue;
                int ki = c._ki[l];
                if (ki >= newindx) ki++;         // account for the inserted entry
                if (ki >= splitIndx)
                {
                    // Entry migrated to rp: follow it and adopt rp's ancestry.
                    c._pg[l] = rp;
                    c._ki[l] = ki - splitIndx;
                    for (int a = 0; a < l && a < rpPath._snum; a++)
                    {
                        c._pg[a] = rpPath._pg[a];
                        c._ki[a] = rpPath._ki[a];
                    }
                    if (bumpPtopKi && ptop >= 0 && ptop < l) c._ki[ptop]++;
                }
                else
                {
                    c._ki[l] = ki;
                }
                if (l == c._top) RefreshSubPageXc(c);   // rebuild moved node data
                break;
            }
        }
    }

    /// <summary>mdb_page_merge's cursor adjustment: every entry of psrc moved to
    /// pdst starting at slot dstOldCount; cursors on psrc follow, adopting
    /// pdst's ancestry from `dstPath`.</summary>
    internal void FixupMerge(byte* psrc, byte* pdst, int dstOldCount, LmdbCursor dstPath)
    {
        var list = Others; if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (FixupSkip(c) || c == dstPath) continue;
            for (int l = 0; l < c._snum; l++)
            {
                if (c._pg[l] != psrc) continue;
                c._pg[l] = pdst;
                c._ki[l] += dstOldCount;
                for (int a = 0; a < l && a < dstPath._snum; a++)
                {
                    c._pg[a] = dstPath._pg[a];
                    c._ki[a] = dstPath._ki[a];
                }
                if (l == c._top) RefreshSubPageXc(c);
                break;
            }
        }
    }

    /// <summary>mdb_node_move's cursor adjustment: the entry at (srcPage, srcKi)
    /// became (dstPage, dstKi). Cursors parked exactly on it follow; slide
    /// semantics for the source/destination pages are applied by the caller via
    /// FixupInsert/FixupDelete-style shifts baked in here.</summary>
    internal void FixupMove(byte* srcPage, int srcKi, byte* dstPage, int dstKi, LmdbCursor dstPath)
    {
        var list = Others; if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (FixupSkip(c) || c == dstPath) continue;
            for (int l = 0; l < c._snum; l++)
            {
                if (c._pg[l] == dstPage && c._ki[l] >= dstKi)
                {
                    c._ki[l]++;   // destination insert shift
                    if (l == c._top) RefreshSubPageXc(c);
                    break;
                }
                if (c._pg[l] == srcPage)
                {
                    if (c._ki[l] > srcKi) c._ki[l]--;   // source delete shift
                    else if (c._ki[l] == srcKi)
                    {
                        // Follow the moved entry.
                        c._pg[l] = dstPage;
                        c._ki[l] = dstKi;
                        for (int a = 0; a < l && a < dstPath._snum; a++)
                        {
                            c._pg[a] = dstPath._pg[a];
                            c._ki[a] = dstPath._ki[a];
                        }
                    }
                    if (l == c._top) RefreshSubPageXc(c);
                    break;
                }
            }
        }
    }
}

