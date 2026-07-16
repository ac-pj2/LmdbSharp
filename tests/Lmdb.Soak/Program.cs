// Lmdb.Soak — standalone reliability harness for the storage engine.
//
//   dotnet run --project tests/Lmdb.Soak -- soak [--seeds N] [--txns N] [--seed S] [--no-reuse]
//   dotnet run --project tests/Lmdb.Soak -- kill [--iterations N]
//   dotnet run --project tests/Lmdb.Soak -- diff [--seeds N] [--ops N]
//
// soak  Randomized model-checked workload: named DBs, DUPSORT indexes, overflow
//       values, deletes, aborts, no-write commits, nested transactions,
//       long-lived readers, environment reopens. After EVERY commit the file is
//       verified by the read-only integrity walker; at the end the full content
//       is compared against an in-memory shadow model. Any failure prints the
//       seed — rerun with --seed to reproduce deterministically.
//
// kill  Real crash testing: spawns a writer child process (this binary with
//       `worker`) that commits sequenced, checksummed records, SIGKILLs it at a
//       random moment, then verifies the environment is walker-clean and every
//       acknowledged commit is durable and correct. Repeats.
//
// diff  Differential validation against the C LMDB implementation via the
//       Python `lmdb` binding: the same random op sequence is applied to both
//       engines and the full resulting contents are compared.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Lmdb;

int ArgInt(string name, int dflt)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : dflt;
}

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: lmdb-soak <soak|app|kill|diff|worker> [options]");
    return 2;
}

return args[0] switch
{
    "app" => AppShapedMode.Run(
        cycles: ArgInt("--cycles", 40),
        writesPerCycle: ArgInt("--writes", 50)),
    "soak" => SoakMode.Run(
        seeds: ArgInt("--seeds", 25),
        txns: ArgInt("--txns", 120),
        onlySeed: ArgInt("--seed", 0),
        reuse: Array.IndexOf(args, "--no-reuse") < 0),
    "kill" => KillMode.Run(iterations: ArgInt("--iterations", 15)),
    "worker" => KillMode.Worker(args[1]),
    "diskfull" => DiskFullMode.Run(iterations: ArgInt("--iterations", 3)),
    "diskfull-child" => DiskFullMode.Child(args[1], long.Parse(args[2])),
    "diff" => DiffMode.Run(seeds: ArgInt("--seeds", 10), ops: ArgInt("--ops", 400)),
    _ => Fail($"unknown mode '{args[0]}'"),
};

static int Fail(string msg) { Console.Error.WriteLine(msg); return 2; }

// ─────────────────────────────── soak ───────────────────────────────

static class SoakMode
{
    public static int Run(int seeds, int txns, int onlySeed, bool reuse)
    {
        var failed = new List<int>();
        var seedList = onlySeed != 0
            ? new[] { onlySeed }
            : Enumerable.Range(1, seeds).Select(i => i * 7919).ToArray();

        foreach (int seed in seedList)
        {
            try
            {
                RunSeed(seed, txns, reuse);
                Console.WriteLine($"seed {seed}: OK ({txns} txns, reuse={reuse})");
            }
            catch (Exception e)
            {
                failed.Add(seed);
                Console.Error.WriteLine($"seed {seed}: FAILED\n{e}");
                Console.Error.WriteLine($"reproduce: dotnet run --project tests/Lmdb.Soak -- soak --seed {seed} --txns {txns}{(reuse ? "" : " --no-reuse")}");
            }
        }
        Console.WriteLine(failed.Count == 0 ? "SOAK CLEAN" : $"SOAK FAILED seeds: {string.Join(",", failed)}");
        return failed.Count == 0 ? 0 : 1;
    }

