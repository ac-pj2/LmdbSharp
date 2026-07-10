# LmdbSharp — Pure C# LMDB + Object Database + LiveView

A from-scratch, pure-C# (unsafe) rewrite of [LMDB](https://git.openldap.org/openldap/openldap)
used as the foundation for an **ultra-fast embedded object database** with a
**LiveView-style real-time web framework**. No P/Invoke into liblmdb — the B+tree,
page format, and transaction logic are reimplemented directly.

## What's in this repo

| Project | Description |
|---|---|
| `src/Lmdb` | Pure C# LMDB engine (B+tree, MVCC, COW, DUPSORT, DUPFIXED) |
| `src/Lmdb.Objects` | Object database: typed collections, indexes, LINQ, serialization |
| `src/Lmdb.AspNetCore` | ASP.NET Core DI integration |
| `src/Lmdb.LiveView` | Server-side rendering + WebSocket DOM diff framework |
| `samples/TodoApi` | REST API sample (CRUD + indexes) |
| `samples/LiveTodo` | Collaborative real-time todo app (WebSocket + LiveView) |
| `tests/Lmdb.Tests` | 83 tests incl. differential fuzzing vs real LMDB |
| `tests/Lmdb.Objects.Tests` | 25 object database tests |

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│                    Browser (vanilla JS, ~2KB)                 │
│                   WebSocket ←→ DOM patches                    │
└──────────────────────────┬───────────────────────────────────┘
                           │
┌──────────────────────────▼───────────────────────────────────┐
│                   Lmdb.LiveView                               │
│   DeltaLiveView → RenderTree() → HtmlDiff → WebSocket         │
│   In-memory state, delta broadcasts, zero DB reads on render  │
└──────────────────────────┬───────────────────────────────────┘
                           │
┌──────────────────────────▼───────────────────────────────────┐
│                   Lmdb.Objects                                │
│   Collection<T> → indexes → LINQ → batch reads               │
│   MemoryPack serialization, schema versioning, async wrapper  │
└──────────────────────────┬───────────────────────────────────┘
                           │
