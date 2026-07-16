// Bulk DUPFIXED retrieval: GET_MULTIPLE / NEXT_MULTIPLE (+ FIRST/LAST_MULTIPLE
// convenience ops). Instead of one cursor call per duplicate value, each call
// returns ONE PACKED SPAN covering a whole dup page's worth of fixed-size
// values — for 8-byte values on a 4 KB page that's ~500 values per call.
//
// Ports the MDB_GET_MULTIPLE / MDB_NEXT_MULTIPLE cases of mdb_cursor_get, with
// one storage difference: C stores large DUPFIXED dup sets in packed LEAF2
// sub-DB pages and returns zero-copy spans everywhere. This port currently
// stores sub-DB dups as regular leaf nodes (see ConvertSubPageToSubDB), so for
// that layout the values are packed into a cursor-owned scratch buffer — still
// one call + one memcpy per page instead of a call per value. Inline sub-pages
// (small dup sets) are LEAF2 and returned zero-copy.
//
// Span lifetime: zero-copy spans follow the usual rule (valid while the txn is
// live); scratch-buffer spans are valid ONLY until the next operation on this
// cursor.
namespace Lmdb;

public sealed unsafe partial class LmdbCursor
{
    private byte[]? _multiBuf;   // scratch for packing regular-leaf dup pages

    private void RequireDupFixed()
    {
        if (!HasDupSort || (_db.DbFlags & (ushort)Const.MDB_DUPFIXED) == 0)
            throw new LmdbException(LmdbErr.Incompatible,
                "GetMultiple/NextMultiple require a DUPSORT|DUPFIXED database");
    }

    /// <summary>GET_MULTIPLE: position at <paramref name="key"/> (or use the
    /// current position when the key is empty) and return the first page of
    /// packed duplicate values. Leaves the dup cursor on that page's last item
    /// so <see cref="CursorOp.NextMultiple"/> continues with the next page.</summary>
    private bool GetMultiple(ReadOnlySpan<byte> key,
        out ReadOnlySpan<byte> keyOut, out ReadOnlySpan<byte> data)
    {
        RequireDupFixed();
        keyOut = default; data = default;
        if (!key.IsEmpty)
        {
            fixed (byte* kp = key)
            {
                if (Set(CursorOp.Set, kp, key.Length, out _, out _) != 0)
                    return false;
            }
        }
        else if ((_flags & CursorFlags.Initialized) == 0)
        {
            throw new LmdbException(LmdbErr.Invalid,
                "GetMultiple requires a key or an already-positioned cursor");
        }
        return FetchMultiple(out keyOut, out data);
    }

    /// <summary>NEXT_MULTIPLE: return the next page of packed duplicates for the
    /// current key. False when the key's duplicates are exhausted.</summary>
    private bool NextMultiple(out ReadOnlySpan<byte> keyOut, out ReadOnlySpan<byte> data)
    {
        RequireDupFixed();
        keyOut = default; data = default;
        if ((_flags & CursorFlags.Initialized) == 0 || _snum == 0) return false;
        byte* leaf = Page.NodePtr(_pg[_top], _ki[_top]);
        if ((Node.Flags(leaf) & Const.F_DUPDATA) == 0)
            return false;   // single plain value: GetMultiple already returned it
        if (_xc == null || _xc._snum == 0) return false;
        if (!DupNext(out _)) return false;   // steps onto the next dup page's first item
        return FetchMultiple(out keyOut, out data);
    }

    /// <summary>FIRST_MULTIPLE: rewind to the first page of the current key's
    /// duplicates and return it (restart a bulk iteration).</summary>
    private bool FirstMultiple(out ReadOnlySpan<byte> keyOut, out ReadOnlySpan<byte> data)
    {
        RequireDupFixed();
        keyOut = default; data = default;
        if ((_flags & CursorFlags.Initialized) == 0 || _snum == 0)
            throw new LmdbException(LmdbErr.Invalid, "FirstMultiple requires a positioned cursor");
        byte* leaf = Page.NodePtr(_pg[_top], _ki[_top]);
        if ((Node.Flags(leaf) & Const.F_DUPDATA) != 0)
        {
            XCursorInit1(leaf);
            if (!DupFirst(out _)) return false;
        }
        return FetchMultiple(out keyOut, out data);
    }

