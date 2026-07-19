using Lmdb;
using Lmdb.Objects;
using Xunit;

namespace Lmdb.Objects.Tests;

// mdb_cursor_count and its Collection surface: counted index lookups must be
// O(1) reads of the dup sub-tree's entry count, exact across sub-page and
// sub-DB dup storage, maintenance, and deletes — plus the named empty-key
// error that used to surface as a bare BadValsize from deep in a backfill.
public class IndexCountTests
{
    private static string TmpDir(string name)
    {
        string dir = $"/tmp/lmdb-odb-count/{name}";
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    private static ObjectDatabase Open(string dir, out Collection<Article> articles)
    {
        var db = ObjectDatabase.Open(dir);
        articles = db.GetCollection<Article>("articles");
        articles.RegisterIndex("Category", x => x.Category, unique: false);
        db.EnsureIndex(articles, "Category", x => x.Category);
        return db;
    }

    [Fact]
    public void CountBy_CountsWithoutFetching_AcrossSubPageAndSubDbStorage()
    {
        using var db = Open(TmpDir("count-scales"), out var articles);
        using (var txn = db.BeginWrite())
        {
            // Few dups: inline sub-page storage. Many dups: spills to a
            // sub-DB with its own B+tree. Both must count exactly.
            for (int i = 0; i < 5; i++)
                articles.Insert(txn, new Article { Title = $"S{i}", Category = "small" });
            for (int i = 0; i < 3000; i++)
                articles.Insert(txn, new Article { Title = $"L{i}", Category = "large" });
            txn.Commit();
        }
        using var read = db.BeginRead();
        Assert.Equal(5, articles.CountBy(read, "Category", "small"));
        Assert.Equal(3000, articles.CountBy(read, "Category", "large"));
        Assert.Equal(0, articles.CountBy(read, "Category", "absent"));
        // Missing index on a live collection is a configuration error, same
        // as the other index reads.
        Assert.Throws<LmdbException>(() => articles.CountBy(read, "NoSuchIndex", "x"));
    }

    [Fact]
    public void CountBy_TracksUpdatesAndDeletes()
    {
        using var db = Open(TmpDir("count-maintains"), out var articles);
        var ids = new List<long>();
        using (var txn = db.BeginWrite())
        {
            for (int i = 0; i < 10; i++)
            {
                var article = new Article { Title = $"A{i}", Category = "tech" };
                articles.Insert(txn, article);
                ids.Add(article.Id);
            }
            txn.Commit();
        }
        using (var txn = db.BeginWrite())
        {
            articles.Update(txn, new Article { Id = ids[0], Title = "A0", Category = "food" });
            articles.Delete(txn, ids[1]);
            txn.Commit();
        }
        using var read = db.BeginRead();
        Assert.Equal(8, articles.CountBy(read, "Category", "tech"));
        Assert.Equal(1, articles.CountBy(read, "Category", "food"));
    }

    [Fact]
    public void CountDuplicates_IsOneForPlainDatabases()
    {
        var dir = TmpDir("count-plain");
        using var db = ObjectDatabase.Open(dir);
        var articles = db.GetCollection<Article>("articles");
        using (var txn = db.BeginWrite())
        {
            articles.Insert(txn, new Article { Title = "only", Category = "tech" });
            txn.Commit();
        }
        using var read = db.BeginRead();
        var handle = read.OpenDatabase("articles");
        using var cur = read.CreateCursor(handle);
        Assert.True(cur.TryGet(CursorOp.First, default, out _, out _));
        Assert.Equal(1ul, cur.CountDuplicates());
    }

    [Fact]
    public void EmptyIndexKey_FailsWithTheFieldNamed()
    {
        using var db = Open(TmpDir("count-empty"), out var articles);
        using var txn = db.BeginWrite();
        var error = Assert.Throws<LmdbException>(() =>
            articles.Insert(txn, new Article { Title = "bad", Category = "" }));
        Assert.Contains("articles:Category", error.Message);
        Assert.Contains("empty key", error.Message);
    }
}
