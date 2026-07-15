using System.Text;
using Lmdb;
using Xunit;

namespace Lmdb.Tests;

// Regressions for nested-transaction named-database defects (2026-07-15 audit
// S1-10, S2-13). Named sub-DB records must be shadowed per transaction like
// the core records are: child changes merge on commit and vanish on abort.
public class NestedTxnNamedDbTests
{
    private static (LmdbEnvironment env, string path) OpenEnv()
    {
        var path = $"/tmp/lmdb-cs/nested-named-{Guid.NewGuid():N}";
        Directory.CreateDirectory(path);
        var env = LmdbEnvironment.Open(path,
            new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 24, MaxDbs = 8 });
        return (env, path);
    }

    // S1-10a: a named DB written only in the child must survive the parent
    // commit — previously the child's record was never merged back, losing the
    // writes AND persisting the old root's pages as free while referenced.
    [Fact]
    public void Child_writes_to_named_database_merge_into_parent_commit()
    {
        var (env, path) = OpenEnv();
        try
        {
            using (var setup = env.BeginTransaction(readOnly: false))
            {
                var db = setup.OpenDatabase("records", DatabaseFlags.Create);
                setup.Put(db, "seed"u8, "seed-value"u8);
                setup.Commit();
            }

            using (var parent = env.BeginTransaction(readOnly: false))
            {
                using (var child = parent.BeginChild())
                {
                    var db = child.OpenDatabase("records");
                    child.Put(db, "from-child"u8, "child-value"u8);
                    child.Commit();
                }
                parent.Commit();
            }

            using (var read = env.BeginTransaction(readOnly: true))
            {
                var db = read.OpenDatabase("records");
                Assert.Equal("seed-value", Encoding.UTF8.GetString(read.Get(db, "seed"u8)));
                Assert.Equal("child-value", Encoding.UTF8.GetString(read.Get(db, "from-child"u8)));
            }
            var report = LmdbIntegrityChecker.Check(path);
            Assert.True(report.Clean, report.Render());
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }

    // S1-10b: writes made through a PARENT-opened handle inside the child must
    // roll back with the child — previously they mutated the parent's record
    // in place, and the parent then committed a root whose pages were never
    // flushed.
    [Fact]
    public void Child_abort_rolls_back_writes_through_parent_opened_handle()
    {
        var (env, path) = OpenEnv();
        try
        {
            using (var setup = env.BeginTransaction(readOnly: false))
            {
                var db = setup.OpenDatabase("records", DatabaseFlags.Create);
                setup.Put(db, "stable"u8, "stable-value"u8);
                setup.Commit();
            }

            using (var parent = env.BeginTransaction(readOnly: false))
            {
                var handle = parent.OpenDatabase("records");
                parent.Put(handle, "parent-key"u8, "parent-value"u8);
                using (var child = parent.BeginChild())
                {
                    child.Put(handle, "child-key"u8, "child-value"u8);
                    child.Abort();
                }
                parent.Commit();
            }

            using (var read = env.BeginTransaction(readOnly: true))
            {
                var db = read.OpenDatabase("records");
                Assert.Equal("stable-value", Encoding.UTF8.GetString(read.Get(db, "stable"u8)));
                Assert.Equal("parent-value", Encoding.UTF8.GetString(read.Get(db, "parent-key"u8)));
                Assert.False(read.TryGet(db, "child-key"u8, out _),
                    "aborted child's write leaked into the parent commit");
            }
            var report = LmdbIntegrityChecker.Check(path);
            Assert.True(report.Clean, report.Render());
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }

    // Child sees the parent's uncommitted named-DB state and layers on top.
    [Fact]
    public void Child_sees_parent_uncommitted_named_state_and_merges()
    {
        var (env, path) = OpenEnv();
        try
        {
            using (var parent = env.BeginTransaction(readOnly: false))
            {
                var db = parent.OpenDatabase("records", DatabaseFlags.Create);
                parent.Put(db, "p"u8, "1"u8);
                using (var child = parent.BeginChild())
                {
                    var cdb = child.OpenDatabase("records");
                    Assert.True(child.TryGet(cdb, "p"u8, out var pv),
                        "child cannot see parent's uncommitted named-DB write");
                    Assert.Equal("1", Encoding.UTF8.GetString(pv));
                    child.Put(cdb, "c"u8, "2"u8);
                    child.Commit();
                }
                parent.Commit();
            }
            using (var read = env.BeginTransaction(readOnly: true))
            {
                var db = read.OpenDatabase("records");
                Assert.Equal("1", Encoding.UTF8.GetString(read.Get(db, "p"u8)));
                Assert.Equal("2", Encoding.UTF8.GetString(read.Get(db, "c"u8)));
            }
            var report = LmdbIntegrityChecker.Check(path);
            Assert.True(report.Clean, report.Render());
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }

    // S2-13: the parent must reject operations while a child is active (LMDB
    // returns BAD_TXN) instead of silently racing the child for pages.
    [Fact]
    public void Parent_operations_rejected_while_child_active()
    {
        var (env, path) = OpenEnv();
        try
        {
            using var parent = env.BeginTransaction(readOnly: false);
            using var child = parent.BeginChild();
            var ex = Assert.Throws<LmdbException>(() =>
                parent.Put(parent.OpenDefaultDatabase(), "k"u8, "v"u8));
            Assert.Equal(LmdbErr.BadTxn, ex.ErrorCode);
            child.Abort();
            // After the child ends, the parent works again.
            parent.Put(parent.OpenDefaultDatabase(), "k"u8, "v"u8);
            parent.Commit();
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }
}
