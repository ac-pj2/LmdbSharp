// Lmdb.Fuzz — fuzzing harnesses for the storage engine.
//
// Coverage-guided (requires afl-fuzz + `sharpfuzz` instrumentation of Lmdb.dll):
//   dotnet tool install --global SharpFuzz.CommandLine
//   sharpfuzz src/Lmdb/bin/Release/net10.0/Lmdb.dll
//   afl-fuzz -i corpus -o findings -t 5000 -- dotnet tests/Lmdb.Fuzz/bin/... walker
//   afl-fuzz -i corpus -o findings -t 5000 -- dotnet tests/Lmdb.Fuzz/bin/... ops
//
// No-AFL fallback (runs anywhere, used by the battery as a smoke):
//   dotnet run --project tests/Lmdb.Fuzz -- walker-rng --iterations 2000
//   dotnet run --project tests/Lmdb.Fuzz -- ops-rng --iterations 300
//
// Invariants enforced by both harnesses:
//   walker: LmdbIntegrityChecker must TERMINATE with a report on any input.
//   ops:    any op sequence accepted by the engine must leave a walker-clean
//           file; engine rejections must be LmdbException, never process
//           faults (NRE/AV/StackOverflow).
using System.Text;
using Lmdb;
using SharpFuzz;

int ArgInt(string name, int dflt)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : dflt;
}

string mode = args.Length > 0 ? args[0] : "";
switch (mode)
{
    case "walker":
        Fuzzer.OutOfProcess.Run(stream =>
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            Harness.CheckWalker(ms.ToArray());
        });
        return 0;
    case "ops":
        Fuzzer.OutOfProcess.Run(stream =>
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            Harness.RunOps(ms.ToArray());
        });
        return 0;
    case "walker-rng":
    {
        int iters = ArgInt("--iterations", 2000);
        var rng = new Random(20260716);
        var seedFile = Harness.BuildSeedDatabase();
        for (int i = 0; i < iters; i++)
        {
            byte[] input;
            if (i % 3 == 0)
            {
                input = new byte[rng.Next(3) switch { 0 => rng.Next(64), 1 => 4096 + rng.Next(128), _ => rng.Next(16) * 4096 }];
                rng.NextBytes(input);
            }
            else
            {
                input = (byte[])seedFile.Clone();
                int flips = 1 + rng.Next(24);
                for (int f = 0; f < flips; f++)
                    input[rng.Next(input.Length)] ^= (byte)(1 + rng.Next(255));
            }
            Harness.CheckWalker(input);
        }
        Console.WriteLine($"walker-rng: {iters} inputs, no crash/hang");
        return 0;
    }
    case "ops-rng":
    {
        int iters = ArgInt("--iterations", 300);
        var rng = new Random(20260716);
        for (int i = 0; i < iters; i++)
        {
            var program = new byte[rng.Next(1, 600)];
            rng.NextBytes(program);
            Harness.RunOps(program);
        }
        Console.WriteLine($"ops-rng: {iters} programs, all walker-clean");
        return 0;
    }
    default:
        Console.Error.WriteLine("usage: lmdb-fuzz <walker|ops|walker-rng|ops-rng> [--iterations N]");
        return 2;
}

static class Harness
{
    private static readonly string Root = "/tmp/lmdb-cs/fuzz";

    /// <summary>The walker must terminate with a report on ANY byte string.</summary>
    public static void CheckWalker(byte[] input)
    {
        Directory.CreateDirectory(Root);
        var file = Path.Combine(Root, "walker-input.mdb");
        File.WriteAllBytes(file, input);
        var report = LmdbIntegrityChecker.Check(file);
        if (report == null) throw new InvalidOperationException("no report");
    }

