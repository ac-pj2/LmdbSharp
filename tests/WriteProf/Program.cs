// Write-path profiling harness: runs 100k puts with timing instrumentation.
// Build: dotnet build -c Release
// Run: dotnet run -c Release
using Lmdb;
using System.Diagnostics;
using System.Text;

var dir = "/tmp/lmdb-writeprof";
if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
System.IO.Directory.CreateDirectory(dir);

int Count = 5000000;  // 5M for profiling (gives ~1.5s window)
var keys = new byte[Count][];
var vals = new byte[Count][];
for (int i = 0; i < Count; i++)
{
    keys[i] = Encoding.UTF8.GetBytes($"key{i:00000000}");
    vals[i] = Encoding.UTF8.GetBytes($"value{i:00000000}_payload_data");
}

// Warmup
using (var env = LmdbEnvironment.Open(dir + "_warm", new EnvOpenOptions { ReadOnly = false, MapSize = 64L << 20, NoLock = true }))
using (var txn = env.BeginTransaction(false))
{
    var db = txn.OpenDefaultDatabase();
    for (int i = 0; i < 1000; i++) txn.Put(db, keys[i], vals[i]);
    txn.Commit();
}

// Profiled run
if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
System.IO.Directory.CreateDirectory(dir);

var sw = Stopwatch.StartNew();
using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 64L << 20, NoLock = true }))
using (var txn = env.BeginTransaction(false))
{
    var db = txn.OpenDefaultDatabase();
    for (int i = 0; i < Count; i++)
        txn.Put(db, keys[i], vals[i]);

    var putTime = sw.Elapsed;
    Console.WriteLine($"puts: {putTime.TotalMilliseconds:F1}ms ({Count / putTime.TotalSeconds:N0} ops/s)");

    txn.Commit();
    Console.WriteLine($"commit: {(sw.Elapsed - putTime).TotalMilliseconds:F1}ms");
}
sw.Stop();
Console.WriteLine($"total: {sw.Elapsed.TotalMilliseconds:F1}ms");
Console.WriteLine($"GC alloc: {System.GC.GetTotalAllocatedBytes(precise: true):N0} bytes");
