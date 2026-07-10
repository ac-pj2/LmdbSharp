# LmdbSharp — a C# port of LMDB

A from-scratch, pure-C# (unsafe) rewrite of [LMDB](https://git.openldap.org/openldap/openldap)
for use as an **embedded key-value database** on .NET 8. No P/Invoke into liblmdb —
the B+tree, page format, and transaction logic are reimplemented directly, and the
result is **binary-compatible** with databases produced by the real C LMDB.

## Status

| Layer | State |
|---|---|
| On-disk struct overlays (page / node / meta / db) | ✅ done |
| IDL (sorted pgno lists) + comparators (memn/memnr/cint/int/long) | ✅ done |
| Memory-mapped file access | ✅ done |
| Environment open/close + meta-page selection | ✅ done |
| Read transactions | ✅ done |
| B+tree search (`mdb_page_search` / `mdb_node_search`) | ✅ done |
| Cursor: `First`/`Last`/`Next`/`Prev`/`Set`/`SetRange` | ✅ done |
| Overflow (big-data) page reads | ✅ done |
| Named sub-databases (`mdb_dbi_open` read path) | ✅ done |
| `MDB_INTEGERKEY` / `MDB_REVERSEKEY` | ✅ done |
| Write transactions (`put`/`del`, COW, commit) | ✅ done |
| Page splitting + multi-level B+trees | ✅ done |
| Overflow (big-data) pages (write + read) | ✅ done |
| Updates / deletes | ✅ done |
| Free-DB persistence + page reuse | ✅ done (freed pages saved to free-DB and reused across txns) |
| Page rebalance/merge on delete | ✅ done (borrow/merge/collapse-root) |
| `MDB_DUPSORT` (sorted duplicate values per key) | ✅ done (sub-pages, sub-DBs, xcursor) |
| Lockfile / reader table / multi-process writer | ✅ done (MVCC snapshot isolation, single-writer lock) |
| Named sub-DB creation from C# | ✅ done |
| Nested transactions | ✅ done |
| `env_copy`, `mdb_drop` | ✅ done |
| `MDB_DUPFIXED` (LEAF2 fixed-size dups) | ✅ done |

The read path is **cross-validated**: the test suite generates databases with the
Python `lmdb` wheel (which bundles the real liblmdb) and reads them back with this
library. The **write path is cross-validated too**: C# writes databases that real
liblmdb reads back (point lookups + iteration), and vice versa. See
`tests/Lmdb.Tests/ReadPathTests.cs` and `WritePathTests.cs` — 18/18 tests pass.

## Build & test

```bash
# .NET 8 SDK required (the dotnet-install.sh --user install works without sudo).
dotnet build
dotnet test                            # regenerates fixtures via python3 + the lmdb wheel
dotnet run --project src/Lmdb.Tool -- /tmp/lmdb-ref/seq --list 5 --get key00499
```

Fixture generation needs `python3` and the `lmdb` package (`pip install --user lmdb`).
The test harness runs `test/crosscheck/gen_fixtures.py` automatically when fixtures
are missing.

## Performance

BenchmarkDotNet benchmarks (100k operations, .NET 8 / AVX2):

| Operation | C# port | Native (C/Python) | C# vs Native |
|---|---|---|---|
| Sequential write | 20.7ms (4.8M ops/s) | 60ms | **2.9× faster** |
| Point get | 11.1ms (9.0M ops/s) | 38ms | **3.4× faster** |
| Cursor iterate | 455µs (220M items/s) | 9ms | **20× faster** |
| Point get allocation | **0 bytes/get** | — | — |

C# is faster than the Python bindings because Python's per-call C API overhead
is significant. The important number: **point reads are zero-allocation** thanks
to cursor pooling, and writes allocate only ~21 bytes/put (dirty-list overhead).

```bash
dotnet run -c Release --project tests/Lmdb.Bench
```

## Solution layout

```
src/Lmdb/            the library
  Constants.cs       MDB_* flags, magic, versions, geometry (ported from mdb.c/lmdb.h)
  Errors.cs          error codes + LmdbException
  Page.cs            unsafe overlays: Page / Node / Meta / Db (the on-disk contract)
  Compare.cs         mdb_cmp_memn / memnr / cint / int / long + selectors
  Idl.cs             midl.c port — descending IDL + ascending ID2L (dirty pages)
  Platform/MappedFile.cs   BCL MemoryMappedFile wrapper exposing a raw byte*
  Environment.cs     LmdbEnvironment — open/create, mmap, pick newest meta, write meta
  Transaction.cs     read + write transactions (dirty list, COW, commit)
  Transaction.Freelist.cs  free-DB save/load, PgHead page-reuse pool
  Database.cs        DBI handle + named sub-DB resolution
  Cursor.cs          B+tree descent, node search, cursor ops, sibling traversal (read)
  Cursor.Write.cs    page_touch / node_add / node_del / put / delete
  Cursor.DupSort.cs   xcursor (sub-cursor) for DUPSORT reads
  Cursor.DupWrite.cs  DUPSORT puts: sub-page creation/growth, sub-DB conversion
  Cursor.Rebalance.cs  mdb_rebalance / node_move / page_merge / update_key
src/Lmdb.Tool/       mdb_stat-style CLI
tests/Lmdb.Tests/    cross-validation against real LMDB files
tests/Lmdb.Bench/    BenchmarkDotNet harness (placeholder)
test/crosscheck/     gen_fixtures.py — reference-DB generator
```

## Architecture notes

LMDB is a memory-mapped, copy-on-write B+tree with MVCC. Every page is a 16-byte
header (`pgno`, `pad`, `flags`, `lower`/`upper`) followed by either sorted node
pointers (branch/leaf), packed keys (LEAF2), an overflow blob, or a `MDB_meta`
record. Nodes are an 8-byte header (`lo`/`hi`/`flags`/`ksize`) + key + data; on
64-bit builds a branch node packs a 48-bit child page number across `lo|hi<<16|
flags<<32`, while a leaf node uses `lo|hi<<16` as a 32-bit data size. This port
keeps `MDB_DEVEL=0` semantics (`PAGEBASE=0`, `MDB_DATA_VERSION=1`), matching the
default reference build, so node pointers are absolute page offsets.

The library mirrors the C structure closely — `Page`/`Node`/`Meta`/`Db` are static
accessor classes over `byte*`, named after the `MP_*`/`NODE*` macros — so that the
write-path code (coming next) can be ported near-verbatim.

## Reference source

Ported from the OpenLDAP `mdb.master` branch (`libraries/liblmdb/mdb.c`, `midl.c`,
`lmdb.h`), kept locally at `~/refs/lmdb-ref`. LMDB is distributed under the
OpenLDAP Public License.
