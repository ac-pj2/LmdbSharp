// Async wrapper for web applications. LMDB operations are inherently synchronous
// (memory-mapped I/O), but this wrapper provides Task-based async APIs that run
// on a background thread. Write operations are serialized via SemaphoreSlim.
using System.Threading;

namespace Lmdb.Objects;

public sealed class AsyncObjectDatabase : IDisposable
{
    private readonly ObjectDatabase _db;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public AsyncObjectDatabase(ObjectDatabase db) => _db = db;

    public static AsyncObjectDatabase Open(string path, ObjectDatabaseOptions? options = null)
        => new(ObjectDatabase.Open(path, options));

    public Collection<T> GetCollection<T>(string name, CollectionOptions<T>? options = null) where T : class
        => _db.GetCollection(name, options);

    // Read operations (concurrent, no lock)

    public Task<T?> GetAsync<T>(Collection<T> collection, object key) where T : class
    {
        using var txn = _db.BeginRead();
        return Task.FromResult(collection.Get(txn, key));
    }

    public Task<List<T>> GetAllAsync<T>(Collection<T> collection) where T : class
    {
        using var txn = _db.BeginRead();
        return Task.FromResult(collection.Scan(txn).ToList());
    }

    public Task<T?> FindByAsync<T>(Collection<T> collection, string fieldName, object value) where T : class
    {
        using var txn = _db.BeginRead();
        return Task.FromResult(collection.FindBy(txn, fieldName, value));
    }

    // Write operations (serialized via write lock)

    public async Task<object> InsertAsync<T>(Collection<T> collection, T obj) where T : class
    {
        await _writeLock.WaitAsync();
        try
        {
            using var txn = _db.BeginWrite();
            var key = collection.Insert(txn, obj);
            txn.Commit();
            return key;
        }
        finally { _writeLock.Release(); }
    }

    public async Task UpdateAsync<T>(Collection<T> collection, T obj) where T : class
    {
        await _writeLock.WaitAsync();
        try
        {
            using var txn = _db.BeginWrite();
            collection.Update(txn, obj);
            txn.Commit();
        }
        finally { _writeLock.Release(); }
    }

    public async Task<bool> DeleteAsync<T>(Collection<T> collection, object key) where T : class
    {
        await _writeLock.WaitAsync();
        try
        {
            using var txn = _db.BeginWrite();
            bool deleted = collection.Delete(txn, key);
            txn.Commit();
            return deleted;
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>Execute a batch of write operations in a single transaction.
    /// All writes are committed atomically.</summary>
    public async Task WriteBatchAsync(Action<Lmdb.LmdbTransaction> action)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var txn = _db.BeginWrite();
            action(txn);
            txn.Commit();
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>Execute a write action in a transaction with a return value.</summary>
    public async Task<TResult> WriteBatchAsync<TResult>(Func<Lmdb.LmdbTransaction, TResult> action)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var txn = _db.BeginWrite();
            var result = action(txn);
            txn.Commit();
            return result;
        }
        finally { _writeLock.Release(); }
    }

    public void Dispose()
    {
        _db.Dispose();
        _writeLock.Dispose();
    }
}
