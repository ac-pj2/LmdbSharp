// LmdbDatabase (DBI handle): wraps an MDB_db record pointer + the comparators chosen
// from its persistent flags. For the core DBs (free-DB=0, main-DB=1) the record
// lives inline in the chosen meta page; named sub-DBs are stored as F_SUBDATA
// nodes in the main DB and resolved later (mdb_dbi_open).
namespace Lmdb;

[Flags]
public enum DatabaseFlags : uint
{
    None        = 0,
    ReverseKey  = Const.MDB_REVERSEKEY,
    DupSort     = Const.MDB_DUPSORT,
    IntegerKey  = Const.MDB_INTEGERKEY,
    DupFixed    = Const.MDB_DUPFIXED,
    IntegerDup  = Const.MDB_INTEGERDUP,
    ReverseDup  = Const.MDB_REVERSEDUP,
    Create      = Const.MDB_CREATE,
}

public sealed unsafe class LmdbDatabase
{
    internal readonly LmdbEnvironment Env;
    internal readonly uint Dbi;
    internal byte* DbRec;        // points at the MDB_db record (meta-inline for core DBs)
    internal CmpPtr KeyCmp = null!;
    internal CmpPtr? DupCmp;
    internal ushort DbFlags;
    internal bool InWriteTxn;    // true when DbRec points at a txn's mutable copy

    public DatabaseFlags Flags => (DatabaseFlags)DbFlags;
    public ulong Root => Db.Root(DbRec);
    public ulong Entries => Db.Entries(DbRec);
    public ushort Depth => Db.Depth(DbRec);
    public ulong BranchPages => Db.BranchPages(DbRec);
    public ulong LeafPages => Db.LeafPages(DbRec);
    public ulong OverflowPages => Db.OverflowPages(DbRec);

    internal LmdbDatabase(LmdbEnvironment env, uint dbi)
    {
        Env = env;
        Dbi = dbi;
    }

    /// <summary>Open a core DB (FREE_DBI or MAIN_DBI) whose MDB_db is inline in the snapshot meta.</summary>
    internal static LmdbDatabase OpenCore(LmdbEnvironment env, uint dbi)
        => OpenCore(env, dbi, env.MetaPtr);

    internal static LmdbDatabase OpenCore(LmdbEnvironment env, uint dbi, byte* metaPtr)
        => OpenCoreFromRecord(env, dbi, Meta.DbPtr(metaPtr, dbi));

    /// <summary>Open a core DB handle over an MDB_db record the caller owns
    /// (read txns copy the records out of the meta page at begin — the meta
    /// page itself is recycled by the writer two commits later).</summary>
    internal static LmdbDatabase OpenCoreFromRecord(LmdbEnvironment env, uint dbi, byte* dbRec)
    {
        var db = new LmdbDatabase(env, dbi);
        db.DbRec = dbRec;
        db.DbFlags = Db.PersistentFlags(db.DbRec);
        db.KeyCmp = Compare.PickKey(db.DbFlags);
        db.DupCmp = Compare.PickDup(db.DbFlags);
        return db;
    }

    /// <summary>Open a named sub-database by looking its name up in the main DB tree
    /// (mdb_dbi_open read path). The named DB's MDB_db record is stored as the data
    /// of an F_SUBDATA leaf node. Read-only: does not create. The lookup MUST run
    /// against the transaction's snapshot, not the environment's newest meta.</summary>
    internal static LmdbDatabase OpenNamed(LmdbTransaction txn, string name, DatabaseFlags flags)
    {
        var main = txn.OpenDefaultDatabase();
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        using var c = new LmdbCursor(txn, main);
        if (!c.TryGet(CursorOp.Set, nameBytes, out _, out _))
            throw new LmdbException(LmdbErr.NotFound, $"database '{name}' does not exist");

        byte* node = c.CurrentLeafNode;
        ushort nf = Node.Flags(node);
        if ((nf & Const.F_SUBDATA) == 0)
            throw new LmdbException(LmdbErr.Incompatible, $"'{name}' is not a sub-database");
        if (Node.Dsz(node) < Db.Size48)
            throw new LmdbException(LmdbErr.Corrupted, $"'{name}' DB record too small");

        byte* dbRec;
        if ((nf & Const.F_BIGDATA) != 0)
        {
            // MDB_db record on an overflow page (unusual; record is only 48 bytes).
            ulong pgno = ReadU64(Node.Data(node));
            dbRec = txn.Env.Page(pgno) + Const.PAGEHDRSZ;
        }
        else
        {
            dbRec = Node.Data(node);
        }

        var db = new LmdbDatabase(txn.Env, txn.Env.AllocDbi()) { DbRec = dbRec };
        db.DbFlags = Db.PersistentFlags(dbRec);
        db.KeyCmp = Compare.PickKey(db.DbFlags);
        db.DupCmp = Compare.PickDup(db.DbFlags);
        return db;
    }

    private static ulong ReadU64(byte* p)
        => (ulong)p[0] | ((ulong)p[1] << 8) | ((ulong)p[2] << 16) | ((ulong)p[3] << 24)
         | ((ulong)p[4] << 32) | ((ulong)p[5] << 40) | ((ulong)p[6] << 48) | ((ulong)p[7] << 56);

    public override string ToString()
        => Dbi == Const.MAIN_DBI ? "main" : Dbi == Const.FREE_DBI ? "free" : $"dbi{Dbi}";
}
