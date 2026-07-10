using Lmdb.Objects;

namespace TodoApi;

/// <summary>Service layer: wraps the ObjectDatabase and provides Todo-specific operations.</summary>
public class TodoService : IDisposable
{
    private readonly ObjectDatabase _db;
    private readonly Collection<Todo> _todos;

    public TodoService(string path)
    {
        _db = ObjectDatabase.Open(path, new ObjectDatabaseOptions { MapSize = 64L << 20, MaxDbs = 16 });
        _todos = _db.GetCollection<Todo>("todos");

        // Register indexes (declared once at startup).
        _todos.RegisterIndex("Completed", x => x.Completed);
        _todos.RegisterIndex("Priority", x => (long)x.Priority);
        _todos.RegisterIndex("DueDate", x => x.DueDate);
        _todos.RegisterIndex("Category", x => x.Category);

        // Create the index sub-DBs.
        _db.EnsureIndex(_todos, "Completed", x => x.Completed);
        _db.EnsureIndex(_todos, "Priority", x => (long)x.Priority);
        _db.EnsureIndex(_todos, "DueDate", x => x.DueDate);
        _db.EnsureIndex(_todos, "Category", x => x.Category);
    }

    // ── CRUD ──

    public long CreateTodo(string title, int priority, DateTime dueDate, string category)
    {
        using var txn = _db.BeginWrite();
        var todo = new Todo
        {
            Title = title,
            Priority = priority,
            DueDate = dueDate,
            Category = category,
            CreatedAt = DateTime.UtcNow,
        };
        _todos.Insert(txn, todo);
        txn.Commit();
        return todo.Id;
    }

    public Todo? GetTodo(long id)
    {
        using var txn = _db.BeginRead();
        return _todos.Get(txn, id);
    }

    public bool UpdateTodo(Todo todo)
    {
        using var txn = _db.BeginWrite();
        var existing = _todos.Get(txn, todo.Id);
        if (existing == null) return false;
        _todos.Update(txn, todo);
        txn.Commit();
        return true;
    }

    public bool DeleteTodo(long id)
    {
        using var txn = _db.BeginWrite();
        bool deleted = _todos.Delete(txn, id);
        txn.Commit();
        return deleted;
    }

    public bool MarkComplete(long id)
    {
        using var txn = _db.BeginWrite();
        var todo = _todos.Get(txn, id);
        if (todo == null) return false;
        todo.Completed = true;
        _todos.Update(txn, todo);
        txn.Commit();
        return true;
    }

    // ── Queries ──

    public List<Todo> GetAll(int limit = 100)
    {
        using var txn = _db.BeginRead();
        return _todos.Query(txn).Take(limit).ToList();
    }

    public List<Todo> GetByCategory(string category)
    {
        using var txn = _db.BeginRead();
        return _todos.FindAllBy(txn, "Category", category).ToList();
    }

    public List<Todo> GetPending()
    {
        using var txn = _db.BeginRead();
        return _todos.FindAllBy(txn, "Completed", false).ToList();
    }

    public List<Todo> GetByPriorityRange(int from, int to)
    {
        using var txn = _db.BeginRead();
        return _todos.FindByRange(txn, "Priority", (long)from, (long)to).ToList();
    }

    public List<Todo> GetDueSoon(DateTime upTo)
    {
        using var txn = _db.BeginRead();
        return _todos.FindByRange(txn, "DueDate", DateTime.MinValue, upTo).ToList();
    }

    public List<Todo> GetHighPriority()
    {
        using var txn = _db.BeginRead();
        // LINQ: uses index scan on Priority >= 3
        return _todos.Query(txn)
            .Where(x => x.Priority >= 3)
            .ToList();
    }

    public List<Todo> Search(string titlePrefix)
    {
        // Title isn't indexed — use full scan with in-memory filtering instead.
        using var txn = _db.BeginRead();
        return _todos.Scan(txn).Where(t => t.Title.StartsWith(titlePrefix)).ToList();
    }

    public long Count()
    {
        using var txn = _db.BeginRead();
        return _todos.Count(txn);
    }

    public void Dispose() => _db.Dispose();
}
