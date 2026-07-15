// Freelist persistence: freed pages are written to the free-DB (FREE_DBI) keyed by
// the txnid that freed them, and old records (key < oldest reader) are consumed
// into the env's PgHead cache for reuse by future allocations.
//
// Ports the essential logic of mdb_freelist_save (mdb.c) and mdb_find_oldest.
// Simplifications: no loose pages, no MDB_RESERVE, no spill, no nested-txn
// freelist merging. Single-process (oldest = txnid - 1, no reader table).
//
// Two-phase design:
//   LoadPgHead()  — called at write-txn start. Reads old free-DB records
//                   (key < oldest) into the TRANSACTION's private PgHead view.
//                   Does NOT delete them and does NOT touch environment state.
//   FreelistSave() — called at commit. Deletes the consumed records, then
//                   writes this txn's mt_free_pgs as a new record (key = txnid).
//   PublishPgHead() — called by Commit AFTER FreelistSave succeeded. Only then
//                   does the surviving remainder become the environment pool.
//
// The txn-private view is the containment for the re-merge corruption: an
// aborted (or written-nothing) transaction leaves the on-disk records exactly
// as they were, so the next transaction consumes each record exactly once.
// (Regression: FreelistIntegrityTests.) The free-DB is the single source of
// truth — no reusable-page state survives in process memory between txns, so
// restarts and crashes lose nothing (FreelistPersistenceTests).
using System.Runtime.InteropServices;

namespace Lmdb;

public sealed unsafe partial class LmdbTransaction
{
    /// <summary>Find the oldest live reader's txnid. With a lockfile/reader table,
    /// scans the table for the minimum txnid. Without one (MDB_NOLOCK), uses
    /// txnid - 1 (the previous committed snapshot). (mdb_find_oldest)</summary>
    private ulong FindOldest()
    {
        var lf = Env.Lockfile;
        if (lf != null)
        {
            ulong oldest = lf.FindOldestReader();
            if (oldest != ulong.MaxValue) return oldest;
        }
        return TxnId - 1;
    }

    /// <summary>Key of the persisted pool-remainder record. Txnid 0 is never
    /// used by a real commit, and 0 &lt; oldest for every consumer, so the
    /// remainder is re-consumable immediately and can never collide.</summary>
    private const ulong PoolRecordKey = 0;

    /// <summary>Load reusable pages into this transaction's private pool from
    /// the free-DB records with key &lt; oldest (including the persisted
    /// remainder under key 0), read with a cursor on the committed snapshot.
    /// The records themselves are only deleted when this txn commits writes —
    /// the free-DB on disk is the single source of truth for reusable pages.</summary>
    private void LoadPgHead()
    {
        var pgHead = PgHeadLocal = new Idl(64);
        PgLastLocal = 0;
        _consumedPoolRecord = false;
        if (Db.Root(_dbFreeRec) == Const.P_INVALID) return;
        ulong oldest = FindOldest();

        var freeDb = new LmdbDatabase(Env, Const.FREE_DBI)
        {
            DbRec = _dbFreeRec,
            DbFlags = Db.PersistentFlags(_dbFreeRec),
        };
        freeDb.KeyCmp = Compare.PickKey(freeDb.DbFlags);
        freeDb.DupCmp = Compare.PickDup(freeDb.DbFlags);

        using var rc = new LmdbCursor(this, freeDb);
        if (!rc.TryGet(CursorOp.First, default, out var k, out var data)) return;

        do
        {
            ulong recTxnid = ReadU64Span(k);
            if (recTxnid >= oldest) break;   // ascending order; stop
            MergeIdlInto(pgHead, data);
            if (recTxnid == PoolRecordKey) _consumedPoolRecord = true;
            else PgLastLocal = recTxnid;     // track highest consumed real key
        } while (rc.TryGet(CursorOp.Next, default, out k, out data));

        if (pgHead.Count > 0) pgHead.Sort();
        AssertNoDuplicates(pgHead);
    }

    /// <summary>True when this txn merged the persisted pool-remainder record
    /// (key 0) into its pool — the record must then be rewritten or deleted at
    /// commit, or its pages would be handed out twice by a later txn.</summary>
    private bool _consumedPoolRecord;

    /// <summary>Refuse to allocate from a poisoned reusable-page pool. A duplicate
    /// here means the same physical page would be handed to two logical B-tree
    /// pages — the aliasing found in the preserved P3 environments. Failing the
    /// transaction is strictly better than writing the corruption to disk.</summary>
    private static void AssertNoDuplicates(Idl pgHead)
    {
        ulong dup = pgHead.FindAdjacentDuplicate();
        if (dup != 0)
            throw new LmdbException(LmdbErr.Corrupted,
                $"freelist integrity violation: page {dup} is reusable twice");
    }