    private static void RunSeed(int seed, int txns, bool reuse)
    {
        var rng = new Random(seed);
        string path = $"/tmp/lmdb-cs/soak-{seed}";
        if (Directory.Exists(path)) Directory.Delete(path, true);
        Directory.CreateDirectory(path);

        string[] plainDbs = { "", "alpha", "beta" };
        string[] dupDbs = { "idx:a", "idx:b" };
        // model: plain dbs → key→value; dup dbs → set of (key,value) pairs
        var model = new Dictionary<(string db, string key), byte[]>();
        var dupModel = new HashSet<(string db, long key, long val)>();

        LmdbEnvironment Open() => LmdbEnvironment.Open(path, new EnvOpenOptions
        { ReadOnly = false, MapSize = 1 << 26, MaxDbs = 16, ReuseFreePages = reuse });

        var env = Open();
        LmdbTransaction? heldReader = null;
        try
        {
            using (var init = env.BeginTransaction(readOnly: false))
            {
                foreach (var n in plainDbs.Where(n => n.Length > 0))
                    init.OpenDatabase(n, DatabaseFlags.Create);
                foreach (var n in dupDbs)
                    init.OpenDatabase(n, DatabaseFlags.Create | DatabaseFlags.DupSort);
                init.Put(init.OpenDefaultDatabase(), "init"u8, "init"u8);
                init.Commit();
            }
            model[("", "init")] = "init"u8.ToArray();

            var trace = new List<string>();
            for (int t = 0; t < txns; t++)
            {
                if (heldReader == null && rng.Next(5) == 0)
                    heldReader = env.BeginTransaction(readOnly: true);
                else if (heldReader != null && rng.Next(3) == 0)
                { heldReader.Dispose(); heldReader = null; }

                int action = rng.Next(12);
                trace.Add($"txn#{t} action={action}");
                if (trace.Count > 6) trace.RemoveAt(0);
                OpTrace = trace;
                if (action == 0)
                {
                    using var idle = env.BeginTransaction(readOnly: false);
                    idle.Commit();
                }
                else if (action == 1)
                {
                    using var doomed = env.BeginTransaction(readOnly: false);
                    ApplyOps(doomed, rng, plainDbs, dupDbs, new(), new(), new());
                    doomed.Abort();
                }
                else if (action == 2)
                {
                    var pend = new Dictionary<(string, string), byte[]?>();
                    var dupAdd = new HashSet<(string, long, long)>();
                    var dupDel = new HashSet<(string, long, long)>();
                    using var parent = env.BeginTransaction(readOnly: false);
                    ApplyOps(parent, rng, plainDbs, dupDbs, pend, dupAdd, dupDel);
                    var cpend = new Dictionary<(string, string), byte[]?>();
                    using (var child = parent.BeginChild())
                    {
                        ApplyOps(child, rng, new[] { "" }, Array.Empty<string>(), cpend, new(), new());
                        if (rng.Next(2) == 0) { child.Commit(); foreach (var kv in cpend) pend[kv.Key] = kv.Value; }
                        else child.Abort();
                    }
                    parent.Commit();
                    Apply(model, pend); ApplyDup(dupModel, dupAdd, dupDel);
                }
                else
                {
                    var pend = new Dictionary<(string, string), byte[]?>();
                    var dupAdd = new HashSet<(string, long, long)>();
                    var dupDel = new HashSet<(string, long, long)>();
                    using var txn = env.BeginTransaction(readOnly: false);
                    ApplyOps(txn, rng, plainDbs, dupDbs, pend, dupAdd, dupDel);
                    txn.Commit();
                    Apply(model, pend); ApplyDup(dupModel, dupAdd, dupDel);
                }

                var report = LmdbIntegrityChecker.Check(path);
                var significant = report.Findings.Where(f => f.Severity != IntegritySeverity.Info).ToList();
                if (significant.Count > 0)
                    throw new Exception($"walker findings after txn#{t} (trace: {string.Join(" | ", trace)}):\n{string.Join("\n", significant)}");

                if (rng.Next(15) == 0)
                {
                    heldReader?.Dispose(); heldReader = null;
                    env.Dispose(); env = Open();
                }
            }

            VerifyModel(env, model, dupModel);

            // Steady state must not leak a single page: everything reachable
            // or freelisted (the persisted remainder covers restarts too).
            env.Dispose(); env = Open();
            var final = LmdbIntegrityChecker.Check(path);
            if (final.Findings.Any(f => f.Code == "leaked-pages"))
                throw new Exception("pages leaked:\n" + final.Render());
        }
        finally
        {
            heldReader?.Dispose();
            env.Dispose();
            Directory.Delete(path, true);
        }
    }

    internal static List<string>? OpTrace;

