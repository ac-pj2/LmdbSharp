// DUPSORT write path: inserting duplicate values.
//
// When a DB has MDB_DUPSORT and a key already exists:
// - If the key has a single value (no F_DUPDATA): convert to a sub-page (P_SUBP)
//   containing the old + new values.
// - If the key has a sub-page (F_DUPDATA, not F_SUBDATA): add to the sub-page.
//   If the sub-page is full, grow it. If it exceeds me_nodemax, convert to a sub-DB.
// - If the key has a sub-DB (F_DUPDATA | F_SUBDATA): delegate to the xcursor (recursive put).
//
// Ports the DUPSORT branches of _mdb_cursor_put (the `more:` label and surrounding
// logic) from mdb.c.
using System.Runtime.InteropServices;

namespace Lmdb;

public sealed unsafe partial class Cursor
{
    /// <summary>DUPSORT-aware put. Called from Put() when the DB has MDB_DUPSORT.
    /// Handles sub-page creation, growth, sub-DB conversion, and delegation.</summary>
    private void PutDupSort(byte* keyPtr, int keyLen, byte* dataPtr, int dataLen, PutFlags flags)
    {
        bool noDupData = (flags & PutFlags.NoOverwrite) != 0;  // MDB_NODUPDATA
        bool isUpdate = (flags & PutFlags.Current) != 0;

        bool insertKey;
        bool insertData = false;   // true when adding a new dup to an existing key

        // --- Position the cursor ---
        if (_db.Root == Const.P_INVALID)
        {
            // Empty tree: allocate a root leaf and add the first value (no F_DUPDATA yet).
            byte* np = NewPage(Const.P_LEAF, 1);
            Push(np);
            Db.SetRoot(_db.DbRec, Page.Pgno(np));
            Db.SetDepth(_db.DbRec, (ushort)(Db.Depth(_db.DbRec) + 1));
            insertKey = true;
            _flags |= CursorFlags.Initialized;
            goto addNode;
        }

        int rc = SetPosition(keyPtr, keyLen, out bool exact);
        if (rc != 0 && rc != (int)LmdbErr.NotFound)
            throw new LmdbException((LmdbErr)rc);

        if (!exact)
        {
            // New key: just add a normal leaf node (no F_DUPDATA).
            insertKey = true;
            int t = TouchPath();
            if (t != 0) throw new LmdbException(LmdbErr.Problem, "touch failed");
            goto addNode;
        }

        // Key exists. COW the path.
        insertKey = false;
        int t2 = TouchPath();
        if (t2 != 0) throw new LmdbException(LmdbErr.Problem, "touch failed");

        byte* leaf = Page.NodePtr(_pg[_top], _ki[_top]);
        ushort leafFlags = Node.Flags(leaf);

        if ((leafFlags & Const.F_DUPDATA) == 0)
        {
            // --- Single value: convert to sub-page with old + new ---
            if (isUpdate)
            {
                // MDB_CURRENT: just overwrite the data in place.
                NodeDel(0);
                goto addNode;
            }

            // Check if the new data is a duplicate of the old.
            byte* oldData = Node.Data(leaf);
            int oldLen = (int)Node.Dsz(leaf);
            int cmp = _db.DupCmp!(dataPtr, dataLen, oldData, oldLen);
            if (cmp == 0)
            {
                if (noDupData) throw new LmdbException(LmdbErr.KeyExist);
                goto done;   // identical data, no-op
            }

            // Build a sub-page with the old value + new value.
            insertData = true;
            BuildSubPageFromSingle(leaf, oldData, oldLen, dataPtr, dataLen, cmp < 0);
            goto done;
        }

        if ((leafFlags & Const.F_SUBDATA) != 0)
        {
            // --- Sub-DB: delegate to xcursor ---
            XCursorInit1(leaf);
            insertData = true;
            ulong beforeEntries = Db.Entries(_mxDbRec);
            _xc!.Put(new ReadOnlySpan<byte>(dataPtr, dataLen), ReadOnlySpan<byte>.Empty, flags);
            // Write back the (potentially modified) MDB_db record to the node data.
            // The xcursor's Put may have changed md_root (page split) or md_entries.
            // (mdb.c: memcpy(db, &mc->mc_xcursor->mx_db, sizeof(MDB_db)))
            leaf = Page.NodePtr(_pg[_top], _ki[_top]);   // re-read (page may have changed)
            System.Buffer.MemoryCopy(_mxDbRec, Node.Data(leaf), Db.Size48, Db.Size48);
            goto done;
        }

        // --- Sub-page: add to it (or grow / convert to sub-DB) ---
        insertData = true;
        AddToSubPage(leaf, dataPtr, dataLen, noDupData);
        goto done;

    addNode:
        // Add a normal leaf node (for new keys or MDB_CURRENT overwrite).
        {
            byte* top = _pg[_top];
            int nsize = LeafSize(keyLen, dataLen);
            if (Page.SizeLeft(top) < nsize)
            {
                rc = PageSplit(keyPtr, keyLen, dataPtr, dataLen, 0, 0);
                if (rc != 0) throw new LmdbException((LmdbErr)rc);
            }
            else
            {
                rc = NodeAdd(_ki[_top], keyPtr, keyLen, dataPtr, dataLen, 0, 0);
                if (rc == (int)LmdbErr.PageFull)
                    rc = PageSplit(keyPtr, keyLen, dataPtr, dataLen, 0, 0);
                if (rc != 0) throw new LmdbException((LmdbErr)rc);
            }
            if (insertKey) Db.SetEntries(_db.DbRec, Db.Entries(_db.DbRec) + 1);
        }

    done:
        if (insertKey) Db.SetEntries(_db.DbRec, Db.Entries(_db.DbRec) + 1);
        else if (insertData) Db.SetEntries(_db.DbRec, Db.Entries(_db.DbRec) + 1);
    }

