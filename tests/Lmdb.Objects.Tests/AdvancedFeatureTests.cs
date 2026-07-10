using Lmdb;
using Lmdb.Objects;
using MemoryPack;
using Xunit;
using Xunit.Abstractions;

namespace Lmdb.Objects.Tests;

[MemoryPackable]
public partial class Article
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Category { get; set; } = "";
    public int Score { get; set; }
    public bool Published { get; set; }
}

public class AdvancedFeatureTests
{
    private readonly ITestOutputHelper _out;
    public AdvancedFeatureTests(ITestOutputHelper out_) => _out = out_;

    private static string TmpDir(string name)
    {
        string dir = $"/tmp/lmdb-odb-adv/{name}";
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    private static ObjectDatabase OpenWithArticles(string dir, out Collection<Article> articles)
    {
        var db = ObjectDatabase.Open(dir);
        articles = db.GetCollection<Article>("articles");
        articles.RegisterIndex("Category", x => x.Category, unique: false);
        articles.RegisterIndex("Score", x => (long)x.Score, unique: false);
        articles.RegisterIndex("Published", x => x.Published, unique: false);
        db.EnsureIndex(articles, "Category", x => x.Category);
        db.EnsureIndex(articles, "Score", x => (long)x.Score);
        db.EnsureIndex(articles, "Published", x => x.Published);
        return db;
    }

    private static void SeedArticles(Collection<Article> articles, ObjectDatabase db)
    {
        using var txn = db.BeginWrite();
        articles.Insert(txn, new Article { Title = "A1", Category = "tech", Score = 80, Published = true });
        articles.Insert(txn, new Article { Title = "A2", Category = "tech", Score = 90, Published = true });
        articles.Insert(txn, new Article { Title = "A3", Category = "sports", Score = 50, Published = false });
        articles.Insert(txn, new Article { Title = "A4", Category = "tech", Score = 70, Published = true });
        articles.Insert(txn, new Article { Title = "B1", Category = "food", Score = 85, Published = true });
        txn.Commit();
    }

    // ── Range queries ──

    [Fact]
    public void FindByRange_ReturnsArticlesInScoreRange()
    {
        using var db = OpenWithArticles(TmpDir("range"), out var articles);
        SeedArticles(articles, db);

        using var txn = db.BeginRead();
        var mid = articles.FindByRange(txn, "Score", 70L, 90L).ToList();
        // 70, 80, 85, 90 (4 articles with scores >= 70 and < 90... actually 90 is inclusive in our test)
        // FindByRange is [from, to) — so 70, 80, 85 (not 90)
        Assert.True(mid.Count >= 3);
        Assert.All(mid, a => Assert.True(a.Score >= 70 && a.Score < 90));
    }

    [Fact]
    public void FindByPrefix_ReturnsMatchingTitles()
    {
        using var db = OpenWithArticles(TmpDir("prefix"), out var articles);
        articles.RegisterIndex("Title", x => x.Title, unique: false);
        db.EnsureIndex(articles, "Title", x => x.Title);
        SeedArticles(articles, db);

        using var txn = db.BeginRead();
        var aArticles = articles.FindByPrefix(txn, "Title", "A").ToList();
        Assert.Equal(4, aArticles.Count);
        Assert.All(aArticles, a => Assert.StartsWith("A", a.Title));
    }

    // ── LINQ query provider ──

    [Fact]
    public void Linq_EqualityUsesIndex()
    {
        using var db = OpenWithArticles(TmpDir("linq_eq"), out var articles);
        SeedArticles(articles, db);

        using var txn = db.BeginRead();
        var techArticles = articles.Query(txn)
            .Where(x => x.Category == "tech")
            .ToList();
        Assert.Equal(3, techArticles.Count);
        Assert.All(techArticles, a => Assert.Equal("tech", a.Category));
    }

    [Fact]
    public void Linq_RangeUsesIndex()
    {
        using var db = OpenWithArticles(TmpDir("linq_range"), out var articles);
        SeedArticles(articles, db);

        using var txn = db.BeginRead();
        var highScore = articles.Query(txn)
            .Where(x => x.Score >= 80)
            .ToList();
        Assert.True(highScore.Count >= 2);
        Assert.All(highScore, a => Assert.True(a.Score >= 80));
    }

    [Fact]
    public void Linq_CompoundRange()
    {
        using var db = OpenWithArticles(TmpDir("linq_compound"), out var articles);
        SeedArticles(articles, db);

        using var txn = db.BeginRead();
        var mid = articles.Query(txn)
            .Where(x => x.Score >= 70 && x.Score < 90)
            .ToList();
        Assert.All(mid, a => Assert.True(a.Score >= 70 && a.Score < 90));
    }

    [Fact]
    public void Linq_TakeAndSkip()
    {
        using var db = OpenWithArticles(TmpDir("linq_skip"), out var articles);
        SeedArticles(articles, db);

        using var txn = db.BeginRead();
        var first3 = articles.Query(txn).Take(3).ToList();
        Assert.Equal(3, first3.Count);

        var skipped = articles.Query(txn).Skip(2).ToList();
        Assert.Equal(3, skipped.Count);
    }

    [Fact]
    public void Linq_FallbackFullScan()
    {
        using var db = OpenWithArticles(TmpDir("linq_fallback"), out var articles);
        SeedArticles(articles, db);

        using var txn = db.BeginRead();
        // Complex predicate not supported by index → full scan fallback
        var published = articles.Query(txn)
            .Where(x => x.Published && x.Title.Contains('A'))
            .ToList();
        Assert.All(published, a => Assert.True(a.Published));
    }

    // ── OrderBy ──

    [Fact]
    public void Linq_OrderBy_UsesIndexScan()
    {
        using var db = OpenWithArticles(TmpDir("orderby"), out var articles);
        SeedArticles(articles, db);

        using var txn = db.BeginRead();
        var ordered = articles.Query(txn)
            .OrderBy(x => x.Score)
            .ToList();
        for (int i = 1; i < ordered.Count; i++)
            Assert.True(ordered[i - 1].Score <= ordered[i].Score);
    }

    [Fact]
    public void Linq_OrderByDescending_Take()
    {
        using var db = OpenWithArticles(TmpDir("orderby_desc"), out var articles);
        SeedArticles(articles, db);

        using var txn = db.BeginRead();
        var top2 = articles.Query(txn)
            .OrderByDescending(x => x.Score)
            .Take(2)
            .ToList();
        Assert.Equal(2, top2.Count);
        Assert.True(top2[0].Score >= top2[1].Score);
    }

    // ── Batch index maintenance ──

    [Fact]
    public void BatchInsert_DeferredIndexMaintenance()
    {
        using var db = OpenWithArticles(TmpDir("batch"), out var articles);
        articles.BeginBatch();

        using (var txn = db.BeginWrite())
        {
            for (int i = 0; i < 100; i++)
                articles.Insert(txn, new Article { Title = $"T{i}", Category = "tech", Score = i, Published = true });
            // Flush deferred index updates before commit
            articles.FlushPendingIndexes(txn);
            txn.Commit();
        }

        using var txn2 = db.BeginRead();
        var techCount = articles.FindAllBy(txn2, "Category", "tech").Count();
        Assert.Equal(100, techCount);
    }

    // ── Schema versioning ──

    [Fact]
    public void SchemaVersioning_ReadsOldVersion()
    {
        // Simulate: write a V1 record, then read it with a V2 serializer that has a migrator.
        using var db = ObjectDatabase.Open(TmpDir("schema"));

        // Write with V1 (JSON serializer, version tag = 1)
        var v1Serializer = new VersionedSerializer<Article>(1, JsonObjectSerializer<Article>.Instance);
        var articlesV1 = db.GetCollection<Article>("articles", new CollectionOptions<Article> { Serializer = v1Serializer });

        using (var txn = db.BeginWrite())
        {
            articlesV1.Insert(txn, new Article { Title = "Hello", Category = "test", Score = 42, Published = true });
            txn.Commit();
        }

        // Read with V2 (version tag = 2) + migrator from V1
        var migrators = new Dictionary<byte, Func<byte[], Article>>
        {
            [1] = data => System.Text.Json.JsonSerializer.Deserialize<Article>(data)!
        };
        var v2Serializer = new VersionedSerializer<Article>(2, JsonObjectSerializer<Article>.Instance, migrators);
        var articlesV2 = db.GetCollection<Article>("articles_v2", new CollectionOptions<Article> { Serializer = v2Serializer });

        // Can't read from a different collection name — need to read the same data.
        // In practice the collection name stays the same; here we verify the serializer works.
        using var txn2 = db.BeginRead();
        var raw = db.GetCollection<Article>("articles",
            new CollectionOptions<Article> { Serializer = v2Serializer }).Get(txn2, 1L);
        Assert.NotNull(raw);
        Assert.Equal("Hello", raw!.Title);
        Assert.Equal(42, raw.Score);
    }

    // ── Async wrapper ──

    [Fact]
    public async Task Async_InsertAndGet()
    {
        using var db = AsyncObjectDatabase.Open(TmpDir("async"));
        var users = db.GetCollection<Article>("articles");

        await db.InsertAsync(users, new Article { Title = "Async1", Category = "test", Score = 10, Published = true });
        await db.InsertAsync(users, new Article { Title = "Async2", Category = "test", Score = 20, Published = true });

        var article = await db.GetAsync(users, 1L);
        Assert.NotNull(article);
        Assert.Equal("Async1", article!.Title);
    }

    [Fact]
    public async Task Async_WriteBatch()
    {
        using var db = AsyncObjectDatabase.Open(TmpDir("async_batch"));
        var users = db.GetCollection<Article>("articles");

        await db.WriteBatchAsync(txn =>
        {
            for (int i = 0; i < 10; i++)
                users.Insert(txn, new Article { Title = $"B{i}", Category = "batch", Score = i, Published = true });
        });

        var all = await db.GetAllAsync(users);
        Assert.Equal(10, all.Count);
    }
}
