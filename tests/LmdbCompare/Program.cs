// Head-to-head benchmark: pure C# LMDB port vs native liblmdb (P/Invoke).
// Same workload, same data, same .NET runtime — isolates the engine difference.
//
// Run: dotnet run -c Release --project tests/LmdbCompare
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Configs;
using Lmdb;
using System.Text;
using NativeLmdb;

BenchmarkRunner.Run<LmdbCompare>(ManualConfig.Create(DefaultConfig.Instance));

[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median")]
public class LmdbCompare
{
    private const long MapSize = 64L << 20;
    private byte[][] _keys = null!;
    private byte[][] _vals = null!;

    // Pre-built environments for read benchmarks.
    private unsafe NativeEnv _nativeEnv = null!;
    private unsafe void* _nativeEnvPtr;
    private LmdbEnvironment _csEnv = null!;

    [Params(100000)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _keys = new byte[Count][];
        _vals = new byte[Count][];
        for (int i = 0; i < Count; i++)
        {
            _keys[i] = Encoding.UTF8.GetBytes($"key{i:00000000}");
            _vals[i] = Encoding.UTF8.GetBytes($"value{i:00000000}_payload_data");
        }

        // Pre-build identical databases for read benchmarks.
        string csDir = "/tmp/lmdb-compare/cs_read";
        string nativeDir = "/tmp/lmdb-compare/native_read";

        // C# DB
        if (System.IO.Directory.Exists(csDir)) System.IO.Directory.Delete(csDir, true);
        System.IO.Directory.CreateDirectory(csDir);
        using (var env = LmdbEnvironment.Open(csDir, new EnvOpenOptions { ReadOnly = false, MapSize = MapSize, NoLock = true }))
        using (var txn = env.BeginTransaction(false))
        {
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < Count; i++) txn.Put(db, _keys[i], _vals[i]);
            txn.Commit();
        }

        // Native DB
        using (var env = new NativeEnv(nativeDir, MapSize, create: true))
        {
            unsafe
            {
                void* txn;
                Native.mdb_txn_begin(env.Ptr, null, 0, out txn).Check();
                uint dbi;
                Native.mdb_dbi_open(txn, null, 0, out dbi).Check();
                for (int i = 0; i < Count; i++)
                {
                    var key = Native.Val(_keys[i]);
                    var val = Native.Val(_vals[i]);
                    Native.mdb_put(txn, dbi, &key, &val, 0).Check();
                }
                Native.mdb_txn_commit(txn).Check();
            }
        }

        // Open read environments (kept open for the duration of benchmarks).
        _nativeEnv = new NativeEnv(nativeDir, MapSize, create: false);
        unsafe { _nativeEnvPtr = _nativeEnv.Ptr; }
        _csEnv = LmdbEnvironment.Open(csDir);
    }

    private string FreshDir(string suffix)
    {
        string d = $"/tmp/lmdb-compare/{suffix}";
        if (System.IO.Directory.Exists(d)) System.IO.Directory.Delete(d, true);
        System.IO.Directory.CreateDirectory(d);
        return d;
    }

    // ── WRITE (bulk insert, single txn) ──

    [Benchmark(Description = "C# port")]
    [BenchmarkCategory("Write")]
    public void Write_CsPort()
    {
        string d = FreshDir("cs_write");
        using var env = LmdbEnvironment.Open(d, new EnvOpenOptions { ReadOnly = false, MapSize = MapSize, NoLock = true });
        using var txn = env.BeginTransaction(readOnly: false);
        var db = txn.OpenDefaultDatabase();
        for (int i = 0; i < Count; i++)
            txn.Put(db, _keys[i], _vals[i]);
        txn.Commit();
    }

    [Benchmark(Description = "Native liblmdb")]
    [BenchmarkCategory("Write")]
    public unsafe void Write_Native()
    {
        string d = FreshDir("native_write");
        using var env = new NativeEnv(d, MapSize, create: true);
        void* txn;
        Native.mdb_txn_begin(env.Ptr, null, 0, out txn).Check();
        uint dbi;
        Native.mdb_dbi_open(txn, null, 0, out dbi).Check();
        for (int i = 0; i < Count; i++)
        {
            var key = Native.Val(_keys[i]);
            var val = Native.Val(_vals[i]);
            Native.mdb_put(txn, dbi, &key, &val, 0).Check();
        }
        Native.mdb_txn_commit(txn).Check();
    }

    // ── POINT GET (sequential, read txn) ──

    [Benchmark(Description = "C# port")]
    [BenchmarkCategory("Point Get")]
    public void PointGet_CsPort()
    {
        using var txn = _csEnv.BeginTransaction(readOnly: true);
        var db = txn.OpenDefaultDatabase();
        for (int i = 0; i < Count; i++)
            txn.TryGet(db, _keys[i], out _);
    }

    [Benchmark(Description = "Native liblmdb")]
    [BenchmarkCategory("Point Get")]
    public unsafe void PointGet_Native()
    {
        void* txn;
        Native.mdb_txn_begin(_nativeEnvPtr, null, Native.MDB_NOTLS, out txn).Check();
        uint dbi;
        Native.mdb_dbi_open(txn, null, 0, out dbi).Check();
        for (int i = 0; i < Count; i++)
        {
            var key = Native.Val(_keys[i]);
            Native.MDB_val data;
            Native.mdb_get(txn, dbi, &key, &data).Check();
        }
        Native.mdb_txn_commit(txn).Check();
    }

    // ── CURSOR ITERATE (full forward scan) ──

    [Benchmark(Description = "C# port")]
    [BenchmarkCategory("Cursor Scan")]
    public int CursorScan_CsPort()
    {
        using var txn = _csEnv.BeginTransaction(readOnly: true);
        var db = txn.OpenDefaultDatabase();
        using var cur = txn.CreateCursor(db);
        int count = 0;
        if (cur.TryGet(CursorOp.First, default, out _, out _))
        {
            do { count++; }
            while (cur.TryGet(CursorOp.Next, default, out _, out _));
        }
        return count;
    }

    [Benchmark(Description = "Native liblmdb")]
    [BenchmarkCategory("Cursor Scan")]
    public unsafe int CursorScan_Native()
    {
        void* txn;
        Native.mdb_txn_begin(_nativeEnvPtr, null, Native.MDB_NOTLS, out txn).Check();
        uint dbi;
        Native.mdb_dbi_open(txn, null, 0, out dbi).Check();
        void* cur;
        Native.mdb_cursor_open(txn, dbi, out cur).Check();
        Native.MDB_val key, data;
        int count = 0;
        int rc = Native.mdb_cursor_get(cur, &key, &data, Native.MDB_FIRST);
        while (rc == 0)
        {
            count++;
            rc = Native.mdb_cursor_get(cur, &key, &data, Native.MDB_NEXT);
        }
        Native.mdb_cursor_close(cur);
        Native.mdb_txn_commit(txn).Check();
        return count;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _nativeEnv?.Dispose();
        _csEnv?.Dispose();
    }
}
