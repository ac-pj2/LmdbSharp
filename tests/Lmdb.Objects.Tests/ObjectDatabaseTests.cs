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

    [Fact]
    public void IndexUpdate_MaintainsCorrectEntries()
    {
        // Regression test: updating an object must remove the OLD index entry and
        // add the NEW one. Previously IndexDelete deleted ALL entries for a fieldKey
        // in DUPSORT indexes, corrupting multi-value indexes.
        using var db = ObjectDatabase.Open(TmpDir("idx_update"));
        var users = db.GetCollection<User>("users");
        users.RegisterIndex("Age", x => (long)x.Age, unique: false);
        db.EnsureIndex(users, "Age", x => (long)x.Age);

        using (var txn = db.BeginWrite())
        {
            users.Insert(txn, new User { Name = "Alice", Email = "a@x.com", Age = 25 });
            users.Insert(txn, new User { Name = "Bob", Email = "b@x.com", Age = 25 });
            users.Insert(txn, new User { Name = "Carol", Email = "c@x.com", Age = 30 });
            txn.Commit();
        }

        // Verify 2 users with age 25.
        using (var txn = db.BeginRead())
        {
            var age25 = users.FindAllBy(txn, "Age", 25L).ToList();
            Assert.Equal(2, age25.Count);
            var age30 = users.FindAllBy(txn, "Age", 30L).ToList();
            Assert.Single(age30);
        }

        // Update Alice's age from 25 to 30.
        using (var txn = db.BeginWrite())
        {
            var alice = users.Get(txn, 1L)!;
            alice.Age = 30;
            users.Update(txn, alice);
            txn.Commit();
        }

        // Now age 25 should have 1 (Bob), age 30 should have 2 (Carol + Alice).
        using (var txn = db.BeginRead())
        {
            var age25 = users.FindAllBy(txn, "Age", 25L).ToList();
            Assert.Single(age25);
            Assert.Equal("Bob", age25[0].Name);

            var age30 = users.FindAllBy(txn, "Age", 30L).ToList();
            Assert.Equal(2, age30.Count);
            var names = age30.Select(u => u.Name).OrderBy(n => n).ToList();
            Assert.Equal("Alice", names[0]);
            Assert.Equal("Carol", names[1]);
        }
    }

    [Fact]
    public void IndexDelete_RemovesCorrectEntry()
    {
        // Deleting one user should only remove that user's index entry, not others
        // who share the same field value.
        using var db = ObjectDatabase.Open(TmpDir("idx_delete"));
        var users = db.GetCollection<User>("users");
        users.RegisterIndex("Age", x => (long)x.Age, unique: false);
        db.EnsureIndex(users, "Age", x => (long)x.Age);

        using (var txn = db.BeginWrite())
        {
            users.Insert(txn, new User { Name = "A", Email = "a@x.com", Age = 25 });
            users.Insert(txn, new User { Name = "B", Email = "b@x.com", Age = 25 });
            users.Insert(txn, new User { Name = "C", Email = "c@x.com", Age = 25 });
            txn.Commit();
        }

        // Delete user B (id=2).
        using (var txn = db.BeginWrite())
        {
            users.Delete(txn, 2L);
            txn.Commit();
        }

        // A and C should still be indexed under age 25.
        using (var txn = db.BeginRead())
        {
            var age25 = users.FindAllBy(txn, "Age", 25L).ToList();
            Assert.Equal(2, age25.Count);
        }
    }
}