    private static void ApplyOps(LmdbTransaction txn, Random rng, string[] plainDbs, string[] dupDbs,
        Dictionary<(string, string), byte[]?> pend,
        HashSet<(string, long, long)> dupAdd, HashSet<(string, long, long)> dupDel)
    {
        int ops = 1 + rng.Next(8);
        for (int i = 0; i < ops; i++)
        {
            if (dupDbs.Length > 0 && rng.Next(3) == 0)
            {
                // DUPSORT op: add or remove a specific (key,value) pair.
                string dbName = dupDbs[rng.Next(dupDbs.Length)];
                var db = txn.OpenDatabase(dbName);
                long key = rng.Next(8);
                long val = rng.Next(120);
                if (rng.Next(3) == 0)
                {
                    using var cur = txn.CreateCursor(db);
                    if (cur.TryGet(CursorOp.GetBoth, Big(key), DupVal(key, val), out _, out _))
                    { cur.DeleteCurrent(); dupDel.Add((dbName, key, val)); dupAdd.Remove((dbName, key, val)); }
                }
                else
                {
                    txn.Put(db, Big(key), DupVal(key, val));
                    dupAdd.Add((dbName, key, val)); dupDel.Remove((dbName, key, val));
                }
            }
            else
            {
                string dbName = plainDbs[rng.Next(plainDbs.Length)];
                var db = dbName.Length == 0 ? txn.OpenDefaultDatabase() : txn.OpenDatabase(dbName);
                int shape = rng.Next(10);
                OpTrace?.Add($"{dbName}:shape{shape}");
                if (shape == 0)
                {
                    // Sequential burst of mid-size values: deepens the tree and
                    // drives splits (incl. recursive parent splits) hard.
                    int start = rng.Next(4000);
                    for (int b = 0; b < 25; b++)
                    {
                        string bk = $"seq{start + b:D6}";
                        var bv = new byte[600 + rng.Next(500)];
                        rng.NextBytes(bv);
                        txn.Put(db, Encoding.UTF8.GetBytes(bk), bv);
                        pend[(dbName, bk)] = bv;
                    }
                }
                else if (shape == 1 && dbName.Length > 0)
                {
                    // Cursor-iteration range delete: DeleteCurrent under live
                    // rebalance cascades (root collapses, fromleft merges).
                    // (Not on the main DB — its F_SUBDATA records are protected.)
                    using var cur = txn.CreateCursor(db);
                    int budget = 15;
                    if (cur.TryGet(CursorOp.First, default, out var ck, out _))
                    {
                        while (budget-- > 0)
                        {
                            string dk = Encoding.UTF8.GetString(ck);
                            cur.DeleteCurrent();
                            pend[(dbName, dk)] = null;
                            if (!cur.TryGet(CursorOp.First, default, out ck, out _)) break;
                        }
                    }
                }
                else if (shape <= 3)
                {
                    string key = $"k{rng.Next(48)}";
                    txn.Delete(db, Encoding.UTF8.GetBytes(key));
                    pend[(dbName, key)] = null;
                }
                else
                {
                    string key = rng.Next(4) == 0 ? $"seq{rng.Next(4000):D6}" : $"k{rng.Next(48)}";
                    int len = rng.Next(3) == 0 ? 2200 + rng.Next(9000) : 1 + rng.Next(150);
                    var value = new byte[len];
                    rng.NextBytes(value);
                    txn.Put(db, Encoding.UTF8.GetBytes(key), value);
                    pend[(dbName, key)] = value;
                }
            }
        }
    }

    private static byte[] Big(long v)
    {
        var b = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(b, v);
        return b;
    }

    /// <summary>Deterministic variable-length dup value for (key,val): stresses
    /// sub-page growth and the sub-page→sub-DB conversion with mixed sizes.</summary>
    private static byte[] DupVal(long key, long val)
    {
        int len = 8 + (int)((key * 31 + val * 7) % 180);
        var b = new byte[len];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(b, val);
        for (int i = 8; i < len; i++) b[i] = (byte)(key ^ val ^ i);
        return b;
    }

    private static void Apply(Dictionary<(string, string), byte[]> model, Dictionary<(string, string), byte[]?> pend)
    {
        foreach (var (k, v) in pend) { if (v == null) model.Remove(k); else model[k] = v; }
    }

    private static void ApplyDup(HashSet<(string, long, long)> model,
        HashSet<(string, long, long)> add, HashSet<(string, long, long)> del)
    {
        foreach (var d in del) model.Remove(d);
        foreach (var a in add) model.Add(a);
    }

