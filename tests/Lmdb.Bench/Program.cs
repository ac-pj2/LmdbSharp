// LMDB C# port benchmarks. Measures write, point-read, and cursor-iteration
// throughput against the same workload as the Python lmdb (native C) baseline.
//
// Run: dotnet run -c Release --project tests/Lmdb.Bench
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using Lmdb;
using System.Text;

BenchmarkRunner.Run<LmdbBench>();

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.Method)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class LmdbBench
{
    private string _dir = null!;
    private byte[][] _keys = null!;
    private byte[][] _vals = null!;
    private LmdbEnvironment _env = null!;
    private LmdbDatabase _db = null!;
    private LmdbTransaction _readTxn = null!;

    [Params(100000)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _dir = $"/tmp/lmdb-bench/{Count}";
        if (System.IO.Directory.Exists(_dir)) System.IO.Directory.Delete(_dir, true);
        System.IO.Directory.CreateDirectory(_dir);

        _keys = new byte[Count][];
        _vals = new byte[Count][];
        for (int i = 0; i < Count; i++)
        {
            _keys[i] = Encoding.UTF8.GetBytes($"key{i:00000000}");
            _vals[i] = Encoding.UTF8.GetBytes($"value{i:00000000}_payload_data");
        }

        // Pre-build the DB for read benchmarks.
        using (var env = LmdbEnvironment.Open(_dir, new EnvOpenOptions { ReadOnly = false, MapSize = 64L * 1024 * 1024 }))
        using (var txn = env.BeginTransaction(readOnly: false))
        {
            var db = txn.OpenDefaultDatabase();
            for (int i = 0; i < Count; i++)
                txn.Put(db, _keys[i], _vals[i]);
            txn.Commit();
        }

        // Open the read environment + transaction.
        _env = LmdbEnvironment.Open(_dir);
        _readTxn = _env.BeginTransaction(readOnly: true);
        _db = _readTxn.OpenDefaultDatabase();
    }

    /// <summary>Bulk-insert Count keys in a single write transaction.</summary>
    [Benchmark, BenchmarkCategory("Write")]
    public void Write()
    {
        var wdir = _dir + "_write";
        if (System.IO.Directory.Exists(wdir)) System.IO.Directory.Delete(wdir, true);
        System.IO.Directory.CreateDirectory(wdir);
        using var env = LmdbEnvironment.Open(wdir, new EnvOpenOptions { ReadOnly = false, MapSize = 64L * 1024 * 1024, NoLock = true });
        using var txn = env.BeginTransaction(readOnly: false);
        var db = txn.OpenDefaultDatabase();
        for (int i = 0; i < Count; i++)
            txn.Put(db, _keys[i], _vals[i]);
        txn.Commit();
    }

    /// <summary>Point-lookup every key in a read transaction.</summary>
    [Benchmark, BenchmarkCategory("Read")]
    public void PointGet()
    {
        for (int i = 0; i < Count; i++)
            _readTxn.TryGet(_db, _keys[i], out _);
    }

    /// <summary>Full forward cursor iteration of all entries.</summary>
    [Benchmark, BenchmarkCategory("Read")]
    public int CursorIterate()
    {
        using var cur = _readTxn.CreateCursor(_db);
        int count = 0;
        if (cur.TryGet(CursorOp.First, default, out _, out _))
        {
            do { count++; }
            while (cur.TryGet(CursorOp.Next, default, out _, out _));
        }
        return count;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _readTxn?.Dispose();
        _env?.Dispose();
    }
}
