using System.Text;
using Lmdb;
using Xunit;

namespace Lmdb.Tests;

// Regressions for reader snapshot-isolation defects (2026-07-15 audit S1-8,
// S1-9, S2-7, S2-8). The meta page a reader pins is overwritten in place by
// the writer two commits later; read transactions must copy their snapshot
// state at begin, and reader slots must outlive Commit-without-Dispose only
// until the txn actually ends.
public class ReaderIsolationTests
{
    private static (LmdbEnvironment env, string path) OpenEnv()
    {
        var path = $"/tmp/lmdb-cs/reader-iso-{Guid.NewGuid():N}";
        Directory.CreateDirectory(path);
        var env = LmdbEnvironment.Open(path,
            new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 24, MaxDbs = 4 });
        return (env, path);
    }

    private static void Put(LmdbEnvironment env, string db, string key, string value)
    {
        using var txn = env.BeginTransaction(readOnly: false);
        var h = db.Length == 0 ? txn.OpenDefaultDatabase()
                               : txn.OpenDatabase(db, DatabaseFlags.Create);
        txn.Put(h, Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value));
        txn.Commit();
    }

    // S1-8: a reader must keep observing its own snapshot even after the
    // writer has committed twice (recycling the reader's meta page).
    [Fact]
    public void Reader_snapshot_survives_two_subsequent_commits()
    {
        var (env, path) = OpenEnv();
        try
        {
            Put(env, "", "k", "v-original");
            using var reader = env.BeginTransaction(readOnly: true);
            var db = reader.OpenDefaultDatabase();
            Assert.Equal("v-original", Encoding.UTF8.GetString(reader.Get(db, "k"u8)));

            Put(env, "", "k", "v-second");    // meta page A
            Put(env, "", "k", "v-third");     // meta page B — the reader's page!
            Put(env, "", "k2", "unrelated");

            // The reader's view must be exactly its begin-time snapshot.
            Assert.Equal("v-original", Encoding.UTF8.GetString(reader.Get(db, "k"u8)));
            Assert.False(reader.TryGet(db, "k2"u8, out _),
                "reader observed a key committed after its snapshot");
            // A handle opened NOW inside the old txn must also see the snapshot.
            var db2 = reader.OpenDefaultDatabase();
            Assert.Equal("v-original", Encoding.UTF8.GetString(reader.Get(db2, "k"u8)));
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }

    // S1-9: resolving a named database inside a read txn must use the txn's
    // snapshot, not the environment's newest meta.
    [Fact]
    public void Named_database_opened_in_read_txn_resolves_against_snapshot()
    {
        var (env, path) = OpenEnv();
        try
        {
            Put(env, "records", "a", "1");
            using var reader = env.BeginTransaction(readOnly: true);

            Put(env, "records", "a", "2");
            Put(env, "records", "b", "9");

            var db = reader.OpenDatabase("records");
            Assert.Equal("1", Encoding.UTF8.GetString(reader.Get(db, "a"u8)));
            Assert.False(reader.TryGet(db, "b"u8, out _),
                "reader observed a named-DB key committed after its snapshot");
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }

    // S2-8: ending a read txn with Commit()/Abort() alone (no Dispose) must
    // release its reader slot — otherwise `oldest` is pinned forever and the
    // freelist can never recycle anything again.
    [Fact]
    public void Read_txn_commit_without_dispose_releases_reader_slot()
    {
        var (env, path) = OpenEnv();
        try
        {
            Put(env, "", "k", "v");
            var lockfile = (Lmdb.Platform.Lockfile)env.LockfileInfo!;

            var reader = env.BeginTransaction(readOnly: true);
            Assert.NotEqual(ulong.MaxValue, lockfile.FindOldestReader());
            reader.Commit();   // no Dispose
            Assert.Equal(ulong.MaxValue, lockfile.FindOldestReader());

            var reader2 = env.BeginTransaction(readOnly: true);
            reader2.Abort();   // no Dispose
            Assert.Equal(ulong.MaxValue, lockfile.FindOldestReader());
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }

    // S2-7 (stress): concurrent short readers must always observe internally
    // consistent snapshots while a writer churns. Two reads of the same key
    // inside one read txn must never differ.
    [Fact]
    public void Concurrent_readers_never_observe_torn_or_moving_snapshots()
    {
        var (env, path) = OpenEnv();
        try
        {
            Put(env, "", "counter", "0000000");
            using var stop = new CancellationTokenSource();
            var writer = Task.Run(() =>
            {
                for (int i = 1; !stop.IsCancellationRequested && i < 5000; i++)
                    Put(env, "", "counter", i.ToString("D7"));
            });

            var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
            var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 400; i++)
                {
                    using var txn = env.BeginTransaction(readOnly: true);
                    var db = txn.OpenDefaultDatabase();
                    string first = Encoding.UTF8.GetString(txn.Get(db, "counter"u8));
                    Thread.SpinWait(50);
                    string second = Encoding.UTF8.GetString(txn.Get(db, "counter"u8));
                    if (first != second)
                        failures.Add($"snapshot moved: '{first}' -> '{second}'");
                    if (first.Length != 7 || !first.All(char.IsDigit))
                        failures.Add($"torn read: '{first}'");
                }
            })).ToArray();

            Task.WaitAll(readers);
            stop.Cancel();
            writer.Wait();
            Assert.True(failures.IsEmpty, string.Join("\n", failures.Take(5)));
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }
}