    private static void VerifyModel(LmdbEnvironment env,
        Dictionary<(string db, string key), byte[]> model, HashSet<(string, long, long)> dupModel)
    {
        using var read = env.BeginTransaction(readOnly: true);
        foreach (var ((dbName, key), expected) in model)
        {
            var db = dbName.Length == 0 ? read.OpenDefaultDatabase() : read.OpenDatabase(dbName);
            if (!read.TryGet(db, Encoding.UTF8.GetBytes(key), out var actual))
                throw new Exception($"model mismatch: '{dbName}/{key}' missing");
            if (!actual.SequenceEqual(expected))
                throw new Exception($"model mismatch: '{dbName}/{key}' value differs");
        }
        // Full dup scan both directions.
        var seen = new HashSet<(string, long, long)>();
        foreach (var dbName in new[] { "idx:a", "idx:b" })
        {
            var db = read.OpenDatabase(dbName);
            using var cur = read.CreateCursor(db);
            if (cur.TryGet(CursorOp.First, default, out var k, out var v))
                do
                {
                    seen.Add((dbName,
                        System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(k),
                        System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(v)));   // value prefix = val id
                } while (cur.TryGet(CursorOp.Next, default, out k, out v));
        }
        if (!seen.SetEquals(dupModel))
        {
            var missing = dupModel.Except(seen).Take(5);
            var extra = seen.Except(dupModel).Take(5);
            throw new Exception($"dup model mismatch: missing [{string.Join(" ", missing)}] extra [{string.Join(" ", extra)}]");
        }

        // md_entries must exactly match the model per database.
        foreach (var dbName in new[] { "", "alpha", "beta" })
        {
            var db = dbName.Length == 0 ? read.OpenDefaultDatabase() : read.OpenDatabase(dbName);
            ulong expected = (ulong)model.Keys.Count(k => k.db == dbName);
            if (dbName.Length == 0) expected += 4;   // the named-DB records live in main
            if (db.Entries != expected)
                throw new Exception($"entries drift in '{dbName}': md_entries={db.Entries} model={expected}");
        }
        foreach (var dbName in new[] { "idx:a", "idx:b" })
        {
            var db = read.OpenDatabase(dbName);
            ulong expected = (ulong)dupModel.Count(d => d.Item1 == dbName);
            if (db.Entries != expected)
                throw new Exception($"entries drift in '{dbName}': md_entries={db.Entries} model={expected}");
        }
    }
}

// ─────────────────────────────── app ────────────────────────────────

/// <summary>Application-shaped soak: the exact store layout and access pattern
/// of the downstream consumer that hit the original corruption (records +
/// DUPSORT index DBs + sequences; per-txn multi-DB writes with GetBoth +
/// DeleteCurrent index maintenance; continuous concurrent scans; restart
/// cycles). Strict walker after every cycle; zero leaked pages at the end.
/// This mode found the freelist-save divergence and the split boundary bug.</summary>
static class AppShapedMode
{
    public static int Run(int cycles, int writesPerCycle)
    {
        string path = "/tmp/lmdb-cs/app-shaped-soak";
        if (Directory.Exists(path)) Directory.Delete(path, true);
        Directory.CreateDirectory(path);

        LmdbEnvironment Open() => LmdbEnvironment.Open(path, new EnvOpenOptions
        { ReadOnly = false, MapSize = 1L << 30, MaxDbs = 64, ReuseFreePages = true });

        byte[] K(string s) => Encoding.UTF8.GetBytes(s);
        byte[] Json(long id, int rev) =>
            Encoding.UTF8.GetBytes($"{{\"id\":{id},\"rev\":{rev},\"body\":\"{new string('x', 400 + (int)(id % 2200))}\"}}");

        long nextId = 0;
        int failures = 0;
        var env = Open();
        using (var init = env.BeginTransaction(readOnly: false))
        {
            init.OpenDatabase("records", DatabaseFlags.Create);
            init.OpenDatabase("records:Type", DatabaseFlags.Create | DatabaseFlags.DupSort);
            init.OpenDatabase("records:ref:a", DatabaseFlags.Create | DatabaseFlags.DupSort);
            init.OpenDatabase("records:ref:b", DatabaseFlags.Create);
            init.OpenDatabase("sequences", DatabaseFlags.Create);
            init.Commit();
        }

        try
        {
            for (int cycle = 0; cycle < cycles && failures == 0; cycle++)
            {
                using var stopScans = new CancellationTokenSource();
                var scanners = Enumerable.Range(0, 3).Select(scanIdx => Task.Run(() =>
                {
                    while (!stopScans.IsCancellationRequested)
                    {
                        using var read = env.BeginTransaction(readOnly: true);
                        var db = read.OpenDatabase("records");
                        using var cur = read.CreateCursor(db);
                        if (cur.TryGet(CursorOp.First, default, out _, out var v))
                            do
                            {
                                if (v.Length < 2 || v[0] != (byte)'{')
                                { Interlocked.Increment(ref failures); Console.WriteLine("NON-JSON bytes in scan!"); return; }
                            } while (cur.TryGet(CursorOp.Next, default, out _, out v));
                    }
                })).ToArray();

                for (int w = 0; w < writesPerCycle; w++)
                {
                    using var txn = env.BeginTransaction(readOnly: false);
                    var records = txn.OpenDatabase("records");
                    var typeIdx = txn.OpenDatabase("records:Type");
                    var refA = txn.OpenDatabase("records:ref:a");
                    var refB = txn.OpenDatabase("records:ref:b");
                    var seq = txn.OpenDatabase("sequences");

                    long id = nextId++;
                    txn.Put(records, K($"rec:{id:D8}"), Json(id, 1));
                    txn.Put(typeIdx, K("ticket"), K($"{id:D8}"));
                    txn.Put(refA, K($"{id % 25:D4}"), K($"{id:D8}"));
                    txn.Put(refB, K($"{id:D8}"), K($"{id % 25:D4}"));
                    txn.Put(seq, K("records"), K(nextId.ToString()));

                    if (id >= 30)
                    {
                        txn.Put(records, K($"rec:{id - 30:D8}"), Json(id - 30, 2));
                        long gone = id - 25;
                        if (txn.Delete(records, K($"rec:{gone:D8}")))
                        {
                            using var cur = txn.CreateCursor(typeIdx);
                            if (cur.TryGet(CursorOp.GetBoth, K("ticket"), K($"{gone:D8}"), out _, out _))
                                cur.DeleteCurrent();
                            using var cur2 = txn.CreateCursor(refA);
                            if (cur2.TryGet(CursorOp.GetBoth, K($"{gone % 25:D4}"), K($"{gone:D8}"), out _, out _))
                                cur2.DeleteCurrent();
                            txn.Delete(refB, K($"{gone:D8}"));
                        }
                    }
                    txn.Commit();
                }

                stopScans.Cancel();
                Task.WaitAll(scanners);

                env.Dispose();
                var report = LmdbIntegrityChecker.Check(path);
                var bad = report.Findings.Where(f => f.Severity != IntegritySeverity.Info).ToList();
                if (bad.Count > 0)
                {
                    Console.WriteLine($"cycle {cycle}: WALKER FINDINGS\n" + string.Join("\n", bad));
                    return 1;
                }
                env = Open();
                if ((cycle + 1) % 10 == 0)
                    Console.WriteLine($"cycle {cycle + 1}/{cycles} clean (last_pg={env.Info.LastPgno})");
            }

            env.Dispose(); env = Open();
            var final = LmdbIntegrityChecker.Check(path);
            var leaks = final.Findings.Where(f => f.Code == "leaked-pages").ToList();
            if (!final.Clean || leaks.Count > 0 || failures > 0)
            {
                Console.WriteLine(final.Render());
                return 1;
            }
            Console.WriteLine("APP-SHAPED SOAK CLEAN");
            return 0;
        }
        finally { env.Dispose(); Directory.Delete(path, true); }
    }
}

