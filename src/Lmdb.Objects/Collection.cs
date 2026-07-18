// Collection<T>: a typed collection of objects stored in a named LMDB sub-DB.
//
// Key encoding is determined by the key property:
//   - [LmdbKey] on a long   → KeyType.Long (INTEGERKEY sub-DB)
//   - [LmdbKey] on a string → KeyType.String
//   - [LmdbKey] on a Guid   → KeyType.Guid
//   - No [LmdbKey]          → KeyType.AutoLong (auto-incrementing, needs Id/id property)
//
// The ID counter for AutoLong is stored in the main DB at key "counter:{name}".
using System.Buffers;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Lmdb;

namespace Lmdb.Objects;

/// <summary>Configuration for a collection.</summary>
public sealed class CollectionOptions<T>
{
    /// <summary>The serializer to use. Defaults to MemoryPack (requires [MemoryPackable] on T).
    /// Set to JsonObjectSerializer<T>.Instance for schemaless JSON.</summary>
    public IObjectSerializer<T> Serializer { get; set; } =
        MemoryPackObjectSerializer<T>.Instance;
}

/// <summary>A typed collection of objects, backed by a named LMDB sub-DB.</summary>
public sealed class Collection<T> where T : class
{
    private readonly ObjectDatabase _db;
    internal readonly string Name;
    internal readonly KeyType KeyType;
    private readonly PropertyInfo _keyProp;
    private readonly IObjectSerializer<T> _serializer;
    // Registered indexes: fieldName → (getter, isUnique)
    private readonly Dictionary<string, (Func<T, object> getter, bool unique)> _indexes = new();

