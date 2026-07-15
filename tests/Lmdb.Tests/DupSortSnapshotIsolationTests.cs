using Lmdb;
using Xunit;

namespace Lmdb.Tests;

// Regression: deleting a single duplicate (GetBoth + DeleteCurrent) used an
// xcursor positioned BEFORE TouchPath, so the deletion was applied to the
// committed page in place — mutating the durable snapshot without COW — while
// the transaction's own COW copy silently kept the entry. A concurrent reader
// observed the mutation mid-snapshot, and the committed result depended on
// which stale handle happened to be written back.
public class DupSortSnapshotIsolationTests
{
    private static byte[] K(long v)
    {
        var b = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(b, v);
        return b;
    }

    private static List<(long key, long val)> ScanAll(LmdbTransaction txn, LmdbDatabase db)
    {
        var result = new List<(long, long)>();
        using var cur = txn.CreateCursor(db);
        if (cur.TryGet(CursorOp.First, default, out var k, out var v))
            do
            {
                result.Add((System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(k),
                            System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(v)));
            } while (cur.TryGet(CursorOp.Next, default, out k, out v));
        return result;
    }

    [Theory]
    [InlineData(3)]     // few dups: inline sub-page (P_SUBP)
    [InlineData(400)]   // many dups: converted to a sub-DB (F_SUBDATA)
    public void DeleteCurrent_removes_dup_without_mutating_committed_snapshot(int dupCount)
    {
        var path = $"/tmp/lmdb-cs/dup-isolation-{dupCount}-{Guid.NewGuid():N}";
        Directory.CreateDirectory(path);
        try
        {
            using var env = LmdbEnvironment.Open(path,
                new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 24, MaxDbs = 4 });

            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDatabase("idx", DatabaseFlags.Create | DatabaseFlags.DupSort);
                for (long i = 1; i <= dupCount; i++)
                    txn.Put(db, K(25), K(i));
                txn.Put(db, K(30), K(999));
                txn.Commit();
            }

            // A reader holding the pre-delete snapshot for the whole write.
            using var oldReader = env.BeginTransaction(readOnly: true);
            var oldDb = oldReader.OpenDatabase("idx");

            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDatabase("idx");
                using var cur = txn.CreateCursor(db);
                Assert.True(cur.TryGet(CursorOp.GetBoth, K(25), K(1), out _, out _));
                cur.DeleteCurrent();
                txn.Commit();
            }

            // Snapshot isolation: the old reader must still see every original dup.
            var oldView = ScanAll(oldReader, oldDb);
            Assert.Equal(dupCount + 1, oldView.Count);
            Assert.Contains((25L, 1L), oldView);

            // New snapshot: exactly the one dup removed.
            using (var read = env.BeginTransaction(readOnly: true))
            {
                var view = ScanAll(read, read.OpenDatabase("idx"));
                Assert.Equal(dupCount, view.Count);
                Assert.DoesNotContain((25L, 1L), view);
                Assert.Contains((30L, 999L), view);
                if (dupCount >= 2) Assert.Contains((25L, 2L), view);
            }

            var report = LmdbIntegrityChecker.Check(path);
            Assert.True(report.Clean, report.Render());
        }
        finally { Directory.Delete(path, recursive: true); }
    }
}