// ─────────────────────────────── kill ───────────────────────────────

static class KillMode
{
    public static int Run(int iterations)
    {
        string path = "/tmp/lmdb-cs/kill-soak";
        if (Directory.Exists(path)) Directory.Delete(path, true);
        Directory.CreateDirectory(path);
        var rng = new Random(20260715);

        for (int iter = 0; iter < iterations; iter++)
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
            psi.ArgumentList.Add("worker");
            psi.ArgumentList.Add(path);
            using var proc = Process.Start(psi)!;

            long lastAcked = -1;
            var reader = new Thread(() =>
            {
                string? line;
                while ((line = proc.StandardOutput.ReadLine()) != null)
                    if (long.TryParse(line, out var n)) Interlocked.Exchange(ref lastAcked, n);
            });
            reader.Start();

            // Let the worker commit at least once before the kill window opens,
            // so every iteration verifies real durability (acked >= 0).
            while (Interlocked.Read(ref lastAcked) < 0 && !proc.HasExited)
                Thread.Sleep(10);
            Thread.Sleep(150 + rng.Next(600));
            try { proc.Kill(entireProcessTree: true); } catch { }
            proc.WaitForExit();
            reader.Join(2000);
            long acked = Interlocked.Read(ref lastAcked);

            // Stale writer locks from the killed process must not block recovery.
            var report = LmdbIntegrityChecker.Check(path);
            if (!report.Clean)
            {
                Console.Error.WriteLine($"iter {iter}: walker violations after kill (acked={acked}):\n{report.Render()}");
                return 1;
            }

            using var env = LmdbEnvironment.Open(path, new EnvOpenOptions
            { ReadOnly = false, MapSize = 1 << 26, MaxDbs = 8, ReuseFreePages = true });
            using var read = env.BeginTransaction(readOnly: true);
            var db = read.OpenDefaultDatabase();
            long counter = read.TryGet(db, "counter"u8, out var c)
                ? long.Parse(Encoding.UTF8.GetString(c)) : -1;
            if (counter < acked)
            {
                Console.Error.WriteLine($"iter {iter}: durability violation — acked commit {acked} but counter={counter}");
                return 1;
            }
            for (long i = Math.Max(0, counter - 50); i <= counter; i++)
            {
                if (!read.TryGet(db, Encoding.UTF8.GetBytes($"rec-{i % 64}"), out var v))
                    continue;   // record may have been overwritten by a later i
                var expected = Payload(ExtractStamp(v));
                if (!v.SequenceEqual(expected))
                {
                    Console.Error.WriteLine($"iter {iter}: payload corruption at rec-{i % 64}");
                    return 1;
                }
            }
            Console.WriteLine($"iter {iter}: OK (acked={acked}, counter={counter}, last_pg={env.Info.LastPgno})");
        }
        Console.WriteLine("KILL SOAK CLEAN");
        return 0;
    }

    /// <summary>Child process: commit sequenced checksummed records forever,
    /// acknowledging each durable commit on stdout. Killed by the parent.</summary>
    public static int Worker(string path)
    {
        using var env = LmdbEnvironment.Open(path, new EnvOpenOptions
        { ReadOnly = false, MapSize = 1 << 26, MaxDbs = 8, ReuseFreePages = true });

        long start = 0;
        using (var read = env.BeginTransaction(readOnly: true))
        {
            if (read.TryGet(read.OpenDefaultDatabase(), "counter"u8, out var c))
                start = long.Parse(Encoding.UTF8.GetString(c)) + 1;
        }

        for (long i = start; ; i++)
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            txn.Put(db, Encoding.UTF8.GetBytes($"rec-{i % 64}"), Payload(i));
            if (i % 7 == 0) txn.Delete(db, Encoding.UTF8.GetBytes($"rec-{(i + 31) % 64}"));
            txn.Put(db, "counter"u8, Encoding.UTF8.GetBytes(i.ToString()));
            txn.Commit();
            Console.WriteLine(i);   // ack AFTER the durable commit
            Console.Out.Flush();
        }
    }

    /// <summary>Deterministic payload for stamp i: 8-byte stamp + SHA256-derived
    /// bytes, sized to hit inline and overflow paths alternately.</summary>
    internal static byte[] Payload(long i)
    {
        int len = (i % 3 == 0) ? 3000 + (int)(i % 500) : 40 + (int)(i % 100);
        var buf = new byte[8 + len];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buf, i);
        var seedBytes = SHA256.HashData(buf.AsSpan(0, 8).ToArray());
        for (int o = 0; o < len; o++) buf[8 + o] = seedBytes[o % 32];
        return buf;
    }

    internal static long ExtractStamp(ReadOnlySpan<byte> v)
        => System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(v);
}

