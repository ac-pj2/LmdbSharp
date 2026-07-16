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

### 1. Memory-pressure and failure-mode testing — DONE (2026-07-16), remainder below

Covered now:

- All native allocations route through the `Mem` shim (fail-injection +
  per-thread leak accounting). `FaultInjectionTests` sweeps an OOM through
  ~150 fail points across a full txn lifecycle asserting zero leaks, clean
  integrity, and pre-or-post (never torn) state.
- fsync failure (`SyncFailureTests`): data-sync failure aborts the txn and the
  env stays usable; meta-sync failure sets the env `Panicked` (C LMDB's
  MDB_FATAL_ERROR) — write txns are refused until reopen, reads stay allowed.
- Map exhaustion (`MapFullRecoveryTests`): MDB_MAP_FULL is a clean recoverable
  error, and reopening with a larger `MapSize` now actually grows the map (the
  open path previously discarded the requested size in favor of the meta's).
- Nested txns don't spill but stay correct (tested).

Also covered (2026-07-16, second pass):

- Cross-process map growth: both txn-begin paths already threw
  `MDB_MAP_RESIZED` when another process grew the map past this env's view;
  recovery no longer requires a reopen — `LmdbEnvironment.SetMapSize(0)`
  adopts the on-disk size (C's `mdb_env_set_mapsize` contract), guarded by a
  live-transaction counter and clamped so it can never shrink below committed
  data (`MapResizeTests`).
- Real SIGBUS crash testing (`Lmdb.Soak diskfull`, in verify.sh): a child
  process truncates the sparse tail of its own mapped file — the identical
  kernel delivery path a full filesystem takes on an mmap store — and commits
  until the flush crosses EOF and the OS kills it mid-commit. The parent
  verifies death-by-signal, walker-clean recovery, durability of every acked
  commit, and post-recovery writability. (A mounted-tmpfs ENOSPC variant needs
  root/user-namespaces, unavailable in this container; the SIGBUS mechanism
  and crash window are the same.)

### 2. NuGet packaging / release readiness — DONE (2026-07-16)

- `LmdbSharp` package (assembly `Lmdb`, net8.0/net10.0): full metadata, XML
  docs, embedded PDB + untracked sources, package README; validated by a
  local-feed install + runtime smoke test.
- License resolved: the engine ships under OLDAP-2.8 (it is a port; the
  README already declared this) — overriding the MIT default in
  src/Directory.Build.props, which still covers the non-derived layers.
  LICENSE file documents both.
- README refreshed: package install/quick-start, current 1M-key perf table,
  compatibility statement, updated feature matrix and test counts.
- docs/DIVERGENCES.md documents every deliberate divergence from C LMDB.
- release.yml: push a v* tag → test, pack (version from tag), push to
  nuget.org when NUGET_API_KEY is configured, attach the nupkg to a GitHub
  release. Publishing to Lmdb.Objects/AspNetCore/LiveView packages remains
  future work if those layers are meant to ship.

### 3. Cursor-op and flag completeness

Only worth implementing against a driving use case:

- Remaining `CursorOp` values that throw `NotSupportedException`
  (see `Cursor.cs` default arm).
- ~~`MDB_RESERVE`~~ — DONE (2026-07-16): `PutReserve` returns a writable span
  into the page (inline or overflow); incompatible with DUPSORT (as in C).
- ~~`MDB_MULTIPLE`~~ — DONE (2026-07-16): `PutMultiple` stores packed
  fixed-size dups in one call, keeping the dup cursor warm (LEAF2 append
  path): 1M ascending dups ~49M values/s, 5-10x over per-value puts.
- ~~LEAF2 sub-DB storage~~ — DONE (2026-07-16): DUPFIXED dup sub-trees now use
  packed LEAF2 pages end to end (C format parity, validated by the dupfixed
  differential which has C LMDB read C#-written files directly). Bulk reads
  are zero-copy everywhere; 1M 8-byte dups: 2.5× denser, GetMultiple ~1.3B
  values/s (22× over NextDup).
- ~~Loose pages (`mdb_page_loose`)~~ — DONE (2026-07-16): dirty pages freed
  within their own txn are recycled immediately (same pgno + buffer via the
  dirty list); leftovers join the free list at commit and their buffers are
  never flushed. Fill/clear cycles inside one txn no longer grow the file per
  cycle.
- ~~Cursor tracking/fixup (cursor shadowing)~~ — DONE (2026-07-16): all
  same-txn cursors are adjusted across COW, insert/delete slides (C_DEL),
  splits, merges, root growth/collapse, and dup-storage shape conversions
  (value-preserving re-seek, stronger than C's teleport). Validated by a
  randomized successor/predecessor oracle against a shadow model.

### 4. Perf follow-ups (only if profiles demand)

- Mid-page split rebuild and per-level dirty-list page resolution are the
  remaining random-write costs; both match C LMDB's design. Going further
  means diverging from the port (e.g. per-page free-space maps).
- `Lmdb.Bench` (BenchmarkDotNet) remains for microscopic analysis;
  `Lmdb.QuickBench` is the fast harness the CI guard uses.
