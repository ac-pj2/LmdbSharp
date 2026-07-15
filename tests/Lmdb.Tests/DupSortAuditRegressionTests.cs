using System.Text;
using Lmdb;
using Xunit;

namespace Lmdb.Tests;

// Regressions for the DUPSORT defects found in the 2026-07-15 code audit
// (docs/CODE-AUDIT-2026-07-15.md S1-5, S1-6, S1-7, S2-1).
public class DupSortAuditRegressionTests
{
    private static (LmdbEnvironment env, string path) OpenEnv(DatabaseFlags extra = 0)
    {
        var path = $"/tmp/lmdb-cs/dup-audit-{Guid.NewGuid():N}";
        Directory.CreateDirectory(path);
        var env = LmdbEnvironment.Open(path,
            new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 24, MaxDbs = 4 });
        return (env, path);
    }

    private static byte[] Val(byte fill, int len)
    {
        var v = new byte[len];
        for (int i = 0; i < len; i++) v[i] = (byte)(fill + i % 13);
        return v;
    }

    private static List<byte[]> DupsOf(LmdbTransaction txn, LmdbDatabase db, byte[] key)
    {
        var result = new List<byte[]>();
        using var cur = txn.CreateCursor(db);
        if (!cur.TryGet(CursorOp.Set, key, out _, out var v)) return result;
        result.Add(v.ToArray());
        while (cur.TryGet(CursorOp.NextDup, default, out _, out v))
            result.Add(v.ToArray());
        return result;
    }

    // S1-5: sub-page → sub-DB conversion must transfer VARIABLE-size dup values
    // byte-exact. The old code sized every slot to the newly inserted value's
    // length, truncating/padding all existing dups (and overrunning its buffer).
    [Fact]
    public void SubPage_to_subDb_conversion_preserves_variable_size_dups()
    {
        var (env, path) = OpenEnv();
        try
        {
            var expected = new List<byte[]>();
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDatabase("idx", DatabaseFlags.Create | DatabaseFlags.DupSort);
                // Mixed value sizes guarantee that whenever the growing sub-page
                // crosses NodeMax and converts to a sub-DB, the values being
                // transferred have heterogeneous lengths.
                for (int i = 0; i < 60; i++)
                {
                    var v = Val((byte)(10 + i), 20 + (i % 7) * 25);
                    txn.Put(db, "k"u8, v);
                    expected.Add(v);
                }
                txn.Commit();
            }

            using (var read = env.BeginTransaction(readOnly: true))
            {
                var db = read.OpenDatabase("idx");
                var dups = DupsOf(read, db, "k"u8.ToArray());
                Assert.Equal(expected.Count, dups.Count);
                foreach (var v in expected)
                    Assert.Contains(dups, d => d.SequenceEqual(v));
            }
            Assert.True(LmdbIntegrityChecker.Check(path).Clean);
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }

    // S1-6: LEAF2 (DUPFIXED) sub-page room check. With 2-byte values the old
    // check never reported the sub-page full, so inserts wrote past the node's
    // data area into the neighboring node inside the parent leaf.
    [Fact]
    public void DupFixed_two_byte_values_grow_subpage_without_corrupting_neighbors()
    {
        var (env, path) = OpenEnv();
        try
        {
            var neighborValue = Val(7, 60);
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDatabase("idx",
                    DatabaseFlags.Create | DatabaseFlags.DupSort | DatabaseFlags.DupFixed);
                txn.Put(db, "a-neighbor"u8, neighborValue);
                for (int i = 0; i < 300; i++)
                {
                    var v = new byte[2] { (byte)(i >> 8), (byte)i };
                    txn.Put(db, "k"u8, v);
                }
                txn.Commit();
            }

            using (var read = env.BeginTransaction(readOnly: true))
            {
                var db = read.OpenDatabase("idx");
                Assert.Equal(neighborValue, read.Get(db, "a-neighbor"u8).ToArray());
                var dups = DupsOf(read, db, "k"u8.ToArray());
                Assert.Equal(300, dups.Count);
                for (int i = 0; i < 300; i++)
                    Assert.Contains(dups, d => d.Length == 2 && d[0] == (byte)(i >> 8) && d[1] == (byte)i);
            }
            Assert.True(LmdbIntegrityChecker.Check(path).Clean);
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }

    // S1-7: DUPSORT values become keys in the dup sub-tree, so they must obey
    // the key-size cap. The old code silently spilled an oversized value to an
    // overflow page; the next put for the key then read the overflow pgno as
    // inline data.
    [Fact]
    public void DupSort_rejects_values_larger_than_max_key_size()
    {
        var (env, path) = OpenEnv();
        try
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDatabase("idx", DatabaseFlags.Create | DatabaseFlags.DupSort);
            var ex = Assert.Throws<LmdbException>(() => txn.Put(db, "k"u8, Val(1, 5000)));
            Assert.Equal(LmdbErr.BadValsize, ex.ErrorCode);
            // A max-size value is still accepted.
            txn.Put(db, "k"u8, Val(2, 511));
            txn.Put(db, "k"u8, "x"u8);
            txn.Commit();
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }

    // S2-1: md_entries must count every dup value exactly once through inserts,
    // idempotent re-inserts, single-dup deletes, and whole-key deletes.
    [Fact]
    public void DupSort_entry_count_tracks_every_mutation_exactly()
    {
        var (env, path) = OpenEnv();
        try
        {
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDatabase("idx", DatabaseFlags.Create | DatabaseFlags.DupSort);
                txn.Put(db, "a"u8, "1"u8);                 // new key           -> 1
                txn.Put(db, "a"u8, "1"u8);                 // idempotent        -> 1
                txn.Put(db, "a"u8, "2"u8);                 // second dup        -> 2
                txn.Put(db, "a"u8, "3"u8);                 // third dup         -> 3
                txn.Put(db, "b"u8, "9"u8);                 // second key        -> 4
                txn.Commit();
            }
            AssertEntries(env, 4);

            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDatabase("idx");
                using var cur = txn.CreateCursor(db);
                Assert.True(cur.TryGet(CursorOp.GetBoth, "a"u8, "2"u8, out _, out _));
                cur.DeleteCurrent();                        // one dup of three  -> 3
                txn.Commit();
            }
            AssertEntries(env, 3);

            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDatabase("idx");
                Assert.True(txn.Delete(db, "a"u8));         // key with 2 dups   -> 1
                txn.Commit();
            }
            AssertEntries(env, 1);

            // Large dup set across the sub-DB conversion boundary.
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDatabase("idx");
                for (int i = 0; i < 400; i++)
                    txn.Put(db, "big"u8, Val((byte)i, 40 + i % 30));
                txn.Commit();
            }
            AssertEntries(env, 401);

            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDatabase("idx");
                Assert.True(txn.Delete(db, "big"u8));       // key with 400 dups -> 1
                txn.Commit();
            }
            AssertEntries(env, 1);
            Assert.True(LmdbIntegrityChecker.Check(path).Clean,
                LmdbIntegrityChecker.Check(path).Render());
        }
        finally { env.Dispose(); Directory.Delete(path, true); }

        static void AssertEntries(LmdbEnvironment env, ulong expected)
        {
            using var read = env.BeginTransaction(readOnly: true);
            Assert.Equal(expected, read.OpenDatabase("idx").Entries);
        }
    }
}