// ─────────────────────────────── diskfull ───────────────────────────────

/// <summary>
/// Real SIGBUS crash testing — the delivery path a full disk takes. The mmap'd
/// data file is sparse; when the filesystem cannot materialize a page under an
/// mmap store, the kernel kills the process with SIGBUS mid-write. We reproduce
/// that exact mechanism without root (no tmpfs mount available) by truncating
/// the sparse tail of the mapped file: a commit-flush store past EOF takes the
/// identical kernel path. The child seeds data, truncates its own file, then
/// keeps committing (monotonic allocation) until the flush crosses EOF and the
/// OS kills it. The parent asserts death-by-signal, restores the sparse tail,
/// and verifies the walker is clean and every ACKED commit is durable.
/// </summary>
static class DiskFullMode
{
    private const long MapBytes = 32L << 20;

    public static int Run(int iterations)
    {
        for (int iter = 0; iter < iterations; iter++)
        {
            string path = $"/tmp/lmdb-cs/diskfull-{iter}";
            if (Directory.Exists(path)) Directory.Delete(path, true);
            Directory.CreateDirectory(path);
            long slackPages = iter switch { 0 => 0, 1 => 4, _ => 16 };

            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
            psi.ArgumentList.Add("diskfull-child");
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add(slackPages.ToString());
            using var proc = Process.Start(psi)!;

            long lastAcked = -1;
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) != null)
                if (long.TryParse(line, out var n)) lastAcked = n;
            proc.WaitForExit();

