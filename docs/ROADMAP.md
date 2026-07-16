# Roadmap

Status as of 2026-07-16. The engine is at feature/perf parity with C LMDB for
the supported surface: full verify.sh battery (soaks, SIGKILL recovery, fuzz,
differential validation vs liblmdb) is the merge gate, and `scripts/bench.sh`
guards throughput as a C#-vs-native ratio per phase.

## Done recently (context)

- Performance passes bringing every measured phase to ≥ native liblmdb
  throughput (see `scripts/bench.sh` output for current ratios).
- Page spill (`mdb_page_spill` port, simplified): write txns bound their
  dirty-page memory to `EnvOpenOptions.MaxDirtyPages` (default 512 MB at 4 KB
  pages); excess pages are written into the map early and re-touched pages COW
  under fresh page numbers instead of unspilling in place.
- Benchmark regression guard in CI (`bench` job in verify.yml).

## Future work, in rough priority order

### 1. Memory-pressure and failure-mode testing

- `OutOfMemoryException` / `NativeMemory.Alloc` failure mid-txn: today the txn
  is poisoned (`Broken`) — verify no native memory leaks and no torn state on
  every allocation site, ideally with a fault-injecting allocator shim.
- Map growth semantics: a long-running reader pinned to an old snapshot while
  the writer needs to grow the map (`MDB_MAP_RESIZED` in C). Currently the map
  size is fixed at open; growing requires reopen. Decide: implement resize, or
  document the constraint.
- fsync failure during commit (disk full, EIO): the commit path should surface
  the error and leave the previous meta as the durable state — needs a test
  harness that injects sync failures (the CommitHook infrastructure is a
  starting point).
- Nested transactions do not spill (parent/child page aliasing makes early
  map writes unsafe there) — a deeply nested bulk load can still grow
  unbounded. Documented limitation; revisit if a consumer needs it.

### 2. NuGet packaging / release readiness

- Package metadata for `Lmdb` (and optionally `Lmdb.Objects`,
  `Lmdb.AspNetCore`): id, license, semver, source link, XML doc file.
- README: quick-start, perf table (from bench.sh), compatibility statement
  (file-format compatible with LMDB 0.9.x; verified by the differential
  battery and by python-lmdb reading C#-written files).
- Document deliberate divergences from C LMDB:
  - Pure-append page splits leave the left page untouched (≈100% fill for
    sequential loads; C rebuilds and splits ~50/50 at the tail).
  - Spilled pages are not unspilled in place; re-touching allocates a fresh
    page number.
  - No `MDB_WRITEMAP`-style in-place dirty pages: dirty pages are native
    buffers flushed at commit (plus spill).
- CI release workflow (tag → pack → push).

### 3. Cursor-op and flag completeness

Only worth implementing against a driving use case:

- Remaining `CursorOp` values that throw `NotSupportedException`
  (see `Cursor.cs` default arm).
- `MDB_RESERVE` (allocate value space, caller fills in) — useful for
  serializers that want to write directly into the page.
- `MDB_MULTIPLE` bulk-append for DUPFIXED databases.
- Loose pages (`mdb_page_loose`): pages allocated and freed within the same
  txn are currently routed through the free-DB; loose handling would recycle
  them immediately and shrink freelist churn (also reduces the page churn the
  spill design introduces on re-touch).
- Cursor tracking/fixup on writes by OTHER cursors of the same txn (C LMDB's
  cursor-shadowing). Today cursors other than the writing one can be left
  stale by structural changes; spill conservatively keeps their pages, but a
  split/rebalance does not re-point them.

### 4. Perf follow-ups (only if profiles demand)

- Mid-page split rebuild and per-level dirty-list page resolution are the
  remaining random-write costs; both match C LMDB's design. Going further
  means diverging from the port (e.g. per-page free-space maps).
- `Lmdb.Bench` (BenchmarkDotNet) remains for microscopic analysis;
  `Lmdb.QuickBench` is the fast harness the CI guard uses.
