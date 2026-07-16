// Quick stopwatch benchmark harness driving the SAME workload through the C#
// engine and native liblmdb (P/Invoke), for regression guarding by ratio.
//
//   dotnet run -c Release --project tests/Lmdb.QuickBench -- cs      # C# engine
//   dotnet run -c Release --project tests/Lmdb.QuickBench -- native  # liblmdb.so.0
//
// Phases: seq-write, rnd-write, get-hit, get-miss, cursor-scan, overwrite,
// delete-half. Each phase prints a human line to stderr and a machine line to
// stdout:  RESULT <engine> <phase> <ops_per_sec>
// Reps: env REPS (default 3); the best rep per phase is reported.
// Keys: env COUNT (default 1_000_000).
using System.Diagnostics;
using System.Text;
using Lmdb;
using NativeLmdb;

string engine = args.Length > 0 ? args[0] : "cs";
int N = int.TryParse(Environment.GetEnvironmentVariable("COUNT"), out var n) ? n : 1_000_000;
int reps = int.TryParse(Environment.GetEnvironmentVariable("REPS"), out var r) ? r : 3;
string baseDir = $"/tmp/lmdb-quickbench-{engine}";
if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
Directory.CreateDirectory(baseDir);

var keys = new byte[N][];
var vals = new byte[N][];
for (int i = 0; i < N; i++)
{
    keys[i] = Encoding.UTF8.GetBytes($"key{i:00000000}");
    vals[i] = Encoding.UTF8.GetBytes($"value{i:00000000}_payload_data");
}
var rnd = new int[N];
for (int i = 0; i < N; i++) rnd[i] = i;
var rng = new Random(42);
for (int i = N - 1; i > 0; i--) { int j = rng.Next(i + 1); (rnd[i], rnd[j]) = (rnd[j], rnd[i]); }
var missKeys = new byte[N][];
for (int i = 0; i < N; i++) missKeys[i] = Encoding.UTF8.GetBytes($"kex{i:00000000}");

var best = new Dictionary<string, double>();
void Phase(string name, int ops, Action a)
{
    var sw = Stopwatch.StartNew();
    a();
    sw.Stop();
    double rate = ops / sw.Elapsed.TotalSeconds;
    Console.Error.WriteLine($"{name,-12} {sw.Elapsed.TotalMilliseconds,8:F1} ms  {rate,14:N0} ops/s");
    if (!best.TryGetValue(name, out var b) || rate > b) best[name] = rate;
}

IBenchEngine eng = engine switch
{
    "cs" => new CsEngine(),
    "native" => new NativeEngine(),
    _ => throw new ArgumentException($"unknown engine '{engine}' (use cs|native)"),
};

for (int rep = 0; rep < reps; rep++)
{
    Console.Error.WriteLine($"--- {engine} rep {rep + 1} ---");
    string dir = $"{baseDir}/r{rep}";

    eng.OpenNew($"{dir}-seq");
    eng.BeginWrite();
    Phase("seq-write", N, () => { for (int i = 0; i < N; i++) eng.Put(keys[i], vals[i]); });
    eng.Commit();
    eng.Close();

    eng.OpenNew($"{dir}-rnd");
    eng.BeginWrite();
    Phase("rnd-write", N, () => { for (int i = 0; i < N; i++) { int k = rnd[i]; eng.Put(keys[k], vals[k]); } });
    eng.Commit();
    eng.Close();

    eng.OpenExisting($"{dir}-seq");
    eng.BeginRead();
    Phase("get-hit", N, () => { for (int i = 0; i < N; i++) eng.Get(keys[rnd[i]]); });
    Phase("get-miss", N, () => { for (int i = 0; i < N; i++) eng.Get(missKeys[rnd[i]]); });
    int count = 0;
    Phase("cursor-scan", N, () => count = eng.Scan());
    if (count != N) throw new Exception($"scan count {count} != {N}");
    eng.AbortRead();
    eng.Close();

    eng.OpenExisting($"{dir}-seq");
    eng.BeginWrite();
    Phase("overwrite", N, () => { for (int i = 0; i < N; i++) { int k = rnd[i]; eng.Put(keys[k], vals[k]); } });
    eng.Commit();
    eng.Close();

    eng.OpenExisting($"{dir}-seq");
    eng.BeginWrite();
    Phase("delete-half", N / 2, () => { for (int i = 0; i < N / 2; i++) eng.Del(keys[rnd[i]]); });
    eng.Commit();
    eng.Close();
}

