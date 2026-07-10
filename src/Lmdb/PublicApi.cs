// C#-idiomatic API layer: convenience methods that make the library feel native
// to .NET developers, layered on top of the zero-alloc low-level API.
//
// What this adds:
//   - BeginWriteTransaction()  — unambiguous write-txn factory (no bool param)
//   - String overloads         — Put/Get/TryGet with string keys/values
//   - IEnumerable Scan          — foreach-friendly iteration + LINQ support
//   - Transaction.Write(Action) — scope-based commit (auto-commit on success)
//
// The low-level Span/byte* API remains unchanged — these are pure additions.
using System.Text;

namespace Lmdb;

public sealed unsafe partial class LmdbEnvironment
{
    /// <summary>Begin a read-write transaction. Prefer this over
    /// <c>BeginTransaction(false)</c> for clarity at the call site.</summary>
    public LmdbTransaction BeginWriteTransaction()
        => BeginTransaction(readOnly: false);

    // ── Scope-based commit (auto-commit on success, abort on exception) ──

    /// <summary>Execute a write action in a transaction, committing on success and
    /// aborting on exception. The transaction is passed to the action; do not call
    /// Commit/Abort manually.</summary>
    /// <example>
    /// <code>
    /// env.Write(txn =>
    /// {
    ///     var db = txn.OpenDefaultDatabase();
    ///     txn.Put(db, keyBytes, valueBytes);
    /// });  // commits here
    /// </code>
    /// </example>
    public void Write(Action<LmdbTransaction> action)
    {
        using var txn = BeginWriteTransaction();
        action(txn);
        txn.Commit();
    }

    /// <summary>Execute a read action in a read transaction.</summary>
    public void Read(Action<LmdbTransaction> action)
    {
        using var txn = BeginTransaction(readOnly: true);
        action(txn);
    }
}

public sealed unsafe partial class LmdbTransaction
{
    // ── String overloads (convenience — allocates via UTF-8 encoding) ──

    /// <summary>Store a string key/value pair (UTF-8 encoded).</summary>
    public void PutString(LmdbDatabase db, string key, string value, PutFlags flags = 0)
        => Put(db, Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value), flags);

    /// <summary>Look up a string key; returns the value decoded as UTF-8, or null if absent.</summary>
    public string? GetString(LmdbDatabase db, string key)
        => TryGet(db, Encoding.UTF8.GetBytes(key), out var data)
               ? Encoding.UTF8.GetString(data) : null;

    /// <summary>Try to look up a string key; returns the value decoded as UTF-8.</summary>
    public bool TryGetString(LmdbDatabase db, string key, out string? value)
    {
        if (TryGet(db, Encoding.UTF8.GetBytes(key), out var data))
        { value = Encoding.UTF8.GetString(data); return true; }
        value = null; return false;
    }

    /// <summary>Delete a string key.</summary>
    public bool Delete(LmdbDatabase db, string key)
        => Delete(db, Encoding.UTF8.GetBytes(key));

    // ── IEnumerable Scan (LINQ-friendly; allocates per item) ──

    /// <summary>Enumerate all key/value pairs in forward order. Each pair's key and
    /// data are copied to managed arrays (allocation per item). For zero-alloc
    /// iteration, use <see cref="CreateCursor"/> with <see cref="LmdbCursor.TryGet"/>
    /// and read the returned <see cref="ReadOnlySpan{Byte}"/> directly.</summary>
    public IEnumerable<(byte[] Key, byte[] Data)> Scan(LmdbDatabase db)
    {
        using var cur = CreateCursor(db);
        if (!cur.TryGet(CursorOp.First, default, out var k, out var v))
            yield break;
        do { yield return (k.ToArray(), v.ToArray()); }
        while (cur.TryGet(CursorOp.Next, default, out k, out v));
    }

    /// <summary>Enumerate all key/value pairs as decoded strings (UTF-8).</summary>
    public IEnumerable<(string Key, string Value)> ScanStrings(LmdbDatabase db)
    {
        foreach (var (k, v) in Scan(db))
            yield return (Encoding.UTF8.GetString(k), Encoding.UTF8.GetString(v));
    }

    // ── Scope-based commit moved to LmdbEnvironment (needs BeginTransaction) ──
}
