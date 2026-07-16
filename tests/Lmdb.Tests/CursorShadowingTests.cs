using System.Buffers.Binary;
using System.Text;
using Lmdb;
using Xunit;
using Xunit.Abstractions;

namespace Lmdb.Tests;

/// <summary>
/// Cursor shadowing: cursors of the same write transaction must stay valid
/// while OTHER cursors (or txn.Put/Delete) mutate the tree — COW repointing,
/// ki slides on insert/delete, split migration, merge following, root
/// growth/collapse, and the C_DEL convention (Next after your entry was
/// deleted returns its successor).
///
/// The randomized oracle: with correct fixups, a parked cursor's Next must
/// return exactly the successor of its last-returned key in the CURRENT
/// content (SortedSet shadow model), no matter what interleaved writes did —
/// and Prev the predecessor. Any stale page pointer, missed ki slide, or
/// wrong split migration breaks that invariant within a few hundred ops.
/// </summary>
public class CursorShadowingTests
{
    private readonly ITestOutputHelper _out;
    public CursorShadowingTests(ITestOutputHelper out_) => _out = out_;

    private static string TmpDir(string name)
    {
        string dir = $"/tmp/lmdb-cs/{name}";
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] K(int i) => Encoding.UTF8.GetBytes($"key{i:D8}");
    private static int UnK(ReadOnlySpan<byte> k) => int.Parse(Encoding.UTF8.GetString(k[3..]));
    private static byte[] V(int i) => Encoding.UTF8.GetBytes($"val{i:D8}");

    private sealed class Parked
    {
        public LmdbCursor Cur = null!;
        public int LastKey;      // last key the cursor returned (its position)
        public bool Positioned;  // false until parked
        public bool AtEnd;       // Next returned false
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Oracle_ParkedCursors_AlwaysSeeSuccessorAndPredecessor(int seed)
    {
        var rng = new Random(seed * 7919);
        string dir = TmpDir($"shadow-oracle-{seed}");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 128L << 20 });
        using var txn = env.BeginTransaction(readOnly: false);
        var db = txn.OpenDefaultDatabase();

        var model = new SortedSet<int>();
        var parked = new Parked[4];
        for (int i = 0; i < parked.Length; i++)
            parked[i] = new Parked { Cur = txn.CreateCursor(db) };

        int? Successor(int k)
        {
            foreach (var m in model.GetViewBetween(k + 1, int.MaxValue)) return m;
            return null;
        }
        int? Predecessor(int k)
        {
            var view = model.GetViewBetween(int.MinValue, k - 1);
            return view.Count > 0 ? view.Max : null;
        }

        const int Ops = 6000;
        for (int op = 0; op < Ops; op++)
        {
            int roll = rng.Next(100);
            if (roll < 30)
            {
                int k = rng.Next(3000);
                txn.Put(db, K(k), V(k));
                model.Add(k);
            }
            else if (roll < 42 && model.Count > 0)
            {
                int k = RandomExisting(rng, model);
                Assert.True(txn.Delete(db, K(k)));
                model.Remove(k);
                InvalidateHit(parked, k, model.Count);
            }
            else if (roll < 47 && model.Count > 60)
            {
                // Range delete burst: drives merges and (eventually) root collapse.
                int start = RandomExisting(rng, model);
                var doomed = new List<int>(model.GetViewBetween(start, start + 200));
                foreach (var k in doomed)
                {
                    Assert.True(txn.Delete(db, K(k)));
                    model.Remove(k);
                    InvalidateHit(parked, k, model.Count);
                }
            }
            else if (roll < 62)
            {
                // (Re)park a cursor at a random key via SetRange.
                var p = parked[rng.Next(parked.Length)];
                int target = rng.Next(3000);
                if (p.Cur.TryGet(CursorOp.SetRange, K(target), out var fk, out _))
                {
                    p.LastKey = UnK(fk);
                    p.Positioned = true;
                    p.AtEnd = false;
                    Assert.Equal(Successor(target - 1), p.LastKey);   // parked on first >= target
                }
                else
                {
                    p.Positioned = false;
                    Assert.Null(Successor(target - 1));
                }
            }
            else if (roll < 87)
            {
                var p = parked[rng.Next(parked.Length)];
                if (!p.Positioned || p.AtEnd) continue;
                var expect = Successor(p.LastKey);
                if (p.Cur.TryGet(CursorOp.Next, default, out var fk, out var fv))
                {
                    int got = UnK(fk);
                    Assert.True(expect.HasValue && got == expect.Value,
                        $"op {op}: Next from {p.LastKey} returned {got}, expected {(expect?.ToString() ?? "end")}");
                    Assert.Equal(V(got), fv.ToArray());
                    p.LastKey = got;
                }
                else
                {
                    Assert.True(expect == null, $"op {op}: Next from {p.LastKey} hit end, expected {expect}");
                    p.AtEnd = true;
                }
            }
            else
            {
                var p = parked[rng.Next(parked.Length)];
                if (!p.Positioned || p.AtEnd) continue;
                var expect = Predecessor(p.LastKey);
                if (p.Cur.TryGet(CursorOp.Prev, default, out var fk, out var fv))
                {
                    int got = UnK(fk);
                    Assert.True(expect.HasValue && got == expect.Value,
                        $"op {op}: Prev from {p.LastKey} returned {got}, expected {(expect?.ToString() ?? "start")}");
                    Assert.Equal(V(got), fv.ToArray());
                    p.LastKey = got;
                }
                else
                {
                    Assert.True(expect == null, $"op {op}: Prev from {p.LastKey} hit start, expected {expect}");
                    p.Positioned = false;   // before-first: require re-park
                }
            }
        }

