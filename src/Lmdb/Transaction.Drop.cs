// mdb_drop: empty a database (delete all entries), optionally deleting the DBI
// itself. Ports mdb_drop / mdb_drop0 from mdb.c.
//
// For the default DB or read-only DBs, only the entries are removed (del=false).
// For named sub-DBs with del=true, the DBI record is also removed from the main DB.
using System.Runtime.InteropServices;

namespace Lmdb;

public sealed unsafe partial class Transaction
{
    /// <summary>Empty a database, optionally deleting the DBI record (mdb_drop).
    /// When <paramref name="delete"/> is true and this is a named sub-DB, the DBI
    /// node is also removed from the main DB.</summary>
    public void Drop(Database db, bool delete)
    {
        if (ReadOnly) throw new LmdbException(LmdbErr.Invalid, "read-only transaction");

        if (db.Root != Const.P_INVALID)
        {
            // Delete all entries by iterating and deleting each key.
            using var cur = new Cursor(this, db);
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
    private void DropNamedDbi(Database db)
    {
        // Find the sub-DB's name in _subDbs and remove its record from the main DB.
        if (_subDbs == null) return;
        for (int i = 0; i < _subDbs.Count; i++)
        {
            if ((uint)_subDbs[i].dbRec == (uint)db.DbRec - 0)  // match by pointer
            {
                var mainDb = OpenDefaultDatabase();
                Delete(mainDb, _subDbs[i].name);
                NativeMemory.Free((void*)_subDbs[i].dbRec);
                _subDbs.RemoveAt(i);
                return;
            }
        }
    }
}