┌──────────────────────────▼───────────────────────────────────┐
│                   Lmdb (pure C# LMDB engine)                  │
│   Memory-mapped B+tree, COW, MVCC, DUPSORT, DUPFIXED         │
│   Free-DB page reuse, crash recovery, lockfile/reader table   │
└──────────────────────────────────────────────────────────────┘
```

## Quick start

```csharp
using Lmdb;
using Lmdb.Objects;

// Open an object database
using var db = ObjectDatabase.Open("./mydb");

var users = db.GetCollection<User>("users");

// Write (auto-commit transaction)
users.Insert(new User { Name = "Alice", Email = "alice@x.com" });

// Read (zero-allocation hot path via ReadOnlySpan<byte>)
using var txn = db.BeginRead();
var user = users.Get(txn, 1L);

// LINQ query with index scan
var results = users.Query(txn)
    .Where(x => x.Age >= 18)
    .OrderByDescending(x => x.Score)
    .Take(10)
    .ToList();
```

### LiveView (collaborative real-time)

```csharp
// Program.cs
builder.Services.AddLmdbObjectDatabase("./mydata");
builder.Services.AddCollection<Todo>("todos");
var app = builder.Build();
app.UseWebSockets();

// LiveView: server renders HTML, sends DOM diffs over WebSocket
app.MapGet("/ws", async (ctx) => {
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await hub.HandleConnectionAsync(ws, "TodoLiveView");
});
```

## Performance

### LMDB engine vs native liblmdb (P/Invoke), 100k operations

| Operation | C# port | Native (P/Invoke) | Winner |
|---|---|---|---|
| Cursor scan | 311µs (321M items/s) | 549µs | **C# 1.77× faster** |
| Point get | 11.6ms (8.6M ops/s) | 13.1ms | **C# 1.13× faster** |
| Write | 17.6ms (5.7M ops/s) | 15.8ms | Native 1.11× faster |
| Point get allocation | **0 bytes/get** | — | — |

C# is faster than P/Invoke on reads because there's no marshalling overhead.
The pure C# port does everything in managed memory with zero allocation on
the read hot path.

### LiveView render + diff

| Items | Full Render | Toggle (re-render + diff) |
|---|---|---|
| 10 | 13µs | 63µs |
| 100 | 114µs | 503µs |
| 1000 | 680µs | 858µs |
| 5000 | 3.1ms | 10.8ms |

Toggle of one item in a 1000-item list: **858µs** — well within the 16ms
frame budget for 60fps.

## Feature status

| Feature | Status |
|---|---|
| Read path (get, cursor, overflow, sub-DBs) | ✅ |
| Write path (put/del/split/commit) | ✅ |
| Free-DB + page reuse | ✅ |
| Page rebalance/merge | ✅ |
| DUPSORT + DUPFIXED (LEAF2) | ✅ |
| Lockfile / MVCC / multi-process | ✅ |
| Nested transactions | ✅ |
| Crash recovery (kill -9 verified) | ✅ |
| Differential fuzzing (vs real LMDB) | ✅ 25 cases, 9400 ops, zero mismatches |
| Typed collections + auto-ID | ✅ |
| Secondary indexes (DUPSORT) | ✅ |
| LINQ query provider | ✅ (Where, OrderBy, Take, Skip, range, prefix) |
| Async wrapper | ✅ |
| Schema versioning | ✅ |
| Batch reads | ✅ |
| ASP.NET Core DI | ✅ |
| LiveView (WebSocket DOM diffs) | ✅ |
| Delta broadcasts (zero DB reads) | ✅ |

## Build & test

```bash
# .NET 10 SDK required
dotnet build
dotnet test                            # 108 tests, incl. fuzzing vs real LMDB

# Run the LiveTodo sample
dotnet run --project samples/LiveTodo -- TodoDbPath=/tmp/todos

# Benchmarks
dotnet run -c Release --project tests/LmdbCompare      # vs native liblmdb
dotnet run -c Release --project tests/LiveViewBench    # render + diff
```

Test fixtures are generated by `test/crosscheck/gen_fixtures.py` using the
Python `lmdb` wheel (which bundles the real C LMDB). The differential fuzzer
runs the same operations through both this C# port and Python, asserting
identical results.

## Solution layout

```
src/Lmdb/                  the LMDB engine (4,690 lines)
  Constants.cs             MDB_* flags, magic, versions, geometry
  Errors.cs                error codes + LmdbException
  Page.cs                  unsafe overlays: Page / Node / Meta / Db
  Compare.cs               comparators (memn/memnr/cint/int/long)
  Idl.cs                   midl.c port — IDL + ID2L (dirty pages)
  Platform/MappedFile.cs   BCL MemoryMappedFile wrapper
  Platform/Lockfile.cs     reader table + writer mutex
  Environment.cs           open/create, mmap, meta selection, write meta
  Transaction.cs           read + write transactions (dirty list, COW, commit)
  Transaction.Freelist.cs  free-DB save/load, PgHead page-reuse pool
  Transaction.Nested.cs    child transactions
  Transaction.SubDb.cs     named sub-DB write-back at commit
  Transaction.DbCache.cs   per-txn DB handle cache
  Transaction.Drop.cs      mdb_drop
  Environment.Copy.cs      env_copy
  Database.cs              DBI handle + named sub-DB resolution
  Cursor.cs                B+tree descent, node search, cursor ops (read)
  Cursor.Write.cs          page_touch / node_add / node_del / put / delete
  Cursor.Split.cs          mdb_page_split
  Cursor.Rebalance.cs      mdb_rebalance / node_move / page_merge
  Cursor.DupSort.cs        xcursor for DUPSORT reads
  Cursor.DupWrite.cs       DUPSORT puts: sub-page, sub-DB conversion
  PublicApi.cs             C#-idiomatic API (strings, Scan, scope commit)
src/Lmdb.Objects/          object database layer
  Serializer.cs            IObjectSerializer<T> (MemoryPack, JSON)
  KeyEncoding.cs           long/string/Guid/DateTime key encoding
  Collection.cs            typed CRUD, indexes, LINQ, batch reads
  ObjectDatabase.cs        entry point + index management
  AsyncWrapper.cs          Task-based async APIs
  LinqQuery.cs             ObjectQuery<T> (Where, OrderBy, Take, Skip)
  SchemaVersioning.cs      VersionedSerializer<T>
src/Lmdb.AspNetCore/       DI integration (AddLmdbObjectDatabase, AddCollection)
src/Lmdb.LiveView/         LiveView framework
  HtmlNode.cs              DOM tree model + HTML parser
  HtmlDiff.cs              tree diff → JSON patches (attr/text/replace/insert/remove)
  DeltaLiveView.cs         base class: state, RenderTree, PushUpdate, BroadcastDelta
  DeltaLiveView.Incremental.cs  incremental patch API (EmitPatches, FindByKey)
  LiveViewHub.cs           WebSocket manager, delta broadcasts
  ClientRuntime.cs         ~2KB vanilla JS client
samples/TodoApi/           REST API sample
samples/LiveTodo/          collaborative real-time todo app
```

## License

LMDB is distributed under the OpenLDAP Public License. This C# port is
provided under the same terms.

## Reference source

Ported from the OpenLDAP `mdb.master` branch (`libraries/liblmdb/mdb.c`,
`midl.c`, `lmdb.h`).