    /// <summary>Save the txn's free-page list to the free-DB and delete consumed
    /// old records (mdb_freelist_save). Called during Commit before page_flush.</summary>
    private void FreelistSave()
    {
        var freePgs = FreePgs!;

        // --- Phase 1: delete old free-DB records already consumed into the pool ---
        //
        // (LoadPgHead read them at txn start; here we delete them via a write
        //  cursor so the free-DB doesn't grow unboundedly. Pages freed by these
        //  deletions land in freePgs BEFORE it is serialized below.)
        if (Db.Root(_dbFreeRec) != Const.P_INVALID && PgLastLocal > 0)
        {
            var oldDb = OpenFreeDatabase();
            // Collect keys to delete (read-only pass on the committed snapshot).
            var oldKeys = new System.Collections.Generic.List<ulong>();
            using (var rc = new LmdbCursor(this, oldDb))
            {
                if (rc.TryGet(CursorOp.First, default, out var k, out _))
                {
                    do
                    {
                        ulong recTxnid = ReadU64Span(k);
                        if (recTxnid > PgLastLocal) break;
                        oldKeys.Add(recTxnid);
                    } while (rc.TryGet(CursorOp.Next, default, out k, out _));
                }
            }
            // Delete the consumed records (write pass — COW-touches free-DB pages).
            Span<byte> keyBuf = stackalloc byte[8];
            foreach (var key in oldKeys)
            {
                if (key == PoolRecordKey) continue;   // rewritten/deleted below
                WriteU64LE(keyBuf, key);
                Delete(oldDb, keyBuf);
            }
        }

        // --- Phase 2: persist this txn's freed pages (key = TxnId) AND the
        // surviving pool remainder (key = 0) in one retry-until-stable loop ---
        //
        // The remainder must be durable: it used to live only in Env memory,
        // so every process exit leaked it permanently (the kill soak drove the
        // file to map-full). Pool allocation is disabled inside the loop so the
        // serialized remainder cannot be invalidated by its own write; pages
        // freed BY these writes join freePgs and are captured by the next
        // iteration (C LMDB loops in mdb_freelist_save for the same reason).
        var pool = PgHeadLocal;
        var freeDb = OpenFreeDatabase();
        Span<byte> keyBytes = stackalloc byte[8];
        NoPoolAlloc = true;
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                if (attempt >= 8)
                    throw new LmdbException(LmdbErr.Problem,
                        "freelist save did not stabilize");
                int before = freePgs.Count;

                if (pool != null && pool.Count > 0)
                {
                    WriteU64LE(keyBytes, PoolRecordKey);
                    Put(freeDb, keyBytes, SerializeIdl(pool));
                }
                else if (_consumedPoolRecord)
                {
                    // Pool fully consumed: the on-disk remainder record must go,
                    // or a later txn re-consumes pages that are now allocated.
                    WriteU64LE(keyBytes, PoolRecordKey);
                    Delete(freeDb, keyBytes);
                }

                if (freePgs.Count > 0)
                {
                    freePgs.Sort();   // descending, as the IDL format requires

                    // Integrity gate: a duplicate here means some page was freed
                    // twice this transaction (double COW or double delete).
                    // Persisting it would poison every future allocation drawn
                    // from the record — the exact seed of the aliasing found in
                    // the preserved P3 environments.
                    ulong dup = freePgs.FindAdjacentDuplicate();
                    if (dup != 0)
                        throw new LmdbException(LmdbErr.Corrupted,
                            $"freelist integrity violation: page {dup} freed twice in txn {TxnId}");
                    if (freePgs.First >= NextPgno || freePgs.Last < Const.NUM_METAS)
                        throw new LmdbException(LmdbErr.Corrupted,
                            $"freelist integrity violation: freed page range [{freePgs.Last},{freePgs.First}] " +
                            $"outside valid pages [{Const.NUM_METAS},{NextPgno - 1}] in txn {TxnId}");

                    WriteU64LE(keyBytes, TxnId);
                    Put(freeDb, keyBytes, SerializeIdl(freePgs));
                }

                if (freePgs.Count == before) break;   // no new frees — stable
            }
        }
        finally { NoPoolAlloc = false; }
    }

    private static byte[] SerializeIdl(Idl idl)
    {
        var buf = new byte[(idl.Count + 1) * 8];
        fixed (byte* p = buf)
        {
            *(ulong*)p = (ulong)idl.Count;
            for (int i = 1; i <= idl.Count; i++)
                *(ulong*)(p + i * 8) = idl[i];
        }
        return buf;
    }

    /// <summary>Merge a free-DB record's IDL (descending array, count at [0]) into
    /// the PgHead pool.</summary>
    private static void MergeIdlInto(Idl pgHead, ReadOnlySpan<byte> idlSpan)
    {
        if (idlSpan.Length < 8) return;
        ulong count = ReadU64Span(idlSpan);
        if (count == 0) return;
        for (int i = 0; i < (int)count; i++)
        {
            int off = (i + 1) * 8;
            if (off + 8 > idlSpan.Length) break;
            pgHead.Append(ReadU64Span(idlSpan[off..]));
        }
    }

    private static ulong ReadU64Span(ReadOnlySpan<byte> s)
        => s.Length >= 8
            ? (ulong)s[0] | ((ulong)s[1] << 8) | ((ulong)s[2] << 16) | ((ulong)s[3] << 24)
              | ((ulong)s[4] << 32) | ((ulong)s[5] << 40) | ((ulong)s[6] << 48) | ((ulong)s[7] << 56)
            : 0;

    private static void WriteU64LE(Span<byte> s, ulong v)
    {
        s[0] = (byte)v; s[1] = (byte)(v >> 8); s[2] = (byte)(v >> 16); s[3] = (byte)(v >> 24);
        s[4] = (byte)(v >> 32); s[5] = (byte)(v >> 40); s[6] = (byte)(v >> 48); s[7] = (byte)(v >> 56);
    }
}
