// Bulk write APIs: MDB_MULTIPLE (PutMultiple — store many fixed-size duplicate
// values for one key in a single call) and MDB_RESERVE (PutReserve — allocate
// value space in the page and return a writable span for the caller to fill,
// skipping one buffer copy).
namespace Lmdb;

public sealed unsafe partial class LmdbCursor
{
    /// <summary>Store <paramref name="values"/> (packed fixed-size items of
    /// <paramref name="itemSize"/> bytes) as duplicates of <paramref name="key"/>
    /// in one call (MDB_MULTIPLE). Requires a DUPSORT|DUPFIXED database. Values
    /// identical to existing duplicates are no-ops (unless
    /// <see cref="PutFlags.NoOverwrite"/> requests MDB_KEYEXIST). Returns the
    /// number of NEW duplicates stored. Ascending input is fastest: the dup
    /// cursor stays warm across items and hits the append path.</summary>
    public int PutMultiple(ReadOnlySpan<byte> key, ReadOnlySpan<byte> values, int itemSize, PutFlags flags = 0)
    {
        if (!HasDupSort || (_db.DbFlags & (ushort)Const.MDB_DUPFIXED) == 0)
            throw new LmdbException(LmdbErr.Incompatible,
                "PutMultiple requires a DUPSORT|DUPFIXED database");
        if (itemSize <= 0 || values.Length % itemSize != 0)
            throw new LmdbException(LmdbErr.BadValsize,
                $"values length {values.Length} is not a multiple of item size {itemSize}");
        int count = values.Length / itemSize;
        if (count == 0) return 0;

        ulong beforeMain = Db.Entries(_db.DbRec);

        // First value goes through the normal put: it creates the key, or
        // grows/converts the dup storage as needed.
        Put(key, values[..itemSize], flags);
        int done = 1;

        fixed (byte* kp = key)
        {
            while (done < count)
            {
                int rc = SetPosition(kp, key.Length, out bool exact);
                if (rc != 0 || !exact)
                    throw new LmdbException(LmdbErr.Problem, "bulk put lost its key");
                byte* leaf = Page.NodePtr(_pg[_top], _ki[_top]);
                if ((Node.Flags(leaf) & (Const.F_DUPDATA | Const.F_SUBDATA))
                    != (Const.F_DUPDATA | Const.F_SUBDATA))
                {
                    // Plain value or inline sub-page: per-value puts handle
                    // growth/conversion; once it becomes a sub-DB the fast loop
                    // below takes over.
                    Put(key, values.Slice(done * itemSize, itemSize), flags);
                    done++;
                    continue;
                }

                // Sub-DB: position once, keep the xcursor warm across the rest.
                // Each xc.Put reuses its stack, so sorted input appends without
                // a per-value descent. (C's MDB_MULTIPLE does the same via its
                // put loop reusing mc.)
                int t = TouchPath();
                if (t != 0) throw new LmdbException(LmdbErr.Problem, "touch failed");
                leaf = Page.NodePtr(_pg[_top], _ki[_top]);
                XCursorInit1(leaf);
                var xc = _xc!;
                ulong before = Db.Entries(_mxDbRec);
                for (; done < count; done++)
                    xc.Put(values.Slice(done * itemSize, itemSize), ReadOnlySpan<byte>.Empty, flags);

                // Write back the sub-DB record and the parent entry count once.
                leaf = Page.NodePtr(_pg[_top], _ki[_top]);
                System.Buffer.MemoryCopy(_mxDbRec, Node.Data(leaf), Db.Size48, Db.Size48);
                Db.SetEntries(_db.DbRec,
                    Db.Entries(_db.DbRec) + (Db.Entries(_mxDbRec) - before));
            }
        }
        return (int)(Db.Entries(_db.DbRec) - beforeMain);
    }

    /// <summary>Insert or update <paramref name="key"/> with an UNINITIALIZED
    /// value of <paramref name="size"/> bytes and return a writable span into
    /// the page for the caller to fill (MDB_RESERVE — saves one buffer copy for
    /// serializers). The span is valid until the next operation on this
    /// transaction. Incompatible with DUPSORT databases (values there are
    /// sorted by content).</summary>
    public Span<byte> PutReserve(ReadOnlySpan<byte> key, int size, PutFlags flags = 0)
    {
        if (HasDupSort)
            throw new LmdbException(LmdbErr.Incompatible, "MDB_RESERVE is incompatible with MDB_DUPSORT");
        if (size < 0)
            throw new LmdbException(LmdbErr.BadValsize, "negative reserve size");

        // A null-source span of the requested length: every copy site in the
        // write path skips null sources, so the space is allocated unwritten.
        var phantom = new ReadOnlySpan<byte>((void*)null, size);
        Put(key, phantom, flags | PutFlags.Reserve);

        byte* leaf = Page.NodePtr(_pg[_top], _ki[_top]);
        if ((Node.Flags(leaf) & Const.F_BIGDATA) != 0)
        {
            byte* omp = _txn.GetPage(ReadU64(Node.Data(leaf)));
            return new Span<byte>(omp + Const.PAGEHDRSZ, size);
        }
        return new Span<byte>(Node.Data(leaf), size);
    }
}