        foreach (var p in parked) p.Cur.Dispose();
        txn.Commit();

        Assert.True(LmdbIntegrityChecker.Check(dir).Clean);
        using var read = env.BeginTransaction(readOnly: true);
        var rdb = read.OpenDefaultDatabase();
        Assert.Equal((ulong)model.Count, rdb.Entries);
        using var cur = read.CreateCursor(rdb);
        var seen = new List<int>();
        if (cur.TryGet(CursorOp.First, default, out var k2, out _))
            do { seen.Add(UnK(k2)); } while (cur.TryGet(CursorOp.Next, default, out k2, out _));
        Assert.Equal(model.ToList(), seen);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    public void Oracle_DupCursors_SurviveInterleavedDupWrites(int seed)
    {
        // Same oracle over DUPFIXED duplicate values of a hot key: parked dup
        // iterators must see the successor dup while another cursor inserts and
        // deletes dups across sub-page and sub-DB storage (incl. LEAF2 splits).
        var rng = new Random(seed * 104729);
        string dir = TmpDir($"shadow-dup-{seed}");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions
        {
            ReadOnly = false, MapSize = 128L << 20,
            MainDbFlags = DatabaseFlags.DupSort | DatabaseFlags.DupFixed,
        });
        using var txn = env.BeginTransaction(readOnly: false);
        var db = txn.OpenDefaultDatabase();
        byte[] hot = "hot"u8.ToArray();
        byte[] DV(long v) { var b = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); return b; }

        var model = new SortedSet<long>();
        var parked = new (LmdbCursor cur, long last, bool positioned, bool atEnd)[3];
        for (int i = 0; i < parked.Length; i++) parked[i].cur = txn.CreateCursor(db);
        using var writer = txn.CreateCursor(db);

        long? Successor(long v)
        {
            foreach (var m in model.GetViewBetween(v + 1, long.MaxValue)) return m;
            return null;
        }

        const int Ops = 5000;
        for (int op = 0; op < Ops; op++)
        {
            int roll = rng.Next(100);
            if (roll < 40)
            {
                long v = rng.Next(4000);
                txn.Put(db, hot, DV(v));
                model.Add(v);
            }
            else if (roll < 55 && model.Count > 0)
            {
                long v = RandomExisting(rng, model);
                Assert.True(writer.TryGet(CursorOp.GetBoth, hot, DV(v), out _, out _));
                writer.DeleteCurrent();
                model.Remove(v);
                for (int i = 0; i < parked.Length; i++)
                    if (model.Count == 0
                        || (parked[i].positioned && !parked[i].atEnd && parked[i].last == v))
                        parked[i].positioned = false;
            }
            else if (roll < 70)
            {
                ref var p = ref parked[rng.Next(parked.Length)];
                if (model.Count == 0) continue;
                long v = RandomExisting(rng, model);
                Assert.True(p.cur.TryGet(CursorOp.GetBoth, hot, DV(v), out _, out _),
                    $"op {op}: GetBoth failed for existing dup {v}");
                p.last = v; p.positioned = true; p.atEnd = false;
            }
            else
            {
                ref var p = ref parked[rng.Next(parked.Length)];
                if (!p.positioned || p.atEnd) continue;
                var expect = Successor(p.last);
                if (p.cur.TryGet(CursorOp.NextDup, default, out _, out var dv))
                {
                    long got = BinaryPrimitives.ReadInt64BigEndian(dv);
                    Assert.True(expect.HasValue && got == expect.Value,
                        $"op {op}: NextDup from {p.last} returned {got}, expected {(expect?.ToString() ?? "end")}");
                    p.last = got;
                }
                else
                {
                    Assert.True(expect == null,
                        $"op {op}: NextDup from {p.last} hit end, expected {expect}");
                    p.atEnd = true;
                }
            }
        }

        foreach (var p in parked) p.cur.Dispose();
        txn.Commit();

        Assert.True(LmdbIntegrityChecker.Check(dir).Clean);
        using var read = env.BeginTransaction(readOnly: true);
        var rdb = read.OpenDefaultDatabase();
        using var cur = read.CreateCursor(rdb);
        var seen = new List<long>();
        if (cur.TryGet(CursorOp.Set, hot, out _, out var v0))
        {
            seen.Add(BinaryPrimitives.ReadInt64BigEndian(v0));
            while (cur.TryGet(CursorOp.NextDup, default, out _, out var dv))
                seen.Add(BinaryPrimitives.ReadInt64BigEndian(dv));
        }
        Assert.Equal(model.ToList(), seen);
    }

    [Fact]
    public void ParkedCursor_FollowsCowOfCommittedPages()
    {
        // Position a cursor on COMMITTED (mmap) pages inside a write txn, then
        // write elsewhere: the touch fixup must repoint the parked stack, or the
        // cursor keeps reading the stale committed snapshot.
        string dir = TmpDir("shadow-cow");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 64L << 20 });
        using (var setup = env.BeginTransaction(readOnly: false))
        {
            var sdb = setup.OpenDefaultDatabase();
            for (int i = 0; i < 100; i++) setup.Put(sdb, K(i), V(i));
            setup.Commit();
        }

        using var txn = env.BeginTransaction(readOnly: false);
        var db = txn.OpenDefaultDatabase();
        using var parked = txn.CreateCursor(db);
        Assert.True(parked.TryGet(CursorOp.Set, K(50), out _, out _));

        // First write COWs the whole path the parked cursor sits on.
        txn.Put(db, K(10), Encoding.UTF8.GetBytes("updated"));

        // The parked cursor must observe writes made AFTER it was parked —
        // through its (repointed) stack, without re-seeking.
        txn.Put(db, K(51), Encoding.UTF8.GetBytes("fresh51"));
        Assert.True(parked.TryGet(CursorOp.Next, default, out var k, out var v));
        Assert.Equal(51, UnK(k));
        Assert.Equal("fresh51", Encoding.UTF8.GetString(v));
        txn.Commit();
    }

    [Fact]
    public void DeleteUnderCursor_NextReturnsSuccessor()
    {
        string dir = TmpDir("shadow-cdel");
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 64L << 20 });
        using var txn = env.BeginTransaction(readOnly: false);
        var db = txn.OpenDefaultDatabase();
        for (int i = 0; i < 10; i++) txn.Put(db, K(i), V(i));

        using var parked = txn.CreateCursor(db);
        Assert.True(parked.TryGet(CursorOp.Set, K(5), out _, out _));

        Assert.True(txn.Delete(db, K(5)));   // deletes the parked cursor's entry
        Assert.True(parked.TryGet(CursorOp.Next, default, out var k, out _));
        Assert.Equal(6, UnK(k));             // successor, not 7 (no double-skip)

        Assert.True(parked.TryGet(CursorOp.Prev, default, out k, out _));
        Assert.Equal(4, UnK(k));             // 5 is gone
        txn.Commit();
    }

    /// <summary>Deleting a parked cursor's own entry moves it to C LMDB's
    /// slot-anchored C_DEL state: interleaved inserts below the slot then make
    /// Next/Prev slide in SLOT terms, not key terms (C behaves identically), so
    /// the key-anchored oracle requires a re-park before asserting again. The
    /// pure delete-then-Next contract is covered deterministically by
    /// DeleteUnderCursor_NextReturnsSuccessor. Emptying the tree drops it and
    /// uninitializes every cursor (also C behavior).</summary>
    private static void InvalidateHit(Parked[] parked, int deletedKey, int remaining)
    {
        foreach (var p in parked)
            if (remaining == 0 || (p.Positioned && !p.AtEnd && p.LastKey == deletedKey))
                p.Positioned = false;
    }

    private static int RandomExisting(Random rng, SortedSet<int> model)
    {
        int idx = rng.Next(model.Count);
        foreach (var m in model) if (idx-- == 0) return m;
        throw new InvalidOperationException();
    }

    private static long RandomExisting(Random rng, SortedSet<long> model)
    {
        int idx = rng.Next(model.Count);
        foreach (var m in model) if (idx-- == 0) return m;
        throw new InvalidOperationException();
    }
}