    /// <summary>Interpret input bytes as an op program against a fresh env.
    /// Engine rejections must be LmdbException; whatever commits must be
    /// walker-clean.</summary>
    public static void RunOps(byte[] program)
    {
        var dir = Path.Combine(Root, "ops-env");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        using var env = LmdbEnvironment.Open(dir, new EnvOpenOptions
        { ReadOnly = false, MapSize = 1 << 22, MaxDbs = 4 });

        int pc = 0;
        byte Next() => pc < program.Length ? program[pc++] : (byte)0;
        byte[] Blob(int max)
        {
            int len = 1 + (Next() * max / 256);
            var b = new byte[len];
            for (int i = 0; i < len; i++) b[i] = Next();
            return b;
        }

        LmdbTransaction? txn = null;
        try
        {
            while (pc < program.Length)
            {
                byte op = Next();
                try
                {
                    switch (op % 8)
                    {
                        case 0:
                            txn ??= env.BeginTransaction(readOnly: false);
                            break;
                        case 1:
                            if (txn != null) { txn.Commit(); txn.Dispose(); txn = null; }
                            break;
                        case 2:
                            if (txn != null) { txn.Abort(); txn.Dispose(); txn = null; }
                            break;
                        case 3:
                        {
                            txn ??= env.BeginTransaction(readOnly: false);
                            var db = (op & 8) != 0
                                ? txn.OpenDatabase("dup", DatabaseFlags.Create | DatabaseFlags.DupSort)
                                : txn.OpenDefaultDatabase();
                            txn.Put(db, Blob(600), Blob((op & 8) != 0 ? 500 : 3000));
                            break;
                        }
                        case 4:
                        {
                            txn ??= env.BeginTransaction(readOnly: false);
                            var db = txn.OpenDefaultDatabase();
                            txn.Delete(db, Blob(600));
                            break;
                        }
                        case 5:
                        {
                            txn ??= env.BeginTransaction(readOnly: false);
                            var db = (op & 8) != 0
                                ? txn.OpenDatabase("dup", DatabaseFlags.Create | DatabaseFlags.DupSort)
                                : txn.OpenDefaultDatabase();
                            using var cur = txn.CreateCursor(db);
                            var opsel = Next() % 4;
                            if (opsel == 0) cur.TryGet(CursorOp.First, default, out _, out _);
                            else if (opsel == 1) cur.TryGet(CursorOp.SetRange, Blob(64), out _, out _);
                            else if (opsel == 2) { if (cur.TryGet(CursorOp.First, default, out _, out _)) cur.DeleteCurrent(); }
                            else cur.TryGet(CursorOp.Last, default, out _, out _);
                            break;
                        }
                        case 6:
                        {
                            using var read = env.BeginTransaction(readOnly: true);
                            var db = read.OpenDefaultDatabase();
                            read.TryGet(db, Blob(64), out _);
                            break;
                        }
                        case 7:
                        {
                            txn ??= env.BeginTransaction(readOnly: false);
                            var db = txn.OpenDatabase($"n{Next() % 3}", DatabaseFlags.Create);
                            txn.Put(db, Blob(200), Blob(200));
                            break;
                        }
                    }
                }
                catch (LmdbException)
                {
                    // Structured rejection is allowed; abort the txn (it may be
                    // poisoned) and continue with a fresh one.
                    if (txn != null) { txn.Abort(); txn.Dispose(); txn = null; }
                }
            }
        }
        finally
        {
            txn?.Dispose();
        }
        env.Dispose();

        var report = LmdbIntegrityChecker.Check(dir);
        var bad = report.Findings.Where(f => f.Severity == IntegritySeverity.Error).ToList();
        if (bad.Count > 0)
            throw new InvalidOperationException("walker errors after op program:\n" + string.Join("\n", bad));
    }

    /// <summary>A small valid database to seed structural mutations.</summary>
    public static byte[] BuildSeedDatabase()
    {
        var dir = Path.Combine(Root, "seed-env");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        using (var env = LmdbEnvironment.Open(dir, new EnvOpenOptions
        { ReadOnly = false, MapSize = 1 << 22, MaxDbs = 4 }))
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            var dup = txn.OpenDatabase("dup", DatabaseFlags.Create | DatabaseFlags.DupSort);
            for (int i = 0; i < 30; i++)
            {
                txn.Put(db, Encoding.UTF8.GetBytes($"k{i:D3}"), new byte[40 + i * 90]);
                txn.Put(dup, "d"u8, Encoding.UTF8.GetBytes($"v{i:D3}"));
            }
            txn.Commit();
        }
        return File.ReadAllBytes(Path.Combine(dir, "data.mdb"));
    }
}
