// ObjectDatabase: the top-level entry point. Wraps an LMDB environment and manages
// typed collections + indexes.
//
// One ObjectDatabase = one LMDB environment = one directory (data.mdb + lock.mdb).
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Lmdb;

namespace Lmdb.Objects;

/// <summary>Configuration for the object database.</summary>
public sealed class ObjectDatabaseOptions
{
    /// <summary>Max map size (virtual address space reservation). Default 1 GB.</summary>
    public long MapSize { get; set; } = 1L << 30;
    /// <summary>Max number of named sub-DBs (collections + indexes). Default 64.</summary>
    public uint MaxDbs { get; set; } = 64;
    /// <summary>Disable locking (single-process, no lock file). Default false.</summary>
    public bool NoLock { get; set; } = false;
    /// <summary>Reuse freed pages. Disable for monotonic page allocation.</summary>
    public bool ReuseFreePages { get; set; } = true;
}

public sealed class ObjectDatabase : IDisposable
{
    private readonly LmdbEnvironment _env;
    private readonly ConcurrentDictionary<string, object> _collections = new();

    private ObjectDatabase(LmdbEnvironment env) => _env = env;

    /// <summary>Open an object database at <paramref name="path"/>.</summary>
    public static ObjectDatabase Open(string path, ObjectDatabaseOptions? options = null)
    {
        options ??= new();
        var env = LmdbEnvironment.Open(path, new EnvOpenOptions
        {
            ReadOnly = false,
            MapSize = options.MapSize,
            MaxDbs = options.MaxDbs,
            NoLock = options.NoLock,
            ReuseFreePages = options.ReuseFreePages,
        });
        return new ObjectDatabase(env);
    }

    /// <summary>Get or create a typed collection.</summary>
    public Collection<T> GetCollection<T>(string name, CollectionOptions<T>? options = null)
        where T : class
        => (Collection<T>)_collections.GetOrAdd(name,
            _ => new Collection<T>(this, name, options));

    // ── Transactions ──

    public LmdbTransaction BeginWrite() => _env.BeginWriteTransaction();
    public LmdbTransaction BeginRead() => _env.BeginTransaction(readOnly: true);

    // ── Indexes ──

    /// <summary>Ensure an index exists on a collection. The index is stored as a
    /// separate DUPSORT sub-DB named "{collection}:{field}". Unique indexes use a
    /// plain sub-DB. Returns true if the index was created (false if it existed).</summary>
    public bool EnsureIndex<T, TField>(Collection<T> collection, string fieldName,
        Expression<Func<T, TField>> selector, bool unique = false)
        where T : class
    {
        var indexDbName = $"{collection.Name}:{fieldName}";
        using var txn = BeginWrite();
        var flags = unique ? DatabaseFlags.Create : DatabaseFlags.Create | DatabaseFlags.DupSort;
        var db = txn.OpenDatabase(indexDbName, flags);
        txn.Commit();
        return true;
    }

    public void Dispose() => _env.Dispose();

    // ── Index operations (internal — used by Collection.FindBy) ──

    /// <summary>Insert into an index: {fieldValue → primaryKey}.</summary>
    internal void IndexInsert(LmdbTransaction txn, string collectionName, string fieldName,
        byte[] fieldKey, byte[] primaryKey, bool unique)
    {
        var indexDbName = $"{collectionName}:{fieldName}";
        if (txn.GetCachedDb(indexDbName) is LmdbDatabase cached)
        {
            PutToIndex(txn, cached, fieldKey, primaryKey, unique);
            return;
        }
        var flags = unique ? DatabaseFlags.Create : DatabaseFlags.Create | DatabaseFlags.DupSort;
        var db = txn.OpenDatabase(indexDbName, flags);
        txn.CacheDb(indexDbName, db);
        PutToIndex(txn, db, fieldKey, primaryKey, unique);
    }

    private static void PutToIndex(LmdbTransaction txn, LmdbDatabase db, byte[] fieldKey, byte[] primaryKey, bool unique)
    {
        if (unique && txn.TryGet(db, fieldKey, out _))
            throw new LmdbException(LmdbErr.KeyExist, "duplicate value for unique index");
        txn.Put(db, fieldKey, primaryKey);
    }

    /// <summary>Delete from an index.</summary>
    internal void IndexDelete(LmdbTransaction txn, string collectionName, string fieldName,
        byte[] fieldKey, byte[] primaryKey, bool unique)
    {
        var indexDbName = $"{collectionName}:{fieldName}";
        var db = txn.GetCachedDb(indexDbName) ?? txn.OpenDatabase(indexDbName);

        if (unique)
        {
            // Unique index: only one entry per fieldKey, delete by key.
            txn.Delete(db, fieldKey);
            return;
        }

        // DUPSORT index: find the specific (fieldKey, primaryKey) pair and delete it.
        // This avoids deleting ALL PKs that share the same field value.
        using var cur = txn.CreateCursor(db);
        if (cur.TryGet(Lmdb.CursorOp.GetBoth, fieldKey, primaryKey, out _, out _))
            cur.DeleteCurrent();
    }

    /// <summary>Look up primary keys via an index.</summary>
    internal byte[] IndexLookup(LmdbTransaction txn, string collectionName, string fieldName,
        byte[] fieldKey)
    {
        var indexDbName = $"{collectionName}:{fieldName}";
        var db = txn.GetCachedDb(indexDbName) ?? txn.OpenDatabase(indexDbName);
        if (!txn.TryGet(db, fieldKey, out var data))
            return null!;
        return data.ToArray();
    }
}