    /// <summary>LAST_MULTIPLE: return the last page of the current key's
    /// duplicates (the final chunk a forward bulk iteration would produce).</summary>
    private bool LastMultiple(out ReadOnlySpan<byte> keyOut, out ReadOnlySpan<byte> data)
    {
        RequireDupFixed();
        keyOut = default; data = default;
        if ((_flags & CursorFlags.Initialized) == 0 || _snum == 0)
            throw new LmdbException(LmdbErr.Invalid, "LastMultiple requires a positioned cursor");
        byte* leaf = Page.NodePtr(_pg[_top], _ki[_top]);
        if ((Node.Flags(leaf) & Const.F_DUPDATA) != 0)
        {
            XCursorInit1(leaf);
            if (!DupLast(out _)) return false;
        }
        return FetchMultiple(out keyOut, out data);
    }

    /// <summary>Return the dup page the xcursor currently sits on as one packed
    /// span (mdb_cursor_get's `fetchm`), leaving the xcursor on the page's last
    /// item. Handles plain single-value nodes, LEAF2 sub-pages (zero-copy), and
    /// regular-leaf sub-DB pages (packed into the scratch buffer).</summary>
    private bool FetchMultiple(out ReadOnlySpan<byte> keyOut, out ReadOnlySpan<byte> data)
    {
        keyOut = default; data = default;
        byte* mp = _pg[_top];
        byte* leaf = Page.NodePtr(mp, _ki[_top]);
        keyOut = new ReadOnlySpan<byte>(Node.Key(leaf), Node.KSize(leaf));

        if ((Node.Flags(leaf) & Const.F_DUPDATA) == 0)
        {
            // Single plain value: a one-item "page".
            if ((Node.Flags(leaf) & Const.F_BIGDATA) != 0)
            {
                byte* omp = _txn.GetPage(ReadU64(Node.Data(leaf)));
                data = new ReadOnlySpan<byte>(omp + Const.PAGEHDRSZ, (int)Node.Dsz(leaf));
            }
            else
            {
                data = new ReadOnlySpan<byte>(Node.Data(leaf), (int)Node.Dsz(leaf));
            }
            return true;
        }

        var xc = _xc;
        if (xc == null || xc._snum == 0) return false;
        byte* sp = xc._pg[xc._top];
        int n = Page.NumKeys(sp);
        if (n == 0) return false;

        if (Page.IsLeaf2(sp))
        {
            int ks = (int)Db.Pad(xc._db.DbRec);
            data = new ReadOnlySpan<byte>(Page.Leaf2Key(sp, 0, ks), n * ks);
        }
        else
        {
            // Regular-leaf dup page: pack the values (stored as sub-tree keys).
            int stride = Node.KSize(Page.NodePtr(sp, 0));
            int total = checked(n * stride);
            if (_multiBuf == null || _multiBuf.Length < total)
                _multiBuf = new byte[Math.Max(total, 4096)];
            fixed (byte* dst = _multiBuf)
            {
                for (int i = 0; i < n; i++)
                {
                    byte* nd = Page.NodePtr(sp, i);
                    if (Node.KSize(nd) != stride)
                        throw new LmdbException(LmdbErr.BadValsize,
                            $"DUPFIXED duplicates have differing sizes ({Node.KSize(nd)} vs {stride})");
                    Buffer.MemoryCopy(Node.Key(nd), dst + (long)i * stride, stride, stride);
                }
            }
            data = new ReadOnlySpan<byte>(_multiBuf, 0, total);
        }
        xc._ki[xc._top] = n - 1;   // NextMultiple advances off this page's end
        return true;
    }
}