    internal Collection(ObjectDatabase db, string name, CollectionOptions<T>? options)
    {
        _db = db;
        Name = name;
        options ??= new();
        _serializer = options.Serializer;

        // Determine key type from [LmdbKey] or default to AutoLong.
        var keyAttr = typeof(T).GetProperties()
            .FirstOrDefault(p => p.GetCustomAttribute<LmdbKeyAttribute>() != null);

        if (keyAttr != null)
        {
            _keyProp = keyAttr;
            KeyType = keyAttr.PropertyType switch
            {
                _ when keyAttr.PropertyType == typeof(long) => KeyType.Long,
                _ when keyAttr.PropertyType == typeof(string) => KeyType.String,
                _ when keyAttr.PropertyType == typeof(Guid) => KeyType.Guid,
                _ when keyAttr.PropertyType == typeof(DateTime) => KeyType.DateTime,
                _ => throw new NotSupportedException(
                    $"[LmdbKey] property type {keyAttr.PropertyType} not supported"),
            };
        }
        else
        {
            // Auto-long: find an Id property to assign to.
            _keyProp = typeof(T).GetProperties()
                .FirstOrDefault(p => string.Equals(p.Name, "Id", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Type {typeof(T).Name} has no [LmdbKey] and no Id/id property for auto-IDs");
            if (_keyProp.PropertyType != typeof(long))
                throw new InvalidOperationException(
                    $"Auto-ID property {_keyProp.Name} must be of type long");
            KeyType = KeyType.AutoLong;
        }
    }

    // ── ID helpers ──

    internal object GetKey(T obj) => _keyProp.GetValue(obj)!;

    internal void SetKey(T obj, object key) => _keyProp.SetValue(obj, key);

    /// <summary>Generate the next auto-increment ID for this collection.
    /// Only valid for KeyType.AutoLong.</summary>
    private long NextId(LmdbTransaction txn)
    {
        if (KeyType != KeyType.AutoLong)
            throw new InvalidOperationException("NextId is only for AutoLong collections");

        var mainDb = txn.OpenDefaultDatabase();
        byte[] counterKey = Encoding.UTF8.GetBytes("counter:" + Name);
        long next;
        // Use the main DB for the counter.
        if (txn.TryGet(mainDb, counterKey, out var data) && data.Length >= 8)
            next = KeyEncoding.DecodeLong(data) + 1;
        else
            next = 1;

        txn.Put(mainDb, counterKey, KeyEncoding.EncodeLong(next));
        return next;
    }

    /// <summary>The underlying ObjectDatabase (for creating transactions).</summary>
    public ObjectDatabase Database => _db;

    // ── CRUD: explicit transaction overloads ──

    /// <summary>Insert an object. For AutoLong collections, assigns the Id property.
    /// If <see cref="BeginBatch"/> was called, defers index maintenance.</summary>
    public object Insert(LmdbTransaction txn, T obj)
    {
        object key;
        if (KeyType == KeyType.AutoLong)
        {
            key = NextId(txn);
            SetKey(obj, key);
        }
        else
        {
            key = GetKey(obj);
        }

        // Unique-index availability is validated BEFORE any mutation, so a
        // conflict surfaces while the transaction is still pristine.
        EnsureUniqueIndexesAvailable(txn, obj, key);

        // Inserting over an existing key is an upsert: the old record's index
        // entries must go, or stale entries point lookups at replaced data.
        var existing = Get(txn, key);
        if (existing != null) MaintainIndexesOnDelete(txn, existing);

        // Serialize to a pooled buffer.
        var db = OpenCollectionDb(txn);
        using var buf = new PooledBuffer();
        _serializer.Serialize(obj, buf);
        txn.Put(db, KeyEncoding.Encode(key, KeyType), buf.WrittenSpan);
        MaintainIndexesOnPut(txn, obj);
        return key;
    }

    /// <summary>Throw KeyExist if any unique index already maps one of the
    /// object's field values to a DIFFERENT primary key. Runs before any
    /// mutation so the transaction stays clean.</summary>
    private void EnsureUniqueIndexesAvailable(LmdbTransaction txn, T obj, object key)
    {
        if (_indexes.Count == 0) return;
        byte[] pk = KeyEncoding.Encode(key, KeyType);
        foreach (var (fieldName, (getter, unique)) in _indexes)
        {
            if (!unique) continue;
            object? value = getter(obj);
            if (value == null) continue;
            byte[]? existingPk = _db.IndexLookup(txn, Name, fieldName, EncodeIndexValue(value));
            if (existingPk != null && !existingPk.AsSpan().SequenceEqual(pk))
                throw new LmdbException(LmdbErr.KeyExist,
                    $"unique index '{Name}:{fieldName}' already maps this value to another record");
        }
    }

    /// <summary>Get an object by key. Returns null if not found (including when
    /// the collection has never been written).</summary>
    public T? Get(LmdbTransaction txn, object key)
    {
        if (!TryOpenCollectionDb(txn, out var db)) return null;
        if (!txn.TryGet(db!, KeyEncoding.Encode(key, KeyType), out var data))
            return null;
        return _serializer.Deserialize(data);
    }

    /// <summary>Update an existing object (overwrites by key).</summary>
    public void Update(LmdbTransaction txn, T obj)
    {
        // Remove old index entries, then add new ones.
        var old = Get(txn, GetKey(obj));
        if (old != null) MaintainIndexesOnDelete(txn, old);

        object key = GetKey(obj);
        var db = OpenCollectionDb(txn);
        using var buf = new PooledBuffer();
        _serializer.Serialize(obj, buf);
        txn.Put(db, KeyEncoding.Encode(key, KeyType), buf.WrittenSpan);
        MaintainIndexesOnPut(txn, obj);
    }

    /// <summary>Delete by key. Returns true if the key existed.</summary>
    public bool Delete(LmdbTransaction txn, object key)
    {
        var db = OpenCollectionDb(txn);
        // Load before deleting to maintain indexes.
        var obj = Get(txn, key);
        bool deleted = txn.Delete(db, KeyEncoding.Encode(key, KeyType));
        if (deleted && obj != null) MaintainIndexesOnDelete(txn, obj);
        return deleted;
    }

    /// <summary>Count the number of entries in this collection. Returns 0 if the
    /// collection doesn't exist yet (read txn can't create it).</summary>
    public long Count(LmdbTransaction txn)
        => TryOpenCollectionDb(txn, out var db) ? (long)db!.Entries : 0;

    /// <summary>Enumerate all objects (allocates per item). Returns empty if the
    /// collection doesn't exist yet.</summary>
    public IEnumerable<T> Scan(LmdbTransaction txn)
    {
        if (!TryOpenCollectionDb(txn, out var db)) yield break;
        foreach (var (_, data) in txn.Scan(db!))
            yield return _serializer.Deserialize(data);
    }

    // ── Batch read (single cursor sweep for multiple keys) ──

    /// <summary>Get multiple objects by key. Missing keys yield null at their
    /// position. Keys may be in any order and may repeat. (The previous
    /// single-sweep implementation compared keys lexicographically against
    /// integer-ordered cursors and reported live keys as missing.)</summary>
    public List<T?> GetBatch(LmdbTransaction txn, IReadOnlyList<object> keys)
    {
        var results = new List<T?>(keys.Count);
        if (keys.Count == 0) return results;
        if (!TryOpenCollectionDb(txn, out var db))
        {
            for (int i = 0; i < keys.Count; i++) results.Add(null);
            return results;
        }
        foreach (var key in keys)
        {
            results.Add(txn.TryGet(db!, KeyEncoding.Encode(key, KeyType), out var data)
                ? _serializer.Deserialize(data) : null);
        }
        return results;
    }

    // ── CRUD: auto-transaction overloads (convenience, one op per txn) ──

    public object Insert(T obj)
    {
        using var txn = _db.BeginWrite();
        var key = Insert(txn, obj);
        txn.Commit();
        return key;
    }

    public T? Get(object key)
    {
        using var txn = _db.BeginRead();
        return Get(txn, key);
    }

    public void Update(T obj)
    {
        using var txn = _db.BeginWrite();
        Update(txn, obj);
        txn.Commit();
    }

    public bool Delete(object key)
    {
        using var txn = _db.BeginWrite();
        bool deleted = Delete(txn, key);
        txn.Commit();
        return deleted;
    }

    // ── Index-based lookups ──

    /// <summary>Find a single object by an indexed field value. For unique indexes,
    /// returns the one match; for non-unique, returns the first match.</summary>
    public T? FindBy(LmdbTransaction txn, string fieldName, object fieldValue)
    {
        byte[] fieldKey = EncodeIndexValue(fieldValue);
        byte[] pkBytes = _db.IndexLookup(txn, Name, fieldName, fieldKey);
        if (pkBytes == null) return null;
        object pk = KeyEncoding.Decode(pkBytes, KeyType);
        return Get(txn, pk);
    }

    /// <summary>True when maintenance is registered for this field.</summary>
    internal bool HasIndex(string fieldName) => _indexes.ContainsKey(fieldName);

    /// <summary>Count objects matching an indexed field value without fetching
    /// any of them: position on the index key and read the duplicate count —
    /// O(log n), which is what pagination totals need.</summary>
    public long CountBy(LmdbTransaction txn, string fieldName, object fieldValue)
    {
        if (!TryOpenIndexDb(txn, fieldName, out var indexDb)) return 0;
        byte[] fieldKey = EncodeIndexValue(fieldValue);
        using var cur = txn.CreateCursor(indexDb!);
        if (!cur.TryGet(Lmdb.CursorOp.Set, fieldKey, out _, out _)) return 0;
        return (long)cur.CountDuplicates();
    }

    /// <summary>Enumerate all objects matching an indexed field value (for non-unique indexes).</summary>
    public IEnumerable<T> FindAllBy(LmdbTransaction txn, string fieldName, object fieldValue)
    {
        if (!TryOpenIndexDb(txn, fieldName, out var indexDb)) yield break;
        byte[] fieldKey = EncodeIndexValue(fieldValue);
        using var cur = txn.CreateCursor(indexDb!);
        if (!cur.TryGet(Lmdb.CursorOp.Set, fieldKey, out _, out var pkData))
            yield break;
        do
        {
            object pk = KeyEncoding.Decode(pkData, KeyType);
            var obj = Get(txn, pk);
            if (obj != null) yield return obj;
        }
        while (cur.TryGet(Lmdb.CursorOp.NextDup, default, out _, out pkData));
    }

    /// <summary>Enumerate all objects whose indexed field value falls within [from, to).
    /// Uses a cursor range scan on the index sub-DB.</summary>
    public IEnumerable<T> FindByRange(LmdbTransaction txn, string fieldName,
        object? from, object? to)
    {
        if (!TryOpenIndexDb(txn, fieldName, out var indexDb)) yield break;
        using var cur = txn.CreateCursor(indexDb!);

        // Position at 'from' (or first if from is null), iterate until 'to'.
        byte[]? fromKey = from != null ? EncodeIndexValue(from) : null;
        byte[]? toKey = to != null ? EncodeIndexValue(to) : null;
        var cmp = new SpanByteComparer();

        CursorOp startOp = from != null ? CursorOp.SetRange : CursorOp.First;
        if (!cur.TryGet(startOp, fromKey ?? default, out var curKey, out var pkData))
            yield break;

        do
        {
            // Copy the key to byte[] for safe use across yield.
            var curKeyCopy = curKey.ToArray();
            // Stop if past the end of the range.
            if (toKey != null && cmp.Compare(curKeyCopy, toKey) >= 0)
                yield break;
            object pk = KeyEncoding.Decode(pkData, KeyType);
            var obj = Get(txn, pk);
            if (obj != null) yield return obj;
        }
        while (cur.TryGet(Lmdb.CursorOp.Next, default, out curKey, out pkData));
    }

    /// <summary>Enumerate all objects whose indexed field starts with the given prefix
    /// (string indexes only).</summary>
    public IEnumerable<T> FindByPrefix(LmdbTransaction txn, string fieldName, string prefix)
    {
        if (!TryOpenIndexDb(txn, fieldName, out var indexDb)) yield break;
        byte[] prefixBytes = Encoding.UTF8.GetBytes(prefix);
        using var cur = txn.CreateCursor(indexDb!);

        if (!cur.TryGet(Lmdb.CursorOp.SetRange, prefixBytes, out var curKey, out var pkData))
            yield break;

        do
        {
            // Stop when the key no longer starts with the prefix.
            if (!curKey.StartsWith(prefixBytes))
                yield break;
            object pk = KeyEncoding.Decode(pkData, KeyType);
            var obj = Get(txn, pk);
            if (obj != null) yield return obj;
        }
        while (cur.TryGet(Lmdb.CursorOp.Next, default, out curKey, out pkData));
    }

    // ── Deferred batch index maintenance (retired) ──
    //
    // Deferral diverted index writes onto shared collection state: one
    // BeginBatch() left the collection deferring forever, ops from concurrent
    // transactions mixed in one list, and a flush could apply entries in a
    // DIFFERENT transaction than the records they index (including entries for
    // rolled-back records). Index maintenance is now always immediate and
    // transactional.

    /// <summary>Obsolete: index maintenance is always immediate; no-op.</summary>
    [Obsolete("Index maintenance is always immediate and transactional; batching is a no-op.")]
    public void BeginBatch() { }

    /// <summary>Obsolete: index maintenance is always immediate; no-op.</summary>
    [Obsolete("Index maintenance is always immediate and transactional; batching is a no-op.")]
    public void FlushPendingIndexes(LmdbTransaction txn) { }

    private static byte[] EncodeIndexValue(object value)
    {
        // Index sub-DBs compare keys LEXICOGRAPHICALLY, so every encoding here
        // must be order-preserving under byte comparison. Numerics use
        // big-endian sign-flipped form; unsupported types fail loudly instead
        // of falling back to culture-sensitive ToString (silent misses).
        if (value is string s) return Encoding.UTF8.GetBytes(s);
        if (value is long l) return KeyEncoding.EncodeOrderedLong(l);
        if (value is int i) return KeyEncoding.EncodeOrderedLong(i);
        if (value is DateTime dt) return KeyEncoding.EncodeOrderedLong(dt.Ticks);
        if (value is bool b) return new[] { b ? (byte)1 : (byte)0 };
        if (value is Guid g) return g.ToByteArray();
        throw new NotSupportedException(
            $"index value type {value.GetType().Name} has no order-preserving encoding");
    }

    /// <summary>Register an index on this collection so that Insert/Update/Delete
    /// maintain it automatically. Call once at startup (not per request).</summary>
    public void RegisterIndex<TField>(string fieldName, Expression<Func<T, TField>> selector, bool unique = false)
    {
        var compiled = selector.Compile();
        _indexes[fieldName] = (obj => compiled(obj)!, unique);
    }

    /// <summary>Write index entries for every existing row of one registered
    /// field (EnsureIndex backfill). Idempotent: existing pairs re-put.</summary>
    internal void BackfillIndex(LmdbTransaction txn, string fieldName)
    {
        if (!_indexes.TryGetValue(fieldName, out var idx)) return;
        if (!TryOpenCollectionDb(txn, out var db)) return;
        foreach (var (_, data) in txn.Scan(db!))
        {
            var obj = _serializer.Deserialize(data);
            object? value = idx.getter(obj);
            if (value == null) continue;
            byte[] pk = KeyEncoding.Encode(GetKey(obj), KeyType);
            _db.IndexInsert(txn, Name, fieldName, IndexKey(fieldName, value), pk, idx.unique);
        }
    }

    /// <summary>Maintain indexes after an insert or update.</summary>
    private void MaintainIndexesOnPut(LmdbTransaction txn, T obj)
    {
        if (_indexes.Count == 0) return;
        byte[] pk = KeyEncoding.Encode(GetKey(obj), KeyType);
        foreach (var (fieldName, (getter, unique)) in _indexes)
        {
            object? value = getter(obj);
            if (value == null) continue;   // null field values are not indexed
            byte[] fieldKey = IndexKey(fieldName, value);
            _db.IndexInsert(txn, Name, fieldName, fieldKey, pk, unique);
        }
    }

    /// <summary>Encode an index key and reject empty ones with the field named —
    /// LMDB cannot store an empty key, and the raw BadValsize from deep inside a
    /// backfill names nothing. Selectors with legitimately absent values should
    /// return null (skipped) or prefix a discriminator.</summary>
    private byte[] IndexKey(string fieldName, object value)
    {
        byte[] key = EncodeIndexValue(value);
        if (key.Length == 0)
            throw new LmdbException(LmdbErr.BadValsize,
                $"index '{Name}:{fieldName}' selector produced an empty key; " +
                "return null to skip the record or lead the key with a discriminator");
        return key;
    }

    /// <summary>Maintain indexes before a delete (remove index entries).</summary>
    private void MaintainIndexesOnDelete(LmdbTransaction txn, T obj)
    {
        if (_indexes.Count == 0) return;
        byte[] pk = KeyEncoding.Encode(GetKey(obj), KeyType);
        foreach (var (fieldName, (getter, unique)) in _indexes)
        {
            object? value = getter(obj);
            if (value == null) continue;
            byte[] fieldKey = IndexKey(fieldName, value);
            _db.IndexDelete(txn, Name, fieldName, fieldKey, pk, unique);
        }
    }

    /// <summary>Open or create the LMDB sub-DB for this collection. Caches the handle
    /// on the transaction to avoid repeated lookups.</summary>
    /// <summary>Open the index sub-DB for a field. Missing index + missing
    /// collection = empty result (cold start). Missing index on a LIVE
    /// collection is a configuration error and throws NotFound — the LINQ
    /// layer catches it and falls back to a full scan.</summary>
    private bool TryOpenIndexDb(LmdbTransaction txn, string fieldName, out LmdbDatabase? indexDb)
    {
        var indexDbName = $"{Name}:{fieldName}";
        try
        {
            indexDb = txn.GetCachedDb(indexDbName) ?? txn.OpenDatabase(indexDbName);
            return true;
        }
        catch (LmdbException e) when (e.ErrorCode == LmdbErr.NotFound)
        {
            if (!TryOpenCollectionDb(txn, out _)) { indexDb = null; return false; }
            throw;   // collection has data but the index does not exist
        }
    }

    /// <summary>Open the collection sub-DB if it exists; false when it was never
    /// created (read txns cannot create) so callers return empty results.</summary>
    internal bool TryOpenCollectionDb(LmdbTransaction txn, out LmdbDatabase? db)
    {
        try { db = OpenCollectionDb(txn); return true; }
        catch (LmdbException e) when (e.ErrorCode == LmdbErr.NotFound) { db = null; return false; }
    }

    internal LmdbDatabase OpenCollectionDb(LmdbTransaction txn)
    {
        // Cache the Database handle per-transaction to avoid reopening (which would
        // create multiple MDB_db record copies, only the last of which commits).
        if (txn.GetCachedDb(Name) is LmdbDatabase cached)
            return cached;
        var db = txn.OpenDatabase(Name, DatabaseFlags.Create | KeyEncoding.ToDbFlags(KeyType));
        txn.CacheDb(Name, db);
        return db;
    }
}

/// <summary>A reusable write buffer backed by a pooled array. Avoids allocating a
/// new MemoryStream + byte[] on every serialize.</summary>
internal sealed class PooledBuffer : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(256);
    private int _written;

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    public void Advance(int count) => _written += count;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    private void EnsureCapacity(int sizeHint)
    {
        int needed = _written + Math.Max(sizeHint, 1);
        if (needed <= _buffer.Length) return;
        int newSize = _buffer.Length;
        while (newSize < needed) newSize *= 2;
        var newBuf = System.Buffers.ArrayPool<byte>.Shared.Rent(newSize);
        _buffer.AsSpan(0, _written).CopyTo(newBuf);
        System.Buffers.ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = newBuf;
    }

    public void Dispose()
    {
        if (_buffer != null)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null!;
        }
    }
}

/// <summary>Lexicographic byte comparison for ReadOnlySpan&lt;byte&gt;.</summary>
internal sealed class SpanByteComparer
{
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int len = a.Length < b.Length ? a.Length : b.Length;
        int cmp = a[..len].SequenceCompareTo(b[..len]);
        return cmp != 0 ? cmp : a.Length - b.Length;
    }
}
