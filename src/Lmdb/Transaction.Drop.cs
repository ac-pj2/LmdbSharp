// mdb_drop: empty a database (delete all entries), optionally deleting the DBI
// itself. Ports mdb_drop / mdb_drop0 from mdb.c.
//
// For the default DB or read-only DBs, only the entries are removed (del=false).
// For named sub-DBs with del=true, the DBI record is also removed from the main DB.
using System.Runtime.InteropServices;

namespace Lmdb;

public sealed unsafe partial class LmdbTransaction
{
    /// <summary>Empty a database, optionally deleting the DBI record (mdb_drop).
    /// When <paramref name="delete"/> is true and this is a named sub-DB, the DBI
    /// node is also removed from the main DB.</summary>
    public void Drop(LmdbDatabase db, bool delete)
    {
        if (ReadOnly) throw new LmdbException(LmdbErr.Invalid, "read-only transaction");

        if (db.Root != Const.P_INVALID)
        {
            // Delete all entries by iterating and deleting each key.
            using var cur = new LmdbCursor(this, db);
            if (cur.TryGet(CursorOp.First, default, out var k, out _))
            {
                do { cur.Delete(k); }
                while (cur.TryGet(CursorOp.First, default, out k, out _));
            }
        }

        if (delete && db.Dbi >= Const.CORE_DBS)
        {
            // Named sub-DB: delete the DBI record from the main DB.
            DropNamedDbi(db);
        }

        Written = true;
    }

    /// <summary>Delete a named sub-DB's record from the main DB (mdb_drop with del).</summary>
    private void DropNamedDbi(LmdbDatabase db)
    {
        if (db.Name == null) return;

        // Remove the record from the main tree so commit cannot resurrect it
        // (works even when the DB was empty and never entered _subDbs).
        var mainDb = OpenDefaultDatabase();
        AllowNamedRecordDelete = true;
        try { Delete(mainDb, db.Name); }
        finally { AllowNamedRecordDelete = false; }

        // Detach this txn's mutable record. The buffer is NOT freed here — live
        // handles still point at it (property reads like Entries would be a
        // use-after-free; the old pointer match also truncated to 32 bits).
        // It is zeroed to read as an empty DB and parked until txn end.
        if (_subDbs != null)
        {
            for (int i = 0; i < _subDbs.Count; i++)
            {
                if (_subDbs[i].name.AsSpan().SequenceEqual(db.Name))
                {
                    byte* rec = (byte*)_subDbs[i].dbRec;
                    for (int b = 0; b < Db.Size48; b++) rec[b] = 0;
                    *(ulong*)(rec + 40) = Const.P_INVALID;
                    (_droppedRecs ??= new()).Add((IntPtr)rec);
                    _subDbs.RemoveAt(i);
                    break;
                }
            }
        }
    }
}