            if (proc.ExitCode == 0)
            {
                Console.Error.WriteLine($"iter {iter}: child survived — truncation never bit (slack={slackPages})");
                return 1;
            }
            // Restore the sparse tail the truncation removed (a real disk-full
            // recovers by freeing space; here the space IS the file length).
            string dataFile = Path.Combine(path, "data.mdb");
            using (var fs = new FileStream(dataFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                fs.SetLength(MapBytes);

            var report = LmdbIntegrityChecker.Check(path);
            if (!report.Clean)
            {
                Console.Error.WriteLine($"iter {iter}: walker violations after SIGBUS (acked={lastAcked}):\n{report.Render()}");
                return 1;
            }

            using var env = LmdbEnvironment.Open(path, new EnvOpenOptions
            { ReadOnly = false, MapSize = MapBytes, ReuseFreePages = false });
            using (var read = env.BeginTransaction(readOnly: true))
            {
                var db = read.OpenDefaultDatabase();
                long counter = read.TryGet(db, "counter"u8, out var c)
                    ? long.Parse(Encoding.UTF8.GetString(c)) : -1;
                if (counter < lastAcked)
                {
                    Console.Error.WriteLine($"iter {iter}: durability violation — acked {lastAcked} but counter={counter}");
                    return 1;
                }
                for (long i = Math.Max(0, counter - 40); i <= counter; i++)
                {
                    if (!read.TryGet(db, Encoding.UTF8.GetBytes($"rec-{i % 64}"), out var v))
                        continue;
                    if (!v.SequenceEqual(KillMode.Payload(KillMode.ExtractStamp(v))))
                    {
                        Console.Error.WriteLine($"iter {iter}: payload corruption at rec-{i % 64}");
                        return 1;
                    }
                }
                Console.WriteLine($"iter {iter}: OK (SIGBUS exit={proc.ExitCode}, acked={lastAcked}, counter={counter})");
            }
            // The recovered environment must accept writes again.
            using (var txn = env.BeginTransaction(readOnly: false))
            {
                var db = txn.OpenDefaultDatabase();
                txn.Put(db, "post-recovery"u8, "ok"u8);
                txn.Commit();
            }
        }
        Console.WriteLine("DISKFULL SOAK CLEAN");
        return 0;
    }

    /// <summary>Child: seed + commit, truncate own sparse tail to (last_pg + 1 +
    /// slack) pages, then commit monotonically-allocated records until an mmap
    /// store past EOF draws SIGBUS. Acks each durable commit on stdout.</summary>
    public static int Child(string path, long slackPages)
    {
        // Monotonic allocation guarantees the flush crosses EOF within a few
        // commits — freelist reuse could hide below the truncation for a while.
        using var env = LmdbEnvironment.Open(path, new EnvOpenOptions
        { ReadOnly = false, MapSize = MapBytes, ReuseFreePages = false });

        long i = 0;
        for (; i < 300; i++)
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            txn.Put(db, Encoding.UTF8.GetBytes($"rec-{i % 64}"), KillMode.Payload(i));
            txn.Put(db, "counter"u8, Encoding.UTF8.GetBytes(i.ToString()));
            txn.Commit();
            Console.WriteLine(i);
            Console.Out.Flush();
        }

