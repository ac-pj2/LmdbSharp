// Transaction: a snapshot view of the environment.
//
// Read-only transactions observe the meta page selected at open time (the newest
// committed snapshot). They perform no locking and never block a writer. Write
// transactions (dirty page list, COW, commit) arrive with the write path.
namespace Lmdb;

public sealed unsafe class Transaction : IDisposable
{
    internal readonly LmdbEnvironment Env;
    internal readonly bool ReadOnly;
    internal ulong TxnId;
    internal ulong NextPgno;

    public LmdbEnvironment Environment => Env;
    public ulong Id => TxnId;

    internal Transaction(LmdbEnvironment env, bool readOnly)
    {
        Env = env;
        ReadOnly = readOnly;
        TxnId = env.TxnId;
        NextPgno = env.LastPg + 1;
    }

    /// <summary>Open the default (unnamed) database.</summary>
    public Database OpenDefaultDatabase() => Database.OpenCore(Env, Const.MAIN_DBI);

    /// <summary>Open a named sub-database. (Requires the DB to have been created with
    /// MDB_CREATE and max_dbs &gt; 0; resolved via the main DB tree.)</summary>
    public Database OpenDatabase(string name, DatabaseFlags flags = 0)
    {
        if (string.IsNullOrEmpty(name))
            return OpenDefaultDatabase();
        return Database.OpenNamed(this, name, flags);
    }

    /// <summary>Point lookup. Returns false (MDB_NOTFOUND) if the key is absent.</summary>
    public bool TryGet(Database db, ReadOnlySpan<byte> key, out ReadOnlySpan<byte> data)
    {
        using var cursor = new Cursor(this, db);
        return cursor.TryGet(CursorOp.Set, key, out _, out data);
    }

    /// <summary>Point lookup that throws on a missing key.</summary>
    public ReadOnlySpan<byte> Get(Database db, ReadOnlySpan<byte> key)
    {
        if (!TryGet(db, key, out var data))
            throw new LmdbException(LmdbErr.NotFound);
        return data;
    }

    public Cursor CreateCursor(Database db) => new(this, db);

    public void Commit()
    {
        if (!ReadOnly)
            throw new NotImplementedException("Write-path commit arrives in the next milestone.");
    }

    public void Abort() { /* read txn: nothing to roll back */ }

    public void Dispose()
    {
        if (!ReadOnly) Abort();   // write-path hook
        GC.SuppressFinalize(this);
    }
}
