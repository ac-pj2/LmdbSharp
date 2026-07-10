// Nested (child) transactions: a write txn can be begun with a parent. The child
// gets its own dirty-page list and DB record copies. On commit, the child's dirty
// pages are appended to the parent's dirty list and the DB records are copied back.
// On abort, the child's dirty pages are freed and the parent is unaffected.
//
// Ports the essential logic of mdb_txn_begin (child) + _mdb_txn_commit (parent merge)
// from mdb.c. Simplified: no spill list, no cursor shadowing.
using System.Runtime.InteropServices;

namespace Lmdb;

public sealed unsafe partial class LmdbTransaction
{
    internal LmdbTransaction? Parent { get; private set; }
    private bool _finished;   // true after Commit or Abort

    /// <summary>Begin a child transaction. The child gets its own dirty list and
    /// DB record copies; changes are visible to the parent only on commit.</summary>
    public LmdbTransaction BeginChild()
    {
        if (ReadOnly) throw new LmdbException(LmdbErr.BadTxn, "read-only transactions cannot have children");

        var child = new LmdbTransaction(Env, readOnly: false)
        {
            Parent = this,
            TxnId = TxnId,   // child shares the parent's txnid
            NextPgno = NextPgno,
        };
        // Child gets its own copies of the parent's DB records.
        // (The LmdbTransaction constructor already allocates these from the env's meta.)
        // But we need the PARENT's current state, not the env's meta state.
        Buffer.MemoryCopy(_dbFreeRec, child._dbFreeRec!, Db.Size48, Db.Size48);
        Buffer.MemoryCopy(_dbMainRec, child._dbMainRec!, Db.Size48, Db.Size48);
        return child;
    }

    /// <summary>Commit a child transaction: merge dirty pages + DB records into parent.</summary>
    private void CommitChild()
    {
        var parent = Parent!;
        if (Written && Dirty != null)
        {
            // Insert the child's dirty pages into the parent's dirty list (ascending).
            for (int i = 1; i <= Dirty.Count; i++)
                parent.Dirty!.Insert(Dirty[i]);
            // Copy the child's DB records back to the parent.
            Buffer.MemoryCopy(_dbFreeRec!, parent._dbFreeRec!, Db.Size48, Db.Size48);
            Buffer.MemoryCopy(_dbMainRec!, parent._dbMainRec!, Db.Size48, Db.Size48);
            // Transfer free pages.
            if (FreePgs != null && parent.FreePgs != null)
                Idl.AppendList(parent.FreePgs, FreePgs);
            parent.NextPgno = NextPgno;
            parent.Written = true;
        }
        // Child's DB record buffers are freed by FreeWriteState.
        Dirty = null;   // don't double-free the dirty pages (they're now in parent's list)
        FreePgs = null;
    }

    /// <summary>Abort a child transaction: free dirty pages, parent is unaffected.</summary>
    private void AbortChild()
    {
        // Free the child's dirty-page buffers (they are NOT in the parent's list).
        var dirty = Dirty;
        if (dirty != null)
        {
            for (int i = 1; i <= dirty.Count; i++)
                NativeMemory.Free(dirty[i].Ptr);
        }
        Dirty = null;
        FreePgs = null;
        // Parent's DB records and nextPgno are untouched.
    }
}
