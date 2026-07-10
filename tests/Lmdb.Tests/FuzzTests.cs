// Differential fuzzer: generates random sequences of LMDB operations (put, get,
// delete, cursor iterate), runs them through BOTH this C# port and the real C
// LMDB (via Python), and asserts identical results.
//
// This is the highest-value test for an embedded database foundation. It catches
// the subtle bugs in split/rebalance/freelist paths that hand-written tests miss.
//
// Run: dotnet test --filter "FuzzTests" (or run indefinitely with --filter "FuzzLongRun")
using Lmdb;
using System.Diagnostics;
using System.Text;
using Xunit.Abstractions;

namespace Lmdb.Tests;

[Trait("Category", "Fuzz")]
public class FuzzTests
{
    private readonly ITestOutputHelper _out;
    public FuzzTests(ITestOutputHelper out_) => _out = out_;

    private static string TmpDir(string name)
    {
        string dir = $"/tmp/lmdb-fuzz/{name}";
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A single fuzz operation.</summary>
    private enum Op { Put, Get, Delete, Iterate }

    /// <summary>Run a random sequence of ops through both C# and Python, compare results.</summary>
    private void RunFuzzCase(int seed, int numOps, int keySpace, int maxValSize)
    {
        var rng = new Random(seed);
        string dir = TmpDir($"case_{seed}");

        // Generate the op sequence.
        var ops = new (Op op, string key, string val)[numOps];
        for (int i = 0; i < numOps; i++)
        {
            Op op = (Op)rng.Next(4);
            string key = $"k{rng.Next(keySpace):D4}";
            int valLen = rng.Next(1, maxValSize);
            string val = new string((char)('a' + rng.Next(26)), valLen);
            ops[i] = (op, key, val);
        }

        // --- Run through C# ---
        var csResults = new List<string>();
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions { ReadOnly = false, MapSize = 1 << 24, NoLock = true }))
        {
            using var txn = env.BeginTransaction(false);
            var db = txn.OpenDefaultDatabase();
            foreach (var (op, key, val) in ops)
            {
                switch (op)
                {
                    case Op.Put:
                        try { txn.Put(db, B(key), B(val)); csResults.Add($"PUT {key} OK"); }
                        catch (LmdbException) { csResults.Add($"PUT {key} ERR"); }
                        break;
                    case Op.Get:
                        if (txn.TryGet(db, B(key), out var data))
                            csResults.Add($"GET {key} = {S(data)}");
                        else
                            csResults.Add($"GET {key} = NOTFOUND");
                        break;
                    case Op.Delete:
                        bool del = txn.Delete(db, B(key));
                        csResults.Add($"DEL {key} {(del ? "OK" : "MISS")}");
                        break;
                    case Op.Iterate:
                        using (var cur = txn.CreateCursor(db))
                        {
                            int count = 0;
                            if (cur.TryGet(CursorOp.First, default, out var k, out _))
                            {
                                do count++; while (cur.TryGet(CursorOp.Next, default, out k, out _));
                            }
                            csResults.Add($"ITER count={count}");
                        }
                        break;
                }
            }
            txn.Commit();
        }

        // --- Run through Python (real C LMDB) ---
        var pyResults = RunPythonFuzz(dir + "_py", ops);

        // --- Compare ---
        bool match = true;
        int mismatches = 0;
        for (int i = 0; i < csResults.Count && i < pyResults.Count; i++)
        {
            if (csResults[i] != pyResults[i])
            {
                if (mismatches < 5)
                    _out.WriteLine($"MISMATCH at op {i}: C#=\"{csResults[i]}\" vs PY=\"{pyResults[i]}\"");
                mismatches++;
                match = false;
            }
        }
        if (csResults.Count != pyResults.Count)
        {
            _out.WriteLine($"COUNT MISMATCH: C#={csResults.Count} vs PY={pyResults.Count}");
            match = false;
        }
        Assert.True(match, $"Fuzz case seed={seed}: {mismatches} mismatches out of {csResults.Count} ops");
    }

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static string S(ReadOnlySpan<byte> b) => Encoding.UTF8.GetString(b);

    /// <summary>Run the same op sequence through Python's lmdb binding (real C LMDB).</summary>
    private List<string> RunPythonFuzz(string dir, (Op op, string key, string val)[] ops)
    {
        var results = new List<string>();
        var sb = new StringBuilder();
        sb.AppendLine("import lmdb, os, shutil");
        sb.AppendLine($"shutil.rmtree('{dir}', ignore_errors=True); os.makedirs('{dir}')");
        sb.AppendLine($"env = lmdb.open('{dir}', map_size={1<<24})");
        sb.AppendLine("txn = env.begin(write=True)");
        foreach (var (op, key, val) in ops)
        {
            switch (op)
            {
                case Op.Put:
                    sb.AppendLine($"try: txn.put({PyStr(key)}, {PyStr(val)}); print('PUT {key} OK')");
                    sb.AppendLine($"except: print('PUT {key} ERR')");
                    break;
                case Op.Get:
                    sb.AppendLine($"r = txn.get({PyStr(key)}); print(f'GET {key} = {{r.decode() if r else \"NOTFOUND\"}}')");
                    break;
                case Op.Delete:
                    sb.AppendLine($"r = txn.delete({PyStr(key)}); print(f'DEL {key} {{\"OK\" if r else \"MISS\"}}')");
                    break;
                case Op.Iterate:
                    sb.AppendLine($"c = txn.cursor(); n = sum(1 for _ in c); print(f'ITER count={{n}}')");
                    break;
            }
        }
        sb.AppendLine("txn.commit(); env.close()");

        // Write script to a temp file (avoids "argument list too long" for large op counts).
        string scriptPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fuzz_{dir.Replace('/', '_')}.py");
        System.IO.File.WriteAllText(scriptPath, sb.ToString());

        var psi = new ProcessStartInfo("python3")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(scriptPath);
        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit(30000);
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            results.Add(line.Trim());
        return results;
    }

    private static string PyStr(string s) => "b'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    // --- Fuzz test cases (each with a different seed for coverage) ---

    [Theory]
    [InlineData(1, 50, 10, 10)]
    [InlineData(2, 100, 20, 20)]
    [InlineData(3, 100, 10, 5)]
    [InlineData(4, 200, 50, 30)]
    [InlineData(5, 100, 5, 100)]
    [InlineData(6, 300, 100, 10)]
    [InlineData(7, 200, 30, 50)]
    [InlineData(8, 500, 200, 5)]
    [InlineData(9, 100, 10, 200)]
    [InlineData(10, 50, 5, 500)]
    // Larger cases that stress page splitting and rebalancing.
    [InlineData(11, 1000, 500, 10)]
    [InlineData(12, 1000, 100, 5)]
    [InlineData(13, 500, 50, 100)]
    [InlineData(14, 2000, 1000, 3)]
    [InlineData(15, 500, 10, 400)]
    public void Fuzz_Differential(int seed, int numOps, int keySpace, int maxValSize)
    {
        RunFuzzCase(seed, numOps, keySpace, maxValSize);
    }
}