    /// <summary>Convert a single-value node to a sub-page with two values.
    /// Deletes the old node and adds a new one with F_DUPDATA.</summary>
    private void BuildSubPageFromSingle(byte* leaf, byte* oldData, int oldLen,
        byte* newData, int newLen, bool newIsFirst)
    {
        bool isDupFixed = (_db.DbFlags & (ushort)Const.MDB_DUPFIXED) != 0;

        int initSize;
        if (isDupFixed)
            initSize = Const.PAGEHDRSZ + newLen * 2 + newLen * 2;  // 2 values + room for 2 more
        else
            initSize = Const.PAGEHDRSZ + oldLen + newLen
                + 2 * (Const.NODESIZE + sizeof(ushort)) + (oldLen & 1) + (newLen & 1);

        byte* sp = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)initSize);
        try
        {
            Page.SetPgno(sp, Page.Pgno(_pg[_top]));
            ushort spFlags = (ushort)(Const.P_LEAF | Const.P_DIRTY | Const.P_SUBP);
            if (isDupFixed) { spFlags |= Const.P_LEAF2; Page.SetPad(sp, (ushort)newLen); }
            Page.SetFlags(sp, spFlags);
            Page.SetLower(sp, (ushort)(Const.PAGEHDRSZ - Const.PAGEBASE));
            Page.SetUpper(sp, (ushort)initSize);   // "page size" of the sub-page

            // Add the two values in sorted order.
            byte* firstData = newIsFirst ? newData : oldData;
            int firstLen = newIsFirst ? newLen : oldLen;
            byte* secondData = newIsFirst ? oldData : newData;
            int secondLen = newIsFirst ? oldLen : newLen;

            int rc;
            if (isDupFixed)
            {
                rc = NodeAddLeaf2(sp, 0, firstData, newLen);
                if (rc != 0) throw new LmdbException((LmdbErr)rc);
                rc = NodeAddLeaf2(sp, 1, secondData, newLen);
                if (rc != 0) throw new LmdbException((LmdbErr)rc);
            }
            else
            {
                rc = NodeAddSub(sp, 0, firstData, firstLen);
                if (rc != 0) throw new LmdbException((LmdbErr)rc);
                rc = NodeAddSub(sp, 1, secondData, secondLen);
                if (rc != 0) throw new LmdbException((LmdbErr)rc);
            }

            // Delete the old node and add a new one with F_DUPDATA + the sub-page.
            // Save the key before NodeDel (it shifts data and invalidates the pointer).
            // NODEDSZ = initSize (the sub-page's allocated size, NOT the current upper).
            int keyLen = Node.KSize(leaf);
            byte* keyBuf = stackalloc byte[keyLen];
            System.Buffer.MemoryCopy(Node.Key(leaf), keyBuf, keyLen, keyLen);

            NodeDel(0);
            rc = NodeAdd(_ki[_top], keyBuf, keyLen, sp, initSize, 0, Const.F_DUPDATA);
            if (rc == (int)LmdbErr.PageFull)
                rc = PageSplit(keyBuf, keyLen, sp, initSize, 0, Const.F_DUPDATA);
            if (rc != 0) throw new LmdbException((LmdbErr)rc);
        }
        finally { System.Runtime.InteropServices.NativeMemory.Free(sp); }
    }

    /// <summary>Add a node to a sub-page. The "key" is the dup value; data is empty.</summary>
    private static int NodeAddSub(byte* page, int indx, byte* data, int dlen)
    {
        int nodeSize = Const.NODESIZE + dlen;
        nodeSize = Even(nodeSize);
        int room = Page.SizeLeft(page) - sizeof(ushort);
        if (nodeSize > room) return (int)LmdbErr.PageFull;

        int nkeys = Page.NumKeys(page);
        for (int i = nkeys; i > indx; i--)
            Page.PtrAt(page, i) = Page.PtrAt(page, i - 1);

        ushort ofs = (ushort)(Page.Upper(page) - nodeSize);
        Page.PtrAt(page, indx) = ofs;
        Page.SetUpper(page, ofs);
        Page.SetLower(page, (ushort)(Page.Lower(page) + sizeof(ushort)));

        byte* node = Page.NodePtr(page, indx);
        Node.SetKSize(node, (ushort)dlen);
        Node.SetFlags(node, 0);
        Node.SetDsz(node, 0);   // no data for dup nodes
        Buffer.MemoryCopy(data, Node.Key(node), dlen, dlen);
        return 0;
    }

    /// <summary>Add a value to a LEAF2 sub-page (packed fixed-size values, no node headers).
    /// Ports the LEAF2 branch of mdb_node_add.</summary>
    private static int NodeAddLeaf2(byte* page, int indx, byte* data, int ksize)
    {
        int nkeys = Page.NumKeys(page);
        int room = Page.SizeLeft(page) - (ksize - sizeof(ushort));
        if (ksize - (int)sizeof(ushort) > room) return (int)LmdbErr.PageFull;

        byte* ptr = Page.Leaf2Key(page, indx, ksize);
        int dif = nkeys - indx;
        if (dif > 0)
            Buffer.MemoryCopy(ptr, ptr + ksize, dif * ksize, dif * ksize);
        Buffer.MemoryCopy(data, ptr, ksize, ksize);

        Page.SetLower(page, (ushort)(Page.Lower(page) + sizeof(ushort)));
        Page.SetUpper(page, (ushort)(Page.Upper(page) - (ksize - sizeof(ushort))));
        return 0;
    }

    /// <summary>Add a value to an existing sub-page. If the sub-page is full, grow it
    /// (by rebuilding with a larger data area). If it exceeds me_nodemax, convert to
    /// a sub-DB. Handles both regular and LEAF2 (DUPFIXED) sub-pages.</summary>
    private void AddToSubPage(byte* leaf, byte* dataPtr, int dataLen, bool noDupData)
    {
        byte* sp = Node.Data(leaf);   // sub-page pointer (in the COW'd parent page)
        int spSize = (int)Node.Dsz(leaf);   // current sub-page data size
        bool isLeaf2 = Page.IsLeaf2(sp);
        int ksize = isLeaf2 ? (int)Page.Pad(sp) : 0;

        // Find the insertion point (binary search within the sub-page).
        var dcmp = _db.DupCmp!;
        int nkeys = Page.NumKeys(sp);
        int lo = 0, hi = nkeys - 1, foundRc = 0, insertAt = nkeys;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            byte* nk = isLeaf2 ? Page.Leaf2Key(sp, mid, ksize) : Node.Key(Page.NodePtr(sp, mid));
            int nlen = isLeaf2 ? ksize : Node.KSize(Page.NodePtr(sp, mid));
            foundRc = dcmp(dataPtr, dataLen, nk, nlen);
            if (foundRc == 0)
            {
                if (noDupData) throw new LmdbException(LmdbErr.KeyExist);
                return;   // duplicate value, no-op
            }
            if (foundRc > 0) lo = mid + 1; else hi = mid - 1;
            insertAt = foundRc > 0 ? mid + 1 : mid;
        }

        // Try adding directly (if the sub-page has room).
        int rc = isLeaf2 ? NodeAddLeaf2(sp, insertAt, dataPtr, dataLen) : NodeAddSub(sp, insertAt, dataPtr, dataLen);
        if (rc == 0)
        {
            UpdateNodeDataSize(leaf, sp);
            return;
        }

        // Sub-page is full. Check if growing would exceed me_nodemax (convert to sub-DB).
        int nodeSize = isLeaf2 ? dataLen : Even(Const.NODESIZE + dataLen) + sizeof(ushort);
        int oldFree = Page.Upper(sp) - Page.Lower(sp);
        int growth = nodeSize > oldFree ? (nodeSize - oldFree) : 0;
        growth += isLeaf2 ? 4 * dataLen : 2 * (Const.NODESIZE + sizeof(ushort));
        int newSubPageSize = spSize + growth;
        int newNodeTotal = Const.NODESIZE + Node.KSize(leaf) + newSubPageSize + sizeof(ushort);
        if (newNodeTotal > Env.NodeMax)
        {
            ConvertSubPageToSubDB(leaf, dataPtr, dataLen, insertAt);
            return;
        }

        // Grow the sub-page: rebuild with a larger data area.
        GrowSubPage(leaf, dataPtr, dataLen, insertAt);
    }

    /// <summary>Update NODEDSZ to reflect the current sub-page size (lower offset).</summary>
    private static void UpdateNodeDataSize(byte* leaf, byte* sp)
    {
        // The sub-page's used size = lower (offset of last ptr slot + 1).
        // Actually, the data size should be the full sub-page size including
        // the data area. We use the upper offset as the boundary.
        // In LMDB, NODEDSZ for a sub-page node = the sub-page's total used size.
        // The sub-page grows downward from upper, so the data size = upper.
        // Wait — actually NODEDSZ is set to the full size of the sub-page data
        // (from the start of the sub-page to the end of its used area).
        // For a P_SUBP page, the data is from offset 0 to mp_upper.
        // But the node data starts at NODEDATA, which is the sub-page start.
        // So NODEDSZ = the sub-page size = everything from the sub-page header
        // to the last node. This is tracked by mp_upper (the free space boundary).
        // Actually, looking at the C code, NODEDSZ for a sub-page is set to
        // the size of the data stored in the node, which includes the full
        // sub-page (header + nodes). The sub-page's mp_upper tracks the boundary.
        //
        // For a sub-page that was modified in place (NodeAddSub succeeded),
        // the sub-page's mp_upper decreased (data grew downward), but the
        // node's dsz didn't change because the sub-page fits within the
        // existing data area. So we don't need to update dsz here.
        //
        // However, we DO need to make sure the sub-page is properly contained
        // within the node's data area. Since NodeAddSub only uses existing
        // free space (it checks room), the data size doesn't change.
        // So this is a no-op for the in-place case.
    }

    /// <summary>Grow a sub-page by increasing its data size. Shifts the data area up
    /// by the growth amount, adjusts ptrs, then adds the new node. Replaces the node
    /// with the expanded sub-page.</summary>
    private void GrowSubPage(byte* leaf, byte* dataPtr, int dataLen, int insertAt)
    {
        byte* oldSp = Node.Data(leaf);
        int oldSize = (int)Node.Dsz(leaf);
        bool isLeaf2 = Page.IsLeaf2(oldSp);
        int oldUpper = Page.Upper(oldSp);
        int oldLower = Page.Lower(oldSp);
        int oldNkeys = Page.NumKeys(oldSp);

        // Calculate growth.
        int nodeSize = isLeaf2 ? dataLen : Even(Const.NODESIZE + dataLen) + sizeof(ushort);
        int oldFree = oldUpper - oldLower;
        int offset = nodeSize > oldFree ? (nodeSize - oldFree) : 0;
        offset += isLeaf2 ? 4 * dataLen : 2 * (Const.NODESIZE + sizeof(ushort));

        int newSize = oldSize + offset;
        byte* newSp = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)newSize);
        try
        {
            Page.SetPgno(newSp, Page.Pgno(oldSp));
            Page.SetFlags(newSp, Page.Flags(oldSp));
            Page.SetPad(newSp, Page.Pad(oldSp));
            Page.SetLower(newSp, (ushort)oldLower);
            Page.SetUpper(newSp, (ushort)(oldUpper + offset));

            if (isLeaf2)
            {
                // LEAF2: values packed contiguously from PAGEHDRSZ. Copy them.
                int dataBytes = oldNkeys * dataLen;
                if (dataBytes > 0)
                    System.Buffer.MemoryCopy(Page.Data(oldSp), Page.Data(newSp), dataBytes, dataBytes);
                int rc = NodeAddLeaf2(newSp, insertAt, dataPtr, dataLen);
                if (rc != 0) throw new LmdbException(LmdbErr.PageFull, "LEAF2 sub-page grow failed");
            }
            else
            {
                // Regular sub-page: copy data area (shifted up by offset).
                int dataBytes = oldSize - oldUpper;
                if (dataBytes > 0)
                    System.Buffer.MemoryCopy(oldSp + oldUpper, newSp + oldUpper + offset, dataBytes, dataBytes);
                // Copy ptr array, adjusting each by +offset.
                for (int i = 0; i < oldNkeys; i++)
                    Page.PtrAt(newSp, i) = (ushort)(Page.PtrAt(oldSp, i) + offset);
                int rc = NodeAddSub(newSp, insertAt, dataPtr, dataLen);
                if (rc != 0) throw new LmdbException(LmdbErr.PageFull, "sub-page grow failed");
            }

            // Replace the node.
            int keyLen = Node.KSize(leaf);
            byte* keyBuf = stackalloc byte[keyLen];
            System.Buffer.MemoryCopy(Node.Key(leaf), keyBuf, keyLen, keyLen);

            NodeDel(0);
            int rc2 = NodeAdd(_ki[_top], keyBuf, keyLen, newSp, newSize, 0, Const.F_DUPDATA);
            if (rc2 == (int)LmdbErr.PageFull)
                rc2 = PageSplit(keyBuf, keyLen, newSp, newSize, 0, Const.F_DUPDATA);
            if (rc2 != 0) throw new LmdbException((LmdbErr)rc2);
        }
        finally { System.Runtime.InteropServices.NativeMemory.Free(newSp); }
    }

    /// <summary>Convert a sub-page to a sub-DB (F_SUBDATA). Allocates a new leaf page,
    /// moves the dupdata values to it, creates an MDB_db record, and replaces the node.</summary>
    private void ConvertSubPageToSubDB(byte* leaf, byte* newData, int newDataLen, int insertAt)
    {
        byte* oldSp = Node.Data(leaf);
        int oldNkeys = Page.NumKeys(oldSp);
        bool isLeaf2 = Page.IsLeaf2(oldSp);
        int ksize = isLeaf2 ? (int)Page.Pad(oldSp) : 0;

        // Collect all existing values from the sub-page.
        int totalVals = oldNkeys + 1;
        // Save values to stackalloc buffers (they're all the same size for DUPFIXED).
        byte* valueBuf = stackalloc byte[totalVals * newDataLen];
        int bufIdx = 0;
        for (int i = 0; i < oldNkeys; i++)
        {
            byte* k = isLeaf2 ? Page.Leaf2Key(oldSp, i, ksize) : Node.Key(Page.NodePtr(oldSp, i));
            int ks = isLeaf2 ? ksize : Node.KSize(Page.NodePtr(oldSp, i));
            if (i == insertAt)
            {
                Buffer.MemoryCopy(newData, valueBuf + bufIdx * newDataLen, newDataLen, newDataLen);
                bufIdx++;
            }
            Buffer.MemoryCopy(k, valueBuf + bufIdx * newDataLen, ks, ks);
            bufIdx++;
        }
        if (insertAt >= oldNkeys)
        {
            Buffer.MemoryCopy(newData, valueBuf + bufIdx * newDataLen, newDataLen, newDataLen);
            bufIdx++;
        }

        // Create an empty sub-DB (root = P_INVALID).
        byte* dbRec = stackalloc byte[Db.Size48];
        for (int i = 0; i < Db.Size48; i++) dbRec[i] = 0;
        *(ulong*)(dbRec + 40) = Const.P_INVALID;  // md_root = P_INVALID (empty)
        *(ushort*)(dbRec + 6) = 0;                 // md_depth = 0

        // Replace the node: save key, delete old, add new with F_DUPDATA|F_SUBDATA.
        int keyLen = Node.KSize(leaf);
        byte* keyBuf = stackalloc byte[keyLen];
        System.Buffer.MemoryCopy(Node.Key(leaf), keyBuf, keyLen, keyLen);
        NodeDel(0);
        int rc = NodeAdd(_ki[_top], keyBuf, keyLen, dbRec, Db.Size48, 0,
                         (ushort)(Const.F_DUPDATA | Const.F_SUBDATA));
        if (rc == (int)LmdbErr.PageFull)
            rc = PageSplit(keyBuf, keyLen, dbRec, Db.Size48, 0,
                           (ushort)(Const.F_DUPDATA | Const.F_SUBDATA));
        if (rc != 0) throw new LmdbException((LmdbErr)rc);

        // Re-init xcursor with the new sub-DB, then Put all values via the xcursor
        // (which handles tree creation, node insertion, and page splitting).
        leaf = Page.NodePtr(_pg[_top], _ki[_top]);
        XCursorInit1(leaf);
        var xc = _xc!;
        for (int i = 0; i < bufIdx; i++)
        {
            xc.Put(new ReadOnlySpan<byte>(valueBuf + i * newDataLen, newDataLen),
                   ReadOnlySpan<byte>.Empty, PutFlags.None);
        }

        // Write the updated MDB_db record back to the node data (root, entries, etc.).
        leaf = Page.NodePtr(_pg[_top], _ki[_top]);
        System.Buffer.MemoryCopy(_mxDbRec, Node.Data(leaf), Db.Size48, Db.Size48);
    }
}
