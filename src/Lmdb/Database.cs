// Database (DBI handle): wraps an MDB_db record pointer + the comparators chosen
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

public sealed unsafe class Database
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

    internal Database(LmdbEnvironment env, uint dbi)
    {
        Env = env;
        Dbi = dbi;
    }

    /// <summary>Open a core DB (FREE_DBI or MAIN_DBI) whose MDB_db is inline in the snapshot meta.</summary>
    internal static Database OpenCore(LmdbEnvironment env, uint dbi)
    {
        var db = new Database(env, dbi);
        db.DbRec = Meta.DbPtr(env.MetaPtr, dbi);
        db.DbFlags = Db.PersistentFlags(db.DbRec);
        db.KeyCmp = Compare.PickKey(db.DbFlags);
        db.DupCmp = Compare.PickDup(db.DbFlags);
        return db;
    }

    /// <summary>Open a named sub-database by looking its name up in the main DB tree
    /// (mdb_dbi_open read path). The named DB's MDB_db record is stored as the data
    /// of an F_SUBDATA leaf node. Read-only: does not create.</summary>
    internal static Database OpenNamed(Transaction txn, string name, DatabaseFlags flags)
    {
        var main = OpenCore(txn.Env, Const.MAIN_DBI);
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        using var c = new Cursor(txn, main);
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

        var db = new Database(txn.Env, txn.Env.AllocDbi()) { DbRec = dbRec };
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
