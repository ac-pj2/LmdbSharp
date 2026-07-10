// Extended differential fuzzer: DUPSORT mode. Generates random sequences of
// put/get/delete operations on a DUPSORT database, runs them through BOTH this
// C# port and Python lmdb (real C LMDB), and asserts identical results.
//
// This exercises the most complex code paths: sub-page creation, growth,
// sub-DB conversion, cursor dup iteration, and index maintenance.
using Lmdb;
using System.Diagnostics;
using System.Text;
using Xunit.Abstractions;

namespace Lmdb.Tests;

[Trait("Category", "Fuzz")]
public class DupSortFuzzTests
{
    private readonly ITestOutputHelper _out;
    public DupSortFuzzTests(ITestOutputHelper out_) => _out = out_;

    private static string TmpDir(string name)
    {
        string dir = $"/tmp/lmdb-fuzz-dup/{name}";
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    private enum DupOp { Put, Get, Delete, IterateAll, IterateKey }

    /// <summary>Run a random DUPSORT op sequence through both C# and Python, compare.</summary>
    private void RunDupFuzzCase(int seed, int numOps, int keySpace, int valSpace)
    {
        var rng = new Random(seed);
        var ops = new (DupOp op, string key, string val)[numOps];
        for (int i = 0; i < numOps; i++)
        {
            DupOp op = (DupOp)rng.Next(5);
            string key = $"k{rng.Next(keySpace):D3}";
            string val = $"v{rng.Next(valSpace):D3}";
            ops[i] = (op, key, val);
        }

        // --- Run through C# ---
        var csResults = new List<string>();
        using (var env = LmdbEnvironment.Open(TmpDir($"cs_{seed}"), new EnvOpenOptions
        { ReadOnly = false, MapSize = 1 << 24, NoLock = true, MaxDbs = 4 }))
        {
            using var txn = env.BeginTransaction(false);
            var db = txn.OpenDatabase("dups", DatabaseFlags.Create | DatabaseFlags.DupSort);
            foreach (var (op, key, val) in ops)
            {
                switch (op)
                {
                    case DupOp.Put:
                        txn.Put(db, B(key), B(val));
                        csResults.Add($"PUT {key} {val}");
                        break;
                    case DupOp.Get:
                        // GET returns the FIRST dup value for a key.
                        if (txn.TryGet(db, B(key), out var data))
                            csResults.Add($"GET {key} = {S(data)}");
                        else
                            csResults.Add($"GET {key} = NONE");
                        break;
                    case DupOp.Delete:
                        bool del = txn.Delete(db, B(key));
                        csResults.Add($"DEL {key} {(del ? "OK" : "MISS")}");
                        break;
                    case DupOp.IterateAll:
                        using (var cur = txn.CreateCursor(db))
                        {
                            int count = 0;
                            if (cur.TryGet(CursorOp.First, default, out _, out _))
                                do count++; while (cur.TryGet(CursorOp.Next, default, out _, out _));
                            csResults.Add($"ITERALL {count}");
                        }
                        break;
                    case DupOp.IterateKey:
                        using (var cur = txn.CreateCursor(db))
                        {
                            int count = 0;
                            if (cur.TryGet(CursorOp.Set, B(key), out _, out _))
                            {
                                do count++; while (cur.TryGet(CursorOp.NextDup, default, out _, out _));
                            }
                            csResults.Add($"ITERKEY {key} {count}");
                        }
                        break;
                }
            }
            txn.Commit();
        }

        // --- Run through Python ---
        var pyResults = RunPythonDupFuzz(TmpDir($"py_{seed}"), ops);

        // --- Compare ---
        bool match = true;
        for (int i = 0; i < csResults.Count && i < pyResults.Count; i++)
        {
            if (csResults[i] != pyResults[i])
            {
                _out.WriteLine($"MISMATCH at op {i}: C#=\"{csResults[i]}\" vs PY=\"{pyResults[i]}\"");
                match = false;
                break;  // report first mismatch only
            }
        }
        if (csResults.Count != pyResults.Count)
        {
            _out.WriteLine($"COUNT MISMATCH: C#={csResults.Count} vs PY={pyResults.Count}");
            match = false;
        }
        // Dump first 20 ops for debugging.
        if (!match)
        {
            _out.WriteLine("--- C# results (first 20) ---");
            for (int i = 0; i < Math.Min(20, csResults.Count); i++)
                _out.WriteLine($"  [{i}] {csResults[i]}");
            _out.WriteLine("--- Python results (first 20) ---");
            for (int i = 0; i < Math.Min(20, pyResults.Count); i++)
                _out.WriteLine($"  [{i}] {pyResults[i]}");
        }
        Assert.True(match && csResults.Count == pyResults.Count,
            $"DupSort fuzz seed={seed}: mismatch at op sequence");
    }

    private List<string> RunPythonDupFuzz(string dir, (DupOp op, string key, string val)[] ops)
    {
        var sb = new StringBuilder();
        sb.AppendLine("import lmdb, os, shutil");
        sb.AppendLine($"shutil.rmtree('{dir}', ignore_errors=True); os.makedirs('{dir}')");
        sb.AppendLine($"env = lmdb.open('{dir}', map_size={1<<24}, max_dbs=4)");
        sb.AppendLine("dbi = env.open_db(b'dups', dupsort=True, create=True)");
        sb.AppendLine("txn = env.begin(write=True, db=dbi)");
        foreach (var (op, key, val) in ops)
        {
            switch (op)
            {
                case DupOp.Put:
                    sb.AppendLine($"txn.put({PyB(key)}, {PyB(val)}); print('PUT {key} {val}')");
                    break;
                case DupOp.Get:
                    sb.AppendLine($"r = txn.get({PyB(key)}); print(f'GET {key} = {{r.decode() if r else \"NONE\"}}')");
                    break;
                case DupOp.Delete:
                    sb.AppendLine($"r = txn.delete({PyB(key)}); print(f'DEL {key} {{\"OK\" if r else \"MISS\"}}')");
                    break;
                case DupOp.IterateAll:
                    sb.AppendLine($"c = txn.cursor(); n = sum(1 for _ in c); print(f'ITERALL {{n}}')");
                    break;
                case DupOp.IterateKey:
                    sb.AppendLine($"c = txn.cursor(); n = 0;");
                    sb.AppendLine($"if c.set_key({PyB(key)}):");
                    sb.AppendLine($"    for _ in c.iternext_dup(): n += 1");
                    sb.AppendLine($"print(f'ITERKEY {key} {{n}}')");
                    break;
            }
        }
        sb.AppendLine("txn.commit(); env.close()");

        return RunScript(sb.ToString());
    }

    private static List<string> RunScript(string script)
    {
        string scriptPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dupfuzz_{Guid.NewGuid():N}.py");
        System.IO.File.WriteAllText(scriptPath, script);
        try
        {
            var psi = new ProcessStartInfo("python3")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(scriptPath);
            using var p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(30000);
            return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                         .Select(l => l.Trim()).ToList();
        }
        finally { System.IO.File.Delete(scriptPath); }
    }

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static string S(ReadOnlySpan<byte> b) => Encoding.UTF8.GetString(b);
    private static string PyB(string s) => "b'" + s + "'";

    [Theory]
    [InlineData(1, 50, 5, 5)]
    [InlineData(2, 100, 10, 10)]
    [InlineData(3, 200, 5, 20)]
    [InlineData(4, 100, 20, 5)]
    [InlineData(5, 300, 10, 10)]
    [InlineData(6, 200, 3, 50)]     // few keys, many dups → sub-page growth + sub-DB conversion
    [InlineData(7, 500, 100, 3)]
    [InlineData(8, 150, 8, 15)]
    [InlineData(9, 400, 50, 8)]
    [InlineData(10, 100, 5, 100)]   // single key with tons of dups → sub-DB conversion
    public void DupFuzz_Differential(int seed, int numOps, int keySpace, int valSpace)
    {
        RunDupFuzzCase(seed, numOps, keySpace, valSpace);
    }
}