foreach (var (phase, rate) in best)
    Console.WriteLine($"RESULT {engine} {phase} {rate:F0}");

interface IBenchEngine
{
    void OpenNew(string dir);
    void OpenExisting(string dir);
    void BeginWrite();
    void BeginRead();
    void Put(byte[] key, byte[] val);
    bool Get(byte[] key);
    bool Del(byte[] key);
    int Scan();
    void Commit();
    void AbortRead();
    void Close();
}

sealed class CsEngine : IBenchEngine
{
    private LmdbEnvironment? _env;
    private LmdbTransaction? _txn;
    private LmdbDatabase? _db;
    private static EnvOpenOptions Opts() => new()
    { ReadOnly = false, MapSize = 2048L << 20, NoLock = true };

    public void OpenNew(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        _env = LmdbEnvironment.Open(dir, Opts());
    }
    public void OpenExisting(string dir) => _env = LmdbEnvironment.Open(dir, Opts());
    public void BeginWrite() { _txn = _env!.BeginTransaction(readOnly: false); _db = _txn.OpenDefaultDatabase(); }
    public void BeginRead() { _txn = _env!.BeginTransaction(readOnly: true); _db = _txn.OpenDefaultDatabase(); }
    public void Put(byte[] key, byte[] val) => _txn!.Put(_db!, key, val);
    public bool Get(byte[] key) => _txn!.TryGet(_db!, key, out _);
    public bool Del(byte[] key) => _txn!.Delete(_db!, key);
    public int Scan()
    {
        using var cur = _txn!.CreateCursor(_db!);
        int c = 0;
        if (cur.TryGet(CursorOp.First, default, out _, out _))
            do { c++; } while (cur.TryGet(CursorOp.Next, default, out _, out _));
        return c;
    }
    public void Commit() { _txn!.Commit(); _txn.Dispose(); _txn = null; }
    public void AbortRead() { _txn!.Dispose(); _txn = null; }
    public void Close() { _env!.Dispose(); _env = null; }
}

sealed unsafe class NativeEngine : IBenchEngine
{
    private NativeEnv? _env;
    private void* _txn;
    private uint _dbi;

    public void OpenNew(string dir) => _env = new NativeEnv(dir, 2048L << 20, create: true);
    public void OpenExisting(string dir) => _env = new NativeEnv(dir, 2048L << 20, create: false);
    public void BeginWrite()
    {
        Native.mdb_txn_begin(_env!.Ptr, null, 0, out _txn).Check();
        Native.mdb_dbi_open(_txn, null, 0, out _dbi).Check();
    }
    public void BeginRead()
    {
        Native.mdb_txn_begin(_env!.Ptr, null, 0x20000 /*MDB_RDONLY*/, out _txn).Check();
        Native.mdb_dbi_open(_txn, null, 0, out _dbi).Check();
    }
    public void Put(byte[] key, byte[] val)
    {
        fixed (byte* kp = key, vp = val)
        {
            var k = new Native.MDB_val { mv_size = (nuint)key.Length, mv_data = kp };
            var v = new Native.MDB_val { mv_size = (nuint)val.Length, mv_data = vp };
            Native.mdb_put(_txn, _dbi, &k, &v, 0).Check();
        }
    }
    public bool Get(byte[] key)
    {
        fixed (byte* kp = key)
        {
            var k = new Native.MDB_val { mv_size = (nuint)key.Length, mv_data = kp };
            Native.MDB_val v;
            return Native.mdb_get(_txn, _dbi, &k, &v) == 0;
        }
    }
    public bool Del(byte[] key)
    {
        fixed (byte* kp = key)
        {
            var k = new Native.MDB_val { mv_size = (nuint)key.Length, mv_data = kp };
            return Native.mdb_del(_txn, _dbi, &k, null) == 0;
        }
    }
    public int Scan()
    {
        Native.mdb_cursor_open(_txn, _dbi, out var cur).Check();
        Native.MDB_val k, v;
        int c = 0;
        if (Native.mdb_cursor_get(cur, &k, &v, Native.MDB_FIRST) == 0)
            do { c++; } while (Native.mdb_cursor_get(cur, &k, &v, Native.MDB_NEXT) == 0);
        Native.mdb_cursor_close(cur);
        return c;
    }
    public void Commit() { Native.mdb_txn_commit(_txn).Check(); _txn = null; }
    public void AbortRead() { Native.mdb_txn_abort(_txn); _txn = null; }
    public void Close() { _env!.Dispose(); _env = null; }
}
