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
| `tests/LiveView.Tests` | 15 diff-engine tests (escaping, stable IDs, memoization) |

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│                    Browser (vanilla JS, ~4KB inlined)         │
│  SSR first paint → WebSocket (permessage-deflate) ←→ patches  │
│  id→element map, rAF batching, focus-safe, backoff reconnect  │
└──────────────────────────┬───────────────────────────────────┘
                           │
┌──────────────────────────▼───────────────────────────────────┐
│                   Lmdb.LiveView                               │
│   DeltaLiveView → RenderTree() (memoized) → HtmlDiff → WS     │
│   Stable node IDs, per-session mailbox, delta broadcasts,     │
│   in-memory state, zero DB reads on render                    │
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
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

// One line: WS endpoint (permessage-deflate), client runtime at /ws/client.js
var hub = app.MapLiveView<TodoLiveView>("/ws", name => new TodoLiveView(...));

// SSR first paint: embed the rendered view + its fingerprint in the page.
// On WS connect the client echoes the fingerprint; if state is unchanged
// the server replies {"t":"ok"} instead of re-sending the HTML.
app.MapGet("/", () => {
    var ssr = hub.RenderInitialHtml("TodoLiveView");
    return Results.Content(Page(ssr, DeltaLiveView.Fingerprint(ssr)), "text/html");
});
```

Inside a view, memoize list rows so re-render + diff cost is proportional
to what changed, not to the page size (the differ skips reference-equal
subtrees):

```csharp
foreach (var item in visible)
    ul.Children.Add(Memo(item.Id, (item.Title, item.Completed), () => BuildRow(item)));
```

Pure-UI interactions (menus, panels, tabs) can skip the server entirely
with client-side commands — instant, zero network:

```html
<button data-client="toggle #menu">☰</button>
<button data-client="class open #nav; focus #search">Search</button>
```

Verbs: `toggle`/`show`/`hide <sel>` (hidden property), `class`/`addclass`/
`removeclass <cls> <sel>`, `focus <sel>`; chain with `;`, target `this` for
the element itself. Local changes are keyed by node and re-applied when
server patches touch the same elements, so a locally-opened menu survives
unrelated updates. An element can combine `data-client` (instant) with
`data-event` (server round trip). Mark client-owned DOM (charts, widgets)
with `data-lv-ignore` — patches skip its children.

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

### LiveView render + diff (toggle one item in a list)

| Items | Full render | Re-render + diff | Memoized re-render + diff |
|---|---|---|---|
| 10 | 11µs | 32µs | 12µs (0 B alloc) |
| 100 | 90µs | 356µs | 15µs |
| 1000 | 891µs | 2.6ms | **60µs** |
| 5000 | 2.4ms | 4.1ms | **206µs** |

With memoized rows (`Memo(key, version, build)`), unchanged rows are the
same tree instance and the differ skips them by reference — cost tracks
*what changed*, not page size. Toggling one item in a 5000-row list costs
206µs and 137KB instead of 4.1ms and 12MB (i5-13500, short-run job).

### Wire protocol

- Patches are minimal JSON ops (`attr`/`text`/`insert`/`remove`/`replace`)
  targeting **stable node IDs** — sibling inserts never renumber the page.
- `permessage-deflate` on the socket (patch JSON compresses 5–10×).
- First paint is server-rendered into the page; on WS connect the client
  echoes the SSR fingerprint and, if state is unchanged, the server sends
  a ~12-byte `ok` instead of the full HTML (Phoenix-style "connected render").

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
| LiveView (WebSocket DOM diffs, stable IDs) | ✅ |
| Delta broadcasts (zero DB reads) | ✅ |
| SSR first paint + fingerprint join | ✅ |
| Subtree memoization (O(changed) diffs) | ✅ |
| XSS-safe rendering (escaped text/attrs) | ✅ |
| Per-session mailbox (thread-safe views) | ✅ |
| Slow-client backpressure (auto resync) | ✅ |
| Client: id map, rAF batching, focus-safe patches | ✅ |
| Client: backoff reconnect, heartbeat, lv-busy states | ✅ |
| Client-side commands (data-client, zero round trip) | ✅ |
| Client-owned DOM zones (data-lv-ignore) | ✅ |

## Build & test

```bash
# .NET 10 SDK required
dotnet build
dotnet test                            # 123 tests, incl. fuzzing vs real LMDB

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
  HtmlNode.cs              DOM tree model + HTML parser (entity-decoding)
  HtmlDiff.cs              stable-ID tree diff → UTF-8 JSON patches, escaped render
  DeltaLiveView.cs         base class: state, RenderTree, Memo, PushUpdate, broadcasts
  LiveViewHub.cs           WebSocket manager, per-session loops, SSR, fingerprint join
  ClientRuntime.cs         ~4KB vanilla JS client (id map, rAF, focus-safe, reconnect)
samples/TodoApi/           REST API sample
samples/LiveTodo/          collaborative real-time todo app (SSR + memoized rows)
tests/LiveView.Tests/      diff engine tests (escaping, stable IDs, fallbacks, memo)
```

## License

LMDB is distributed under the OpenLDAP Public License. This C# port is
provided under the same terms.

## Reference source

Ported from the OpenLDAP `mdb.master` branch (`libraries/liblmdb/mdb.c`,
`midl.c`, `lmdb.h`).
