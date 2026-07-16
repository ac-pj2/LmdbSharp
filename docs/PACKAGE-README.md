# LmdbSharp

A pure C# implementation of [LMDB](https://www.symas.com/lmdb) (Lightning
Memory-Mapped Database). No native dependency, no P/Invoke: the B+tree, page
format, MVCC snapshots, copy-on-write transactions, and crash-safe commit
protocol are implemented directly in C#.

## Quick start

```csharp
using Lmdb;

using var env = LmdbEnvironment.Open("./mydb",
    new EnvOpenOptions { ReadOnly = false, MapSize = 1L << 30 });

// Write
using (var txn = env.BeginWriteTransaction())
{
    var db = txn.OpenDefaultDatabase();
    txn.Put(db, "hello"u8, "world"u8);
    txn.Commit();
}

// Read (zero-copy: spans point into the memory map)
using (var txn = env.BeginTransaction(readOnly: true))
{
    var db = txn.OpenDefaultDatabase();
    if (txn.TryGet(db, "hello"u8, out var value))
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(value));
}

// Range scans
using (var txn = env.BeginTransaction(readOnly: true))
{
    var db = txn.OpenDefaultDatabase();
    using var cur = txn.CreateCursor(db);
    for (bool ok = cur.TryGet(CursorOp.SetRange, "h"u8, out var k, out var v);
         ok; ok = cur.TryGet(CursorOp.Next, default, out k, out v))
    { /* ... */ }
}
```

Duplicate-value indexes (DUPSORT/DUPFIXED) with bulk operations:

```csharp
var env = LmdbEnvironment.Open("./idx", new EnvOpenOptions
{
    ReadOnly = false,
    MainDbFlags = DatabaseFlags.DupSort | DatabaseFlags.DupFixed,
});
using var txn = env.BeginWriteTransaction();
var db = txn.OpenDefaultDatabase();

txn.PutMultiple(db, "tag"u8, packedIds, itemSize: 8);   // bulk insert (MDB_MULTIPLE)

using var cur = txn.CreateCursor(db);
if (cur.TryGet(CursorOp.GetMultiple, "tag"u8, out _, out var chunk))
    do { /* chunk = one packed page of values, zero-copy */ }
    while (cur.TryGet(CursorOp.NextMultiple, default, out _, out chunk));
```

## Compatibility

- **File format**: compatible with LMDB 0.9.x. Verified continuously by a
  differential battery that applies identical operation sequences to this
  engine and to C liblmdb and compares full dumps — including C LMDB reading
  files this engine wrote (packed DUPFIXED sub-trees included).
- **Platforms**: any platform with `MemoryMappedFile` support; multi-process
  access uses an LMDB-compatible lock file.
- A few internal behaviors deliberately diverge from the C implementation
  without affecting the file format's validity — see `docs/DIVERGENCES.md`
  in the repository.

## Performance

Benchmarked continuously against native liblmdb (same machine, same
workload, 1M keys); this engine meets or exceeds native throughput on every
phase, with sequential bulk loads ~5× faster and bulk duplicate reads
(`GetMultiple`) reaching ~1.3B values/s. See `scripts/bench.sh` in the
repository for the current numbers on your hardware.

## Reliability

The merge gate for every engine change: 200+ unit/regression tests, a
randomized model-checked soak with an integrity walker after every commit,
SIGKILL and SIGBUS (disk-full delivery path) crash-recovery soaks,
allocation-failure sweeps with native-leak accounting, fuzzing, and the
differential validation against C LMDB described above.
