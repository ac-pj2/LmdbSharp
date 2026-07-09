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
//                   (key < oldest) into PgHead. Does NOT delete them.
//   FreelistSave() — called at commit. Deletes the consumed records, then
//                   writes this txn's mt_free_pgs as a new record (key = txnid).
using System.Runtime.InteropServices;

namespace Lmdb;

public sealed unsafe partial class Transaction
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

    /// <summary>Load reusable pages from the free-DB into the env's PgHead cache.
    /// Reads records with key &lt; oldest (= txnid - 1) using a read cursor on the
    /// committed snapshot. Does NOT delete the records (that happens at commit).</summary>
    private void LoadPgHead()
    {
        if (Db.Root(_dbFreeRec) == Const.P_INVALID) return;   // free-DB is empty
        var pgHead = Env.PgHead ??= new Idl(64);
        ulong oldest = FindOldest();

        var freeDb = new Database(Env, Const.FREE_DBI)
        {
            DbRec = _dbFreeRec,
            DbFlags = Db.PersistentFlags(_dbFreeRec),
        };
        freeDb.KeyCmp = Compare.PickKey(freeDb.DbFlags);
        freeDb.DupCmp = Compare.PickDup(freeDb.DbFlags);

        using var rc = new Cursor(this, freeDb);
        if (!rc.TryGet(CursorOp.First, default, out var k, out var data)) return;

        do
        {
            ulong recTxnid = ReadU64Span(k);
            if (recTxnid >= oldest) break;   // ascending order; stop
            MergeIdlInto(pgHead, data);
            Env.PgLast = recTxnid;           // track highest consumed key
        } while (rc.TryGet(CursorOp.Next, default, out k, out data));

        if (pgHead.Count > 0) pgHead.Sort();
    }

    /// <summary>Save the txn's free-page list to the free-DB and delete consumed
    /// old records (mdb_freelist_save). Called during Commit before page_flush.</summary>
    private void FreelistSave()
    {
        var freePgs = FreePgs!;
        var pgHead = Env.PgHead;

        // --- Phase 1: delete old free-DB records already consumed into PgHead ---
        //
        // (LoadPgHead read them at txn start; here we delete them via a write
        //  cursor so the free-DB doesn't grow unboundedly.)
        if (Db.Root(_dbFreeRec) != Const.P_INVALID && Env.PgLast > 0)
        {
            var freeDb = OpenFreeDatabase();
            // Collect keys to delete (read-only pass on the committed snapshot).
            var oldKeys = new System.Collections.Generic.List<ulong>();
            using (var rc = new Cursor(this, freeDb))
            {
                if (rc.TryGet(CursorOp.First, default, out var k, out _))
                {
                    do
                    {
                        ulong recTxnid = ReadU64Span(k);
                        if (recTxnid > Env.PgLast) break;
                        oldKeys.Add(recTxnid);
                    } while (rc.TryGet(CursorOp.Next, default, out k, out _));
                }
            }
            // Delete the consumed records (write pass — COW-touches free-DB pages).
            Span<byte> keyBuf = stackalloc byte[8];
            foreach (var key in oldKeys)
            {
                WriteU64LE(keyBuf, key);
                Delete(freeDb, keyBuf);
            }
            Env.PgLast = 0;
        }

        // --- Phase 2: write this txn's freed pages as a new record ---
        //
        // Key = this txn's txnid; value = the descending-sorted IDL of freed pages.
        // These pages CANNOT be reused until a future txn (oldest advances past us).
        if (freePgs.Count > 0)
        {
            freePgs.Sort();   // descending, as the IDL format requires
            var freeDb = OpenFreeDatabase();
            Span<byte> keyBytes = stackalloc byte[8];
            WriteU64LE(keyBytes, TxnId);
            int idlBytes = (freePgs.Count + 1) * 8;
            byte[] idlBuf = new byte[idlBytes];
            fixed (byte* p = idlBuf)
            {
                *(ulong*)p = (ulong)freePgs.Count;
                for (int i = 1; i <= freePgs.Count; i++)
                    *(ulong*)(p + i * 8) = freePgs[i];
            }
            Put(freeDb, keyBytes, idlBuf);
        }
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
