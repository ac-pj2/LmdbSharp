// Per-transaction DB handle cache. Avoids reopening the same named sub-DB multiple
// times within a transaction (which would create conflicting MDB_db record copies).
using System.Collections.Concurrent;

namespace Lmdb;

public sealed unsafe partial class LmdbTransaction
{
    private ConcurrentDictionary<string, LmdbDatabase>? _dbCache;

    /// <summary>Cache a named DB handle on this transaction.</summary>
    internal void CacheDb(string name, LmdbDatabase db)
    {
        _dbCache ??= new ConcurrentDictionary<string, LmdbDatabase>();
        _dbCache.TryAdd(name, db);
    }

    /// <summary>Get a cached DB handle, or null if not cached.</summary>
    internal LmdbDatabase? GetCachedDb(string name)
        => _dbCache?.GetValueOrDefault(name);
}
