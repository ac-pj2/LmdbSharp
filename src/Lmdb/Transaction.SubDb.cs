// Commit-time write-back of named sub-DB records.
//
// At commit, each opened named sub-DB's MDB_db record (which may have been
// modified — new root, updated entries/pages counts) is written back to the
// main DB as an F_SUBDATA node. Ports the sub-DB record write-back loop in
// mdb_txn_commit (mdb.c ~line 4182).
using System.Runtime.InteropServices;

namespace Lmdb;

public sealed unsafe partial class LmdbTransaction
{
    /// <summary>Write back dirty named sub-DB records to the main DB (mdb_txn_commit).</summary>
    private void WriteSubDbRecords()
    {
        if (_subDbs == null || _subDbs.Count == 0) return;

        var mainDb = OpenDefaultDatabase();
        foreach (var (name, dbRec) in _subDbs)
        {
            PutSubDbRecord(mainDb, name, (byte*)dbRec);
        }
    }

    /// <summary>Put a named sub-DB record (MDB_db, 48 bytes) into the main DB as an
    /// F_SUBDATA node. If the name already exists, update it; otherwise insert.</summary>
    private void PutSubDbRecord(LmdbDatabase mainDb, byte[] name, byte* dbRec)
    {
        using var cur = new LmdbCursor(this, mainDb);
        fixed (byte* np = name)
        {
            int rc = cur.SetPosition(np, name.Length, out bool exact);
            if (rc != 0 && rc != (int)LmdbErr.NotFound)
                throw new LmdbException((LmdbErr)rc);

            if (exact)
            {
                // Update: COW the path, delete old node, add new with F_SUBDATA.
                int t = cur.TouchPath();
                if (t != 0) throw new LmdbException(LmdbErr.Problem, "touch failed");
                cur.NodeDelPublic(0);
            }
            else
            {
                // New: COW the path (or create root if main DB is empty).
                if (mainDb.Root == Const.P_INVALID)
                {
                    cur.CreateRootLeaf();
                }
                else
                {
                    int t = cur.TouchPath();
                    if (t != 0) throw new LmdbException(LmdbErr.Problem, "touch failed");
                }
            }

            // Add the node with F_SUBDATA flag and the MDB_db record as data.
            cur.AddSubDbNode(np, name.Length, dbRec);

            // A NEW record is an entry of the main DB (C LMDB counts sub-DB
            // records in md_entries; updates replace in place).
            if (!exact)
                Db.SetEntries(_dbMainRec, Db.Entries(_dbMainRec) + 1);
        }
    }
}
