// Read-only structural integrity walker for LMDB data files.
//
// Operates directly on the data file bytes (RandomAccess reads, no mmap, no
// lockfile, no LmdbEnvironment) so it can inspect environments the engine
// itself would refuse to open, and so running it can never mutate evidence.
//
// For each of the two meta pages it walks the free-DB tree, the main-DB tree,
// every named sub-database (F_SUBDATA records in the main DB) and every DUPSORT
// sub-tree, building a page-ownership map. It reports:
//   - duplicate page ownership (one pgno reached from two places)
//   - pages both reachable and present in the freelist
//   - duplicate page IDs inside/across freelist records
//   - references beyond last_pg, to meta pages, or cyclic references
//   - md_depth / md_*_pages / md_entries disagreements with the walked tree
//   - malformed page/node geometry (lower/upper, node offsets, key order)
using System.Text;

namespace Lmdb;

public enum IntegritySeverity { Info, Warning, Error }

public sealed record IntegrityFinding(
    IntegritySeverity Severity,
    string Code,
    string Message,
    int Meta,           // which meta snapshot (0/1) the finding belongs to; -1 = file-level
    ulong Page)         // page the finding anchors to; ulong.MaxValue = none
{
    public override string ToString()
        => $"[{Severity}] {Code} meta={Meta} page={(Page == ulong.MaxValue ? "-" : Page.ToString())}: {Message}";
}

public sealed class IntegrityMetaSummary
{
    public int MetaIndex;
    public bool Valid;
    public ulong TxnId;
    public ulong LastPg;
    public ulong MainRoot;
    public ulong FreeRoot;
    public List<(string Name, ulong Root, ulong Entries)> NamedDatabases = new();
    public ulong ReachablePages;
    public ulong FreelistPages;
}

public sealed class IntegrityReport
{
    public string FilePath = "";
    public uint PageSize;
    public List<IntegrityMetaSummary> Metas = new();
    public List<IntegrityFinding> Findings = new();

    public bool Clean => !Findings.Any(f => f.Severity == IntegritySeverity.Error);

    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Integrity report: {FilePath}");
        sb.AppendLine($"  page size: {PageSize}");
        foreach (var m in Metas)
        {
            sb.AppendLine($"  meta {m.MetaIndex}: valid={m.Valid} txnid={m.TxnId} last_pg={m.LastPg} " +
                          $"main_root={Fmt(m.MainRoot)} free_root={Fmt(m.FreeRoot)} " +
                          $"reachable={m.ReachablePages} freelisted={m.FreelistPages}");
            foreach (var (name, root, entries) in m.NamedDatabases)
                sb.AppendLine($"    named db '{name}': root={Fmt(root)} entries={entries}");
        }
        if (Findings.Count == 0)
        {
            sb.AppendLine("  no findings — both snapshots structurally consistent.");
        }
        else
        {
            sb.AppendLine($"  findings ({Findings.Count}):");
            foreach (var f in Findings) sb.AppendLine($"    {f}");
        }
        return sb.ToString();

        static string Fmt(ulong pgno) => pgno == Const.P_INVALID ? "P_INVALID" : pgno.ToString();
    }
}

