// mdb_env_copy: copy the entire environment to a new path.
//
// Correctness requirements (audit S2-11): the copy must be a CONSISTENT
// snapshot. A raw memcpy of the live mmap raced concurrent writers — the live
// meta pages could describe pages copied before the writer overwrote them, and
// without a reader slot the writer was free to recycle snapshot pages mid-copy.
// The copy now runs inside a read transaction: the reader slot pins the
// snapshot's pages, data pages are copied from the mmap, and BOTH destination
// meta pages are rebuilt from the transaction's snapshot state.
namespace Lmdb;

public sealed unsafe partial class LmdbEnvironment
{
    /// <summary>Copy this environment to <paramref name="destPath"/> (mdb_env_copy).
    /// Creates a new directory with data.mdb. The copy is a complete, consistent
    /// snapshot of the committed state observed at the time of the call.</summary>
    public void Copy(string destPath)
    {
        System.IO.Directory.CreateDirectory(destPath);
        string destData = System.IO.Path.Combine(destPath, "data.mdb");

        using var txn = BeginTransaction(readOnly: true);
        ulong lastPg = txn.SnapshotLastPg;
        long bytesToCopy = (long)(lastPg + 1) * _psize;

        using var dest = Platform.MappedFile.OpenReadWrite(
            destData, System.Math.Max(_mapSize, bytesToCopy), create: true);
        byte* dstPtr = dest.Pointer;

        // Data pages (2..lastPg) straight from the mmap — the reader slot keeps
        // the writer from recycling any page this snapshot references.
        if (lastPg >= Const.NUM_METAS)
        {
            long dataOffset = (long)Const.NUM_METAS * _psize;
            Buffer.MemoryCopy(_mapPtr + dataOffset, dstPtr + dataOffset,
                bytesToCopy - dataOffset, bytesToCopy - dataOffset);
        }

        // Rebuild both meta pages from the SNAPSHOT (the live metas may already
        // describe newer commits whose pages we did not copy).
        byte* snapshotRecs = txn.SnapshotDbRecs;
        for (int i = 0; i < Const.NUM_METAS; i++)
        {
            byte* mp = dstPtr + (long)_psize * i;
            new Span<byte>(mp, (int)_psize).Clear();
            *(ulong*)(mp + 0) = (ulong)i;                 // mp_pgno
            *(ushort*)(mp + 10) = Const.P_META;           // mp_flags
            *(uint*)(mp + Const.PAGEHDRSZ + 0) = Const.MDB_MAGIC;
            *(uint*)(mp + Const.PAGEHDRSZ + 4) = Const.MDB_DATA_VERSION;
            *(ulong*)(mp + Const.PAGEHDRSZ + 16) = (ulong)_mapSize;
            Buffer.MemoryCopy(snapshotRecs, mp + Meta.DbsOffset,
                Const.CORE_DBS * Db.Size48, Const.CORE_DBS * Db.Size48);
            *(ulong*)(mp + Meta.LastPgOffset) = lastPg;
            *(ulong*)(mp + Meta.TxnIdOffset) = txn.Id;
        }

        dest.Flush();
        dest.Stream!.Flush(true);   // fsync
    }
}
