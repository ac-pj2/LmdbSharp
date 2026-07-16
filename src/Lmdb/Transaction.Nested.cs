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
        EnsureWritable();   // no second child, no broken parent

        var child = new LmdbTransaction(Env, readOnly: false, parent: this)
        {
            TxnId = TxnId,   // child shares the parent's txnid
            NextPgno = NextPgno,
        };
        // Child gets its own copies of the parent's DB records.
        // (The LmdbTransaction constructor already allocates these from the env's meta.)
        // But we need the PARENT's current state, not the env's meta state.
        Buffer.MemoryCopy(_dbFreeRec, child._dbFreeRec!, Db.Size48, Db.Size48);
        Buffer.MemoryCopy(_dbMainRec, child._dbMainRec!, Db.Size48, Db.Size48);
        ActiveChild = child;
        return child;
    }

    /// <summary>Commit a child transaction: merge dirty pages + DB records into parent.</summary>
    private void CommitChild()
    {
        var parent = Parent!;
        parent.ActiveChild = null;
        if (Written && Dirty != null)
        {
            // Insert the child's dirty pages into the parent's dirty list
            // (ascending). A rejected insert means a duplicate pgno — silently
            // dropping the child's buffer would commit the parent's version of
            // the page where the child's data should be.
            for (int i = 1; i <= Dirty.Count; i++)
            {
                int rc = parent.Dirty!.Insert(Dirty[i]);
                if (rc != 0)
                {
                    parent.Broken = true;
                    throw new LmdbException(LmdbErr.Corrupted,
                        $"nested commit: dirty page {Dirty[i].Id} could not merge (rc={rc})");
                }
                Dirty[i].Ptr = null;   // ownership transferred; cleanup skips it
            }
            // Copy the child's DB records back to the parent.
            Buffer.MemoryCopy(_dbFreeRec!, parent._dbFreeRec!, Db.Size48, Db.Size48);
            Buffer.MemoryCopy(_dbMainRec!, parent._dbMainRec!, Db.Size48, Db.Size48);
            // Merge the child's named sub-DB records: existing parent buffers are
            // updated IN PLACE (live parent handles keep pointing at them); new
            // names transfer buffer ownership to the parent.
            if (_subDbs != null)
            {
                var kept = new System.Collections.Generic.List<(byte[] name, IntPtr dbRec)>();
                foreach (var (name, recPtr) in _subDbs)
                {
                    bool mergedInPlace = false;
                    if (parent._subDbs != null)
                    {
                        for (int i = 0; i < parent._subDbs.Count; i++)
                        {
                            if (parent._subDbs[i].name.AsSpan().SequenceEqual(name))
                            {
                                Buffer.MemoryCopy((byte*)recPtr, (byte*)parent._subDbs[i].dbRec,
                                    Db.Size48, Db.Size48);
                                mergedInPlace = true;
                                break;
                            }
                        }
                    }
                    if (mergedInPlace)
                    {
                        kept.Add((name, recPtr));   // child still owns; freed below
                    }
                    else
                    {
                        parent._subDbs ??= new();
                        parent._subDbs.Add((name, recPtr));   // ownership moves
                    }
                }
                _subDbs = kept;
            }
            // Propagate child drops: remove the parent's record for each
            // dropped name, or the parent commit re-inserts the dropped DB
            // pointing at pages the child freed (reachable-and-free).
            if (_droppedNames != null)
            {
                foreach (var dropped in _droppedNames)
                {
                    if (parent._subDbs != null)
                    {
                        for (int i = 0; i < parent._subDbs.Count; i++)
                        {
                            if (parent._subDbs[i].name.AsSpan().SequenceEqual(dropped))
                            {
                                Mem.Free((void*)parent._subDbs[i].dbRec);
                                parent._subDbs.RemoveAt(i);
                                break;
                            }
                        }
                    }
                    (parent._droppedNames ??= new()).Add(dropped);
                }
            }

            // Transfer free pages.
            if (FreePgs != null && parent.FreePgs != null)
                Idl.AppendList(parent.FreePgs, FreePgs);
            // Transfer the reusable-page pool: the child allocated from its own
            // copy, so the parent adopts it (pages the child consumed stay
            // consumed). On child abort the parent keeps its unconsumed copy.
            parent.PgHeadLocal = PgHeadLocal;
            parent.PgLastLocal = PgLastLocal;
            parent.NextPgno = NextPgno;
            parent.Written = true;
        }
        // Child's DB record buffers are freed by FreeWriteState.
        Dirty = null;   // don't double-free the dirty pages (they're now in parent's list)
        FreePgs = null;
    }

    /// <summary>Abort a child transaction: free dirty pages, parent is unaffected
    /// (the child worked exclusively on its own record copies).</summary>
    private void AbortChild()
    {
        var parent = Parent!;
        parent.ActiveChild = null;
        if (Broken) parent.Broken = true;   // conservative: shared machinery may be tainted
        // Free the child's dirty-page buffers (they are NOT in the parent's list).
        var dirty = Dirty;
        if (dirty != null)
        {
            for (int i = 1; i <= dirty.Count; i++)
                Mem.Free(dirty[i].Ptr);
        }
        Dirty = null;
        FreePgs = null;
        // Parent's DB records and nextPgno are untouched.
    }
}