public static class LmdbIntegrityChecker
{
    /// <summary>Check an environment path (directory containing data.mdb, or a
    /// data file itself). Read-only; never touches the lock file.</summary>
    public static IntegrityReport Check(string path)
    {
        string dataFile = Directory.Exists(path) ? System.IO.Path.Combine(path, "data.mdb") : path;
        using var fs = new FileStream(dataFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new Walker(fs, dataFile).Run();
    }

    private sealed class Walker
    {
        private readonly FileStream _fs;
        private readonly IntegrityReport _report = new();
        private uint _psize;
        private long _fileSize;

        // per-meta state
        private int _meta;
        private ulong _lastPg;
        private Dictionary<ulong, string> _owner = new();      // pgno -> first owner
        private Dictionary<ulong, ulong> _freelisted = new();  // pgno -> freelist record txnid

        public Walker(FileStream fs, string path)
        {
            _fs = fs;
            _fileSize = fs.Length;
            _report.FilePath = path;
        }

        private void Add(IntegritySeverity sev, string code, string msg, ulong page = ulong.MaxValue)
            => _report.Findings.Add(new IntegrityFinding(sev, code, msg, _meta, page));

        public IntegrityReport Run()
        {
            byte[] header = ReadRaw(0, 4096);
            _psize = HeaderPageSize(header);
            if (_psize == 0)
            {
                _meta = -1;
                Add(IntegritySeverity.Error, "bad-meta0", "page 0 is not a valid meta page; cannot determine page size");
                return _report;
            }
            _report.PageSize = _psize;

            for (int m = 0; m < Const.NUM_METAS; m++)
                CheckMeta(m);
            return _report;
        }

        private uint HeaderPageSize(byte[] p0)
        {
            // mm_magic at +16+0, mm_version at +16+4, mm_psize = mm_dbs[FREE].md_pad at +16+24
            if (p0.Length < 64) return 0;
            uint magic = BitConverter.ToUInt32(p0, 16);
            uint version = BitConverter.ToUInt32(p0, 20);
            ushort flags = BitConverter.ToUInt16(p0, 10);
            if (magic != Const.MDB_MAGIC || version != Const.MDB_DATA_VERSION || (flags & Const.P_META) == 0)
                return 0;
            uint psize = BitConverter.ToUInt32(p0, 16 + 24);
            return psize is >= Const.MIN_PAGESIZE and <= Const.MAX_PAGESIZE ? psize : 0;
        }

        // ---- meta snapshot walk ----

        private void CheckMeta(int metaIndex)
        {
            _meta = metaIndex;
            _owner = new Dictionary<ulong, string>();
            _freelisted = new Dictionary<ulong, ulong>();

            var summary = new IntegrityMetaSummary { MetaIndex = metaIndex };
            _report.Metas.Add(summary);

            byte[] mp;
            try { mp = ReadPage((ulong)metaIndex); }
            catch (Exception e)
            {
                Add(IntegritySeverity.Error, "meta-unreadable", $"meta page {metaIndex}: {e.Message}");
                return;
            }

            uint magic = BitConverter.ToUInt32(mp, 16);
            uint version = BitConverter.ToUInt32(mp, 20);
            ushort pflags = BitConverter.ToUInt16(mp, 10);
            if (magic != Const.MDB_MAGIC || version != Const.MDB_DATA_VERSION || (pflags & Const.P_META) == 0)
            {
                Add(IntegritySeverity.Warning, "meta-invalid",
                    $"meta page {metaIndex} is not a valid committed meta (magic/version/flags)");
                return;
            }

            summary.Valid = true;
            summary.TxnId = BitConverter.ToUInt64(mp, Meta.TxnIdOffset);
            summary.LastPg = _lastPg = BitConverter.ToUInt64(mp, Meta.LastPgOffset);
            var freeDb = mp.AsSpan(Meta.DbsOffset, Db.Size48).ToArray();
            var mainDb = mp.AsSpan(Meta.DbsOffset + Db.Size48, Db.Size48).ToArray();
            summary.FreeRoot = DbRoot(freeDb);
            summary.MainRoot = DbRoot(mainDb);

            if ((_lastPg + 1) * _psize > (ulong)_fileSize)
                Add(IntegritySeverity.Error, "lastpg-beyond-file",
                    $"last_pg={_lastPg} implies {( _lastPg + 1) * _psize} bytes but file is {_fileSize}");

            // 1) Free-DB tree first: pages of the free-DB itself + freelist contents.
            WalkTree(freeDb, "free-DB", isFreeDb: true);

            // 2) Main DB and named sub-DBs.
            WalkTree(mainDb, "main", isFreeDb: false, collectNamed: summary);

            // 3) Cross checks: reachable ∩ freelisted.
            foreach (var (pgno, recTxn) in _freelisted)
            {
                if (_owner.TryGetValue(pgno, out var owner))
                    Add(IntegritySeverity.Error, "reachable-and-free",
                        $"page {pgno} is owned by {owner} but also in freelist record txn {recTxn}", pgno);
            }

            summary.ReachablePages = (ulong)_owner.Count;
            summary.FreelistPages = (ulong)_freelisted.Count;

            // 4) Unreachable, non-freelisted pages (leaks) — informational.
            ulong leaked = 0;
            for (ulong p = Const.NUM_METAS; p <= _lastPg; p++)
                if (!_owner.ContainsKey(p) && !_freelisted.ContainsKey(p)) leaked++;
            if (leaked > 0)
                Add(IntegritySeverity.Info, "leaked-pages",
                    $"{leaked} pages neither reachable nor freelisted (space leak, not corruption)");
        }

        private static ulong DbRoot(byte[] dbRec) => BitConverter.ToUInt64(dbRec, 40);

        // ---- tree walk ----

        private sealed class TreeStats
        {
            public ulong Branch, Leaf, Overflow, Entries;
            public int MaxDepth;
        }

        private void WalkTree(byte[] dbRec, string name, bool isFreeDb, IntegrityMetaSummary? collectNamed = null)
        {
            ulong root = DbRoot(dbRec);
            ushort depth = BitConverter.ToUInt16(dbRec, 6);
            ulong entries = BitConverter.ToUInt64(dbRec, 32);

            if (root == Const.P_INVALID)
            {
                if (depth != 0 || entries != 0)
                    Add(IntegritySeverity.Error, "empty-db-nonzero-counts",
                        $"db '{name}' has root=P_INVALID but depth={depth} entries={entries}");
                return;
            }

            var stats = new TreeStats();
            var pathSet = new HashSet<ulong>();
            WalkPage(root, name, dbRec, 1, stats, pathSet, isFreeDb);

            if (stats.MaxDepth != depth)
                Add(IntegritySeverity.Error, "depth-mismatch",
                    $"db '{name}': md_depth={depth} but walked depth={stats.MaxDepth}", root);
            ulong mdBranch = BitConverter.ToUInt64(dbRec, 8);
            ulong mdLeaf = BitConverter.ToUInt64(dbRec, 16);
            ulong mdOverflow = BitConverter.ToUInt64(dbRec, 24);
            if (stats.Branch != mdBranch || stats.Leaf != mdLeaf || stats.Overflow != mdOverflow)
                Add(IntegritySeverity.Warning, "page-count-mismatch",
                    $"db '{name}': md counts branch/leaf/overflow={mdBranch}/{mdLeaf}/{mdOverflow} " +
                    $"but walked={stats.Branch}/{stats.Leaf}/{stats.Overflow}", root);
            if (stats.Entries != entries)
                Add(IntegritySeverity.Warning, "entry-count-mismatch",
                    $"db '{name}': md_entries={entries} but walked entries={stats.Entries}", root);
        }

        private void WalkPage(ulong pgno, string owner, byte[] dbRec, int depth,
                              TreeStats stats, HashSet<ulong> pathSet, bool isFreeDb)
        {
            if (!ClaimPage(pgno, owner)) return;
            if (!pathSet.Add(pgno))
            {
                Add(IntegritySeverity.Error, "cycle", $"page {pgno} already on walk path of '{owner}'", pgno);
                return;
            }

            byte[] page;
            try { page = ReadPage(pgno); }
            catch (Exception e)
            {
                Add(IntegritySeverity.Error, "page-unreadable", $"page {pgno} ({owner}): {e.Message}", pgno);
                pathSet.Remove(pgno);
                return;
            }

            ulong storedPgno = BitConverter.ToUInt64(page, 0);
            if (storedPgno != pgno)
                Add(IntegritySeverity.Error, "pgno-mismatch",
                    $"page {pgno} ({owner}) header says pgno={storedPgno}", pgno);

            ushort flags = BitConverter.ToUInt16(page, 10);
            if ((flags & Const.P_DIRTY) != 0)
                Add(IntegritySeverity.Warning, "dirty-on-disk",
                    $"page {pgno} ({owner}) has P_DIRTY persisted", pgno);

            bool isBranch = (flags & Const.P_BRANCH) != 0;
            bool isLeaf = (flags & Const.P_LEAF) != 0;
            bool isLeaf2 = (flags & Const.P_LEAF2) != 0;

            if (isBranch == isLeaf)  // both or neither
            {
                Add(IntegritySeverity.Error, "bad-page-type",
                    $"page {pgno} ({owner}) flags=0x{flags:x} is not exactly one of branch/leaf", pgno);
                pathSet.Remove(pgno);
                return;
            }

            if (isBranch) stats.Branch++; else stats.Leaf++;
            if (depth > stats.MaxDepth) stats.MaxDepth = depth;

            ushort lower = BitConverter.ToUInt16(page, 12);
            ushort upper = BitConverter.ToUInt16(page, 14);
            if (lower < Const.PAGEHDRSZ || upper > _psize || lower > upper)
            {
                Add(IntegritySeverity.Error, "bad-page-bounds",
                    $"page {pgno} ({owner}) lower={lower} upper={upper} psize={_psize}", pgno);
                pathSet.Remove(pgno);
                return;
            }

            int numKeys = (lower - Const.PAGEHDRSZ) >> 1;
            if (isBranch && numKeys < 2)
                Add(IntegritySeverity.Error, "branch-underflow",
                    $"branch page {pgno} ({owner}) has {numKeys} keys", pgno);

            if (isLeaf2)
            {
                stats.Entries += (ulong)numKeys;
                pathSet.Remove(pgno);
                return;
            }

            byte[]? prevKey = null;
            ushort dbFlags = BitConverter.ToUInt16(dbRec, 4);

            for (int i = 0; i < numKeys; i++)
            {
                ushort off = BitConverter.ToUInt16(page, Const.PAGEHDRSZ + i * 2);
                if (off < lower || off + Const.NODESIZE > _psize)
                {
                    Add(IntegritySeverity.Error, "bad-node-offset",
                        $"page {pgno} ({owner}) node {i} offset {off} outside [{lower},{_psize})", pgno);
                    continue;
                }
                ushort nflags = BitConverter.ToUInt16(page, off + 4);
                ushort ksize = BitConverter.ToUInt16(page, off + 6);
                if (off + Const.NODESIZE + ksize > _psize)
                {
                    Add(IntegritySeverity.Error, "node-key-overrun",
                        $"page {pgno} ({owner}) node {i} key overruns page", pgno);
                    continue;
                }

                // key ordering (skip branch slot 0, which is keyless)
                if (!(isBranch && i == 0) && ksize > 0)
                {
                    var key = page.AsSpan(off + Const.NODESIZE, ksize).ToArray();
                    if (prevKey != null && CompareKeys(prevKey, key, dbFlags) >= 0)
                        Add(IntegritySeverity.Error, "key-order",
                            $"page {pgno} ({owner}) node {i} key not greater than predecessor", pgno);
                    prevKey = key;
                }

                if (isBranch)
                {
                    ulong child = BitConverter.ToUInt16(page, off + 0)
                                | ((ulong)BitConverter.ToUInt16(page, off + 2) << 16)
                                | ((ulong)nflags << Const.PGNO_TOPWORD);
                    WalkPage(child, owner, dbRec, depth + 1, stats, pathSet, isFreeDb);
                    continue;
                }

                // leaf node
                uint dsize = (uint)(BitConverter.ToUInt16(page, off + 0)
                           | (BitConverter.ToUInt16(page, off + 2) << 16));
                stats.Entries++;

                if ((nflags & Const.F_BIGDATA) != 0)
                {
                    if (off + Const.NODESIZE + ksize + 8 > _psize)
                    {
                        Add(IntegritySeverity.Error, "node-data-overrun",
                            $"page {pgno} ({owner}) bigdata node {i} overruns page", pgno);
                        continue;
                    }
                    ulong ovf = BitConverter.ToUInt64(page, off + Const.NODESIZE + ksize);
                    ClaimOverflow(ovf, owner, dsize, stats);
                    if (isFreeDb)
                    {
                        // Large freelist records legitimately spill to overflow
                        // pages after mass deletions; validate their IDL content
                        // (contiguous bytes starting after the first page header).
                        try
                        {
                            var idl = ReadRaw((long)ovf * _psize + Const.PAGEHDRSZ, (int)dsize);
                            CheckFreelistRecord(page, off, ksize, idl);
                        }
                        catch (Exception e)
                        {
                            Add(IntegritySeverity.Error, "freelist-overflow-unreadable",
                                $"free-DB overflow record at page {ovf}: {e.Message}", ovf);
                        }
                    }
                    continue;
                }

                if (off + Const.NODESIZE + ksize + dsize > _psize)
                {
                    Add(IntegritySeverity.Error, "node-data-overrun",
                        $"page {pgno} ({owner}) node {i} data (dsize={dsize}) overruns page", pgno);
                    continue;
                }

                var data = page.AsSpan(off + Const.NODESIZE + ksize, (int)dsize);

                if (isFreeDb)
                {
                    CheckFreelistRecord(page, off, ksize, data);
                }
                else if ((nflags & Const.F_SUBDATA) != 0 && (nflags & Const.F_DUPDATA) == 0
                         && owner == "main" && dsize == Db.Size48)
                {
                    // Named sub-database record in the main DB.
                    string name = SafeName(page.AsSpan(off + Const.NODESIZE, ksize));
                    var subRec = data.ToArray();
                    var summary = _report.Metas[^1];
                    summary.NamedDatabases.Add((name, DbRoot(subRec), BitConverter.ToUInt64(subRec, 32)));
                    WalkTree(subRec, $"named:{name}", isFreeDb: false);
                }
                else if ((nflags & (Const.F_DUPDATA | Const.F_SUBDATA)) == (Const.F_DUPDATA | Const.F_SUBDATA)
                         && dsize == Db.Size48)
                {
                    // DUPSORT sub-tree: data is an MDB_db; entries counted from the sub-tree.
                    var subRec = data.ToArray();
                    stats.Entries--;   // replace the node entry with the dup count
                    stats.Entries += BitConverter.ToUInt64(subRec, 32);
                    WalkTree(subRec, $"{owner}/dup", isFreeDb: false);
                }
                else if ((nflags & Const.F_DUPDATA) != 0)
                {
                    // Inline sub-page (P_SUBP): dup values embedded in this node's data.
                    stats.Entries--;
                    stats.Entries += CountSubPageEntries(data, pgno, owner);
                }
            }
            pathSet.Remove(pgno);
        }

        private ulong CountSubPageEntries(ReadOnlySpan<byte> subPage, ulong pgno, string owner)
        {
            if (subPage.Length < Const.PAGEHDRSZ)
            {
                Add(IntegritySeverity.Error, "subpage-truncated",
                    $"page {pgno} ({owner}) inline sub-page shorter than header", pgno);
                return 0;
            }
            ushort lower = BitConverter.ToUInt16(subPage.Slice(12, 2));
            return (ulong)((lower - Const.PAGEHDRSZ) >> 1);
        }

        private void ClaimOverflow(ulong ovf, string owner, uint dsize, TreeStats stats)
        {
            if (!ClaimPage(ovf, owner + "/overflow")) return;
            byte[] page;
            try { page = ReadPage(ovf); }
            catch (Exception e)
            {
                Add(IntegritySeverity.Error, "page-unreadable", $"overflow page {ovf} ({owner}): {e.Message}", ovf);
                return;
            }
            ushort flags = BitConverter.ToUInt16(page, 10);
            if ((flags & Const.P_OVERFLOW) == 0)
            {
                Add(IntegritySeverity.Error, "overflow-flag-missing",
                    $"page {ovf} ({owner}) referenced as overflow but flags=0x{flags:x}", ovf);
                return;
            }
            uint npages = BitConverter.ToUInt32(page, 12);
            uint expected = (uint)(((Const.PAGEHDRSZ - 1 + dsize) / _psize) + 1);
            if (npages != expected)
                Add(IntegritySeverity.Warning, "overflow-count",
                    $"page {ovf} ({owner}) header claims {npages} pages, dsize={dsize} implies {expected}", ovf);
            stats.Overflow += npages;
            for (ulong i = 1; i < npages; i++)
                ClaimPage(ovf + i, owner + "/overflow");
        }

        private void CheckFreelistRecord(byte[] page, int off, int ksize, ReadOnlySpan<byte> data)
        {
            ulong pgno = BitConverter.ToUInt64(page, 0);
            if (ksize != 8)
            {
                Add(IntegritySeverity.Error, "freelist-bad-key",
                    $"free-DB record key size {ksize} != 8", pgno);
                return;
            }
            ulong recTxn = BitConverter.ToUInt64(page, off + Const.NODESIZE);
            var metaSummary = _report.Metas[^1];
            if (recTxn > metaSummary.TxnId)
                Add(IntegritySeverity.Error, "freelist-future-txn",
                    $"free-DB record txn {recTxn} newer than snapshot txn {metaSummary.TxnId}", pgno);

            if (data.Length < 8)
            {
                Add(IntegritySeverity.Error, "freelist-truncated",
                    $"free-DB record txn {recTxn} shorter than IDL count", pgno);
                return;
            }
            ulong count = BitConverter.ToUInt64(data.Slice(0, 8));
            if ((count + 1) * 8 > (ulong)data.Length)
            {
                Add(IntegritySeverity.Error, "freelist-truncated",
                    $"free-DB record txn {recTxn} claims {count} ids but has {data.Length} bytes", pgno);
                count = (ulong)(data.Length / 8) - 1;
            }
            ulong prev = 0;
            bool first = true;
            for (ulong i = 0; i < count; i++)
            {
                ulong id = BitConverter.ToUInt64(data.Slice((int)(i + 1) * 8, 8));
                if (id < Const.NUM_METAS || id > _lastPg)
                    Add(IntegritySeverity.Error, "freelist-bad-id",
                        $"free-DB record txn {recTxn} contains invalid page id {id} (last_pg={_lastPg})", id);
                if (!first && id >= prev)
                    Add(IntegritySeverity.Error, "freelist-order",
                        $"free-DB record txn {recTxn} not strictly descending at id {id}", id);
                first = false; prev = id;
                if (_freelisted.TryGetValue(id, out var priorTxn))
                    Add(IntegritySeverity.Error, "freelist-duplicate",
                        $"page {id} freelisted twice: record txn {priorTxn} and txn {recTxn}", id);
                else
                    _freelisted[id] = recTxn;
            }
        }

        /// <summary>Record ownership of a page; report duplicates. Returns false when
        /// the page must not be walked (invalid id or already claimed).</summary>
        private bool ClaimPage(ulong pgno, string owner)
        {
            if (pgno < Const.NUM_METAS || pgno > _lastPg)
            {
                Add(IntegritySeverity.Error, "invalid-page-ref",
                    $"'{owner}' references page {pgno} outside [{Const.NUM_METAS},{_lastPg}]", pgno);
                return false;
            }
            if (_owner.TryGetValue(pgno, out var prior))
            {
                Add(IntegritySeverity.Error, "duplicate-ownership",
                    $"page {pgno} owned by both '{prior}' and '{owner}'", pgno);
                return false;
            }
            _owner[pgno] = owner;
            return true;
        }

        private static string SafeName(ReadOnlySpan<byte> bytes)
        {
            try { return Encoding.UTF8.GetString(bytes); }
            catch { return Convert.ToHexString(bytes); }
        }

        private static int CompareKeys(byte[] a, byte[] b, ushort dbFlags)
        {
            if ((dbFlags & Const.MDB_INTEGERKEY) != 0 && a.Length == 8 && b.Length == 8)
                return BitConverter.ToUInt64(a, 0).CompareTo(BitConverter.ToUInt64(b, 0));
            if ((dbFlags & Const.MDB_REVERSEKEY) != 0)
            {
                int min = Math.Min(a.Length, b.Length);
                for (int i = 1; i <= min; i++)
                {
                    int d = a[^i].CompareTo(b[^i]);
                    if (d != 0) return d;
                }
                return a.Length.CompareTo(b.Length);
            }
            return a.AsSpan().SequenceCompareTo(b);
        }

        // ---- raw IO ----

        private byte[] ReadPage(ulong pgno)
        {
            long offset = checked((long)pgno * _psize);
            if (offset + _psize > _fileSize)
                throw new IOException($"page {pgno} beyond end of file");
            return ReadRaw(offset, (int)_psize);
        }

        private byte[] ReadRaw(long offset, int len)
        {
            if (offset + len > _fileSize) len = (int)Math.Max(0, _fileSize - offset);
            var buf = new byte[len];
            int done = 0;
            while (done < len)
            {
                int n = System.IO.RandomAccess.Read(_fs.SafeFileHandle, buf.AsSpan(done), offset + done);
                if (n <= 0) throw new IOException("short read");
                done += n;
            }
            return buf;
        }
    }
}
