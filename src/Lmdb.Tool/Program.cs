// Lmdb.Tool — a small mdb_stat-style CLI over the C# LMDB port.
//   dotnet run --project src/Lmdb.Tool -- <env-path> [--count] [--list N]
using System.Text;
using Lmdb;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: lmdbtool <env-path> [--count] [--list N] [--get key]");
    return 1;
}

string path = args[0];
bool count = Array.IndexOf(args, "--count") >= 0;
bool list = Array.IndexOf(args, "--list") >= 0;
int listN = 0;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--list" && int.TryParse(args[i + 1], out listN)) list = true;
string? getKey = null;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--get") getKey = args[i + 1];

try
{
    using var env = LmdbEnvironment.Open(path);
    using var txn = env.BeginTransaction();
    var db = txn.OpenDefaultDatabase();

    var info = env.Info;
    Console.WriteLine($"LMDB environment: {path}");
    Console.WriteLine($"  page size:    {info.PageSize}");
    Console.WriteLine($"  map size:     {info.MapSize:N0}");
    Console.WriteLine($"  last pgno:    {info.LastPgno}");
    Console.WriteLine($"  last txnid:   {info.LastTxnid}");
    Console.WriteLine();
    Console.WriteLine($"Main database:");
    Console.WriteLine($"  entries:      {db.Entries:N0}");
    Console.WriteLine($"  depth:        {db.Depth}");
    Console.WriteLine($"  branch pages: {db.BranchPages}");
    Console.WriteLine($"  leaf pages:   {db.LeafPages}");
    Console.WriteLine($"  overflow pgs: {db.OverflowPages}");
    Console.WriteLine($"  root pgno:    {db.Root}");
    Console.WriteLine($"  flags:        {db.Flags}");

    if (getKey != null)
    {
        if (txn.TryGet(db, Encoding.UTF8.GetBytes(getKey), out var val))
            Console.WriteLine($"\nget({getKey}) = {Encoding.UTF8.GetString(val)} ({val.Length} bytes)");
        else
            Console.WriteLine($"\nget({getKey}) = <not found>");
    }

    if (count || list)
    {
        using var cur = txn.CreateCursor(db);
        long n = 0;
        if (cur.TryGet(CursorOp.First, default, out var k, out var v))
        {
            do
            {
                if (list && n < listN)
                {
                    var keyStr = TryUtf8(k);
                    var valStr = v.Length <= 64 ? TryUtf8(v) : TryUtf8(v[..64]) + "...";
                    Console.WriteLine($"  [{n,4}] {keyStr} = {valStr}");
                }
                n++;
            } while (cur.TryGet(CursorOp.Next, default, out k, out v));
        }
        Console.WriteLine($"\niterated {n:N0} entries");
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}

static string TryUtf8(ReadOnlySpan<byte> b)
{
    try { return Encoding.UTF8.GetString(b); }
    catch { return Convert.ToHexString(b); }
}
