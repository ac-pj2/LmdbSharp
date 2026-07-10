using Lmdb;
using Lmdb.Objects;
using MemoryPack;
using Xunit;
using Xunit.Abstractions;

namespace Lmdb.Objects.Tests;

[MemoryPackable]
public partial class User
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public int Age { get; set; }
}

[MemoryPackable]
public partial class Product
{
    [LmdbKey]
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}

public class ObjectDatabaseTests
{
    private readonly ITestOutputHelper _out;
    public ObjectDatabaseTests(ITestOutputHelper out_) => _out = out_;

    private static string TmpDir(string name)
    {
        string dir = $"/tmp/lmdb-odb/{name}";
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void InsertAndGetById_AutoLong()
    {
        using var db = ObjectDatabase.Open(TmpDir("auto_long"));
        var users = db.GetCollection<User>("users");

        var id = users.Insert(new User { Name = "Alice", Email = "alice@x.com", Age = 30 });
        Assert.Equal(1L, id);

        var retrieved = users.Get(1L)!;
        Assert.Equal("Alice", retrieved.Name);
        Assert.Equal("alice@x.com", retrieved.Email);
        Assert.Equal(30, retrieved.Age);
    }

    [Fact]
    public void InsertBatch_Transactional()
    {
        using var db = ObjectDatabase.Open(TmpDir("batch"));
        var users = db.GetCollection<User>("users");

        // Batch insert in one transaction (much faster than per-op)
        using (var txn = db.BeginWrite())
        {
            for (int i = 0; i < 1000; i++)
                users.Insert(txn, new User { Name = $"User{i}", Email = $"u{i}@x.com", Age = 20 + i % 50 });
            txn.Commit();
        }

        Assert.Equal(1000, users.Count(db.BeginRead()));
    }

    [Fact]
    public void UpdateAndDelete()
    {
        using var db = ObjectDatabase.Open(TmpDir("crud"));
        var users = db.GetCollection<User>("users");

        var id = users.Insert(new User { Name = "Alice", Email = "alice@x.com", Age = 30 });

        users.Update(new User { Id = (long)id, Name = "Alice Smith", Email = "alice@x.com", Age = 31 });
        var updated = users.Get(id)!;
        Assert.Equal("Alice Smith", updated.Name);
        Assert.Equal(31, updated.Age);

        Assert.True(users.Delete(id));
        Assert.Null(users.Get(id));
        Assert.False(users.Delete(id));  // already deleted
    }

    [Fact]
    public void StringKey_Collection()
    {
        using var db = ObjectDatabase.Open(TmpDir("string_key"));
        var products = db.GetCollection<Product>("products");

        products.Insert(new Product { Sku = "WIDGET-001", Name = "Widget", Price = 9.99m });
        products.Insert(new Product { Sku = "GADGET-002", Name = "Gadget", Price = 19.99m });

        var widget = products.Get("WIDGET-001")!;
        Assert.Equal("Widget", widget.Name);
        Assert.Equal(9.99m, widget.Price);

        var gadget = products.Get("GADGET-002")!;
        Assert.Equal("Gadget", gadget.Name);
    }

    [Fact]
    public void Scan_AllObjects()
    {
        using var db = ObjectDatabase.Open(TmpDir("scan"));
        var users = db.GetCollection<User>("users");

        using (var txn = db.BeginWrite())
        {
            for (int i = 0; i < 100; i++)
                users.Insert(txn, new User { Name = $"U{i:000}", Email = $"u{i}@x.com", Age = i });
            txn.Commit();
        }

        using var txn2 = db.BeginRead();
        var all = users.Scan(txn2).ToList();
        Assert.Equal(100, all.Count);
        Assert.Equal("U000", all[0].Name);
        Assert.Equal("U099", all[99].Name);
    }

    [Fact]
    public void Index_FindByEmail()
    {
        using var db = ObjectDatabase.Open(TmpDir("index"));
        var users = db.GetCollection<User>("users");

        // Create a unique index on Email
        users.RegisterIndex("Email", x => x.Email, unique: true);
        db.EnsureIndex(users, "Email", x => x.Email, unique: true);

        using (var txn = db.BeginWrite())
        {
            users.Insert(txn, new User { Name = "Alice", Email = "alice@x.com", Age = 30 });
            users.Insert(txn, new User { Name = "Bob", Email = "bob@x.com", Age = 25 });
            txn.Commit();
        }

        using var txn2 = db.BeginRead();
        var alice = users.FindBy(txn2, "Email", "alice@x.com");
        Assert.NotNull(alice);
        Assert.Equal("Alice", alice!.Name);
        Assert.Equal(30, alice.Age);

        var bob = users.FindBy(txn2, "Email", "bob@x.com");
        Assert.NotNull(bob);
        Assert.Equal("Bob", bob!.Name);

        Assert.Null(users.FindBy(txn2, "Email", "nobody@x.com"));
    }

    [Fact]
    public void JsonSerializer_Option()
    {
        using var db = ObjectDatabase.Open(TmpDir("json"));
        var users = db.GetCollection<User>("users",
            new CollectionOptions<User> { Serializer = JsonObjectSerializer<User>.Instance });

        users.Insert(new User { Name = "Alice", Email = "alice@x.com", Age = 30 });
        var retrieved = users.Get(1L)!;
        Assert.Equal("Alice", retrieved.Name);
        Assert.Equal("alice@x.com", retrieved.Email);
        Assert.Equal(30, retrieved.Age);
    }

    [Fact]
    public void WebAppSimulation_PerRequestTransactions()
    {
        // Simulates a web app: many requests, each with its own read/write txn.
        using var db = ObjectDatabase.Open(TmpDir("webapp"));
        var users = db.GetCollection<User>("users");

        // Seed data
        using (var txn = db.BeginWrite())
        {
            for (int i = 0; i < 100; i++)
                users.Insert(txn, new User { Name = $"User{i}", Email = $"u{i}@x.com", Age = 20 + i });
            txn.Commit();
        }

        // Simulate 100 "requests" doing a read + occasional write
        for (int req = 0; req < 100; req++)
        {
            using var txn = db.BeginRead();
            var user = users.Get(txn, (long)(req % 100 + 1));
            Assert.NotNull(user);
        }

        Assert.Equal(100, users.Count(db.BeginRead()));
    }
}