        long truncateTo = (long)(env.Info.LastPgno + 1 + (ulong)slackPages) * env.Info.PageSize;
        using (var fs = new FileStream(Path.Combine(path, "data.mdb"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            fs.SetLength(truncateTo);

        for (; i < 100_000; i++)
        {
            using var txn = env.BeginTransaction(readOnly: false);
            var db = txn.OpenDefaultDatabase();
            txn.Put(db, Encoding.UTF8.GetBytes($"rec-{i % 64}"), KillMode.Payload(i));
            txn.Put(db, "counter"u8, Encoding.UTF8.GetBytes(i.ToString()));
            txn.Commit();   // flush stores past EOF → SIGBUS kills us mid-commit
            Console.WriteLine(i);
            Console.Out.Flush();
        }
        return 0;   // survived: parent treats this as a harness failure
    }
}

// ─────────────────────────────── diff ───────────────────────────────

static class DiffMode
{
    public static int Run(int seeds, int ops)
    {
        try
        {
            var check = Process.Start(new ProcessStartInfo("python3", "-c \"import lmdb\"") { UseShellExecute = false });
            check!.WaitForExit();
            if (check.ExitCode != 0) { Console.WriteLine("diff: python lmdb binding unavailable — skipped"); return 0; }
        }
        catch { Console.WriteLine("diff: python3 unavailable — skipped"); return 0; }

        for (int s = 1; s <= seeds; s++)
        {
            int seed = s * 104729;
            if (!RunSeed(seed, ops)) { Console.Error.WriteLine($"diff seed {seed}: MISMATCH"); return 1; }
            Console.WriteLine($"diff seed {seed}: OK ({ops} ops)");
        }
        Console.WriteLine("DIFF CLEAN");
        return 0;
    }

    private static bool RunSeed(int seed, int opCount)
    {
        var rng = new Random(seed);
        string csPath = $"/tmp/lmdb-cs/diff-cs-{seed}";
        string pyPath = $"/tmp/lmdb-cs/diff-py-{seed}";
        string opsFile = $"/tmp/lmdb-cs/diff-ops-{seed}.txt";
        foreach (var p in new[] { csPath, pyPath })
        { if (Directory.Exists(p)) Directory.Delete(p, true); Directory.CreateDirectory(p); }

        // Generate ops: put/del on the default DB (hex-encoded), txn boundaries.
        var ops = new List<string>();
        for (int i = 0; i < opCount; i++)
        {
            if (i > 0 && rng.Next(10) == 0) { ops.Add("commit"); continue; }
            string key = $"k{rng.Next(40)}";
            if (rng.Next(4) == 0) ops.Add($"del {key}");
            else
            {
                int len = rng.Next(4) == 0 ? 2500 + rng.Next(4000) : 1 + rng.Next(120);
                var v = new byte[len]; rng.NextBytes(v);
                ops.Add($"put {key} {Convert.ToHexString(v)}");
            }
        }
        ops.Add("commit");
        File.WriteAllLines(opsFile, ops);

        // Apply to the C# engine.
        using (var env = LmdbEnvironment.Open(csPath, new EnvOpenOptions
        { ReadOnly = false, MapSize = 1 << 27, ReuseFreePages = true }))
        {
            LmdbTransaction txn = env.BeginTransaction(readOnly: false);
            foreach (var op in ops)
            {
                var parts = op.Split(' ');
                var db = txn.OpenDefaultDatabase();
                switch (parts[0])
                {
                    case "put": txn.Put(db, Encoding.UTF8.GetBytes(parts[1]), Convert.FromHexString(parts[2])); break;
                    case "del": txn.Delete(db, Encoding.UTF8.GetBytes(parts[1])); break;
                    case "commit":
                        txn.Commit(); txn.Dispose();
                        txn = env.BeginTransaction(readOnly: false);
                        break;
                }
            }
            txn.Commit(); txn.Dispose();

            var report = LmdbIntegrityChecker.Check(csPath);
            if (!report.Clean) { Console.Error.WriteLine(report.Render()); return false; }

            // Apply the same ops via python-lmdb and dump both.
            string script = """
import lmdb, sys, binascii
path, opsfile = sys.argv[1], sys.argv[2]
env = lmdb.open(path, map_size=1<<27, subdir=True)
txn = env.begin(write=True)
for line in open(opsfile):
    parts = line.strip().split(' ')
    if parts[0] == 'put': txn.put(parts[1].encode(), binascii.unhexlify(parts[2]))
    elif parts[0] == 'del': txn.delete(parts[1].encode())
    elif parts[0] == 'commit':
        txn.commit(); txn = env.begin(write=True)
txn.commit()
with env.begin() as t:
    for k, v in t.cursor():
        print(k.decode() + ' ' + binascii.hexlify(v).decode())
""";
            var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(script);
            psi.ArgumentList.Add(pyPath); psi.ArgumentList.Add(opsFile);
            using var proc = Process.Start(psi)!;
            string pyDump = proc.StandardOutput.ReadToEnd();
            string pyErr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0) { Console.Error.WriteLine($"python failed: {pyErr}"); return false; }

            var sb = new StringBuilder();
            using (var read = env.BeginTransaction(readOnly: true))
            {
                var db = read.OpenDefaultDatabase();
                using var cur = read.CreateCursor(db);
                if (cur.TryGet(CursorOp.First, default, out var k, out var v))
                    do
                    {
                        sb.Append(Encoding.UTF8.GetString(k)).Append(' ')
                          .Append(Convert.ToHexString(v).ToLowerInvariant()).Append('\n');
                    } while (cur.TryGet(CursorOp.Next, default, out k, out v));
            }
            string csDump = sb.ToString();
            if (csDump != pyDump)
            {
                File.WriteAllText($"/tmp/lmdb-cs/diff-{seed}-cs.dump", csDump);
                File.WriteAllText($"/tmp/lmdb-cs/diff-{seed}-py.dump", pyDump);
                Console.Error.WriteLine($"dumps differ — see /tmp/lmdb-cs/diff-{seed}-*.dump");
                return false;
            }
        }
        Directory.Delete(csPath, true); Directory.Delete(pyPath, true); File.Delete(opsFile);
        return true;
    }
}
