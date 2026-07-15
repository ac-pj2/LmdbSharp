# Storage Integrity Investigation

## Objective

Prove and repair the managed LMDB engine's page-ownership and freelist behavior
so an authoritative application store can safely enable freed-page reuse.

Correctness is the only success criterion for this investigation. Performance
and file-size improvements come after deterministic integrity.

## Live evidence

P3 experienced two stores where scanning the `records` named database returned
non-JSON bytes. The preserved environments are in the P3 workspace:

```text
/home/devuser1/code/p3/.p3-ai-dev-live-data.corrupt-20260715-1150
/home/devuser1/code/p3/.p3-ai-dev-live-data.corrupt-20260715-1525
```

Raw inspection of the second environment established:

- newest main database root: page 4;
- `records` named-database root: page 4;
- scanning `records` returned named sub-database metadata;
- only one process had that data directory open;
- the application failure was therefore downstream of page/root aliasing, not
  JSON serialization or view rendering.

Do not edit these environments. Work from copies when developing diagnostics.

## Current safety boundary

Branch `agent/harden-storage-integrity` adds `ReuseFreePages`. P3 sets it to
`false`, skips freelist loading/saving, and allocates pages monotonically.

This branch also fixes a separate identity defect: distinct empty named
databases cannot be identified by their shared `P_INVALID` root.

Monotonic allocation is a containment measure, not a freelist repair. Do not
recommend that P3 enable reuse until the root cause has a deterministic failing
test and verified fix.

## Existing checks

```bash
dotnet test tests/Lmdb.Tests/Lmdb.Tests.csproj
```

The suite currently passes 87 tests, including:

- distinct empty named databases written in one transaction;
- monotonic `records` and `sequences` databases across 100 process-level
  environment reopen cycles;
- existing freelist churn, reopen, concurrency, fuzz, and native cross-check
  coverage.

P3 additionally passes continuous full scans during 300 concurrent hierarchy
writes and 100 object-database restart/write/scan cycles. Those tests do not
reproduce the historical freelist corruption on a fresh environment.

## Findings (2026-07-15)

### Tooling

`LmdbIntegrityChecker` (`src/Lmdb/Integrity.cs`, `lmdbtool <env> --check`) walks
a data file read-only — both meta snapshots, free-DB, main DB, named sub-DBs,
DUPSORT sub-trees, overflow chains — and reports duplicate page ownership,
reachable-and-free pages, duplicate freelist IDs, invalid references, and
metadata/tree disagreements. It never opens the environment or lock file.

### Preserved-environment evidence

Running the walker over copies of both preserved environments:

- **corrupt-1150**: in BOTH meta snapshots the main root and the free-DB root
  alias the same page (11 at txn 7, 13 at txn 8). The "free-DB" walk returns
  main-DB keys (`records`, `sequences`, `records:ref:*`), so freelist records
  were parsed from main-DB nodes.
- **corrupt-1525**: meta 1 (txn 9) holds a free-DB record keyed txn 8 whose IDL
  lists pages 4, 5 and 6 **twice each** — duplicate page IDs persisted into the
  freelist. Meta 0 (txn 10) then shows the fallout after those duplicates were
  reallocated: main root == `records` root == page 4 (the aliasing P3 observed),
  and the `sequences` root both reachable and freelisted.

Earliest contradictory invariant: duplicate page IDs inside a single freelist
record — pages were freed twice, then handed out twice.

### Root causes (each pinned by a regression failing before its fix)

1. **Freelist re-merge across abort / no-write commit**
   (`FreelistIntegrityTests`). `LoadPgHead` merged consumed free-DB records into
   the environment-level pool, but only a written commit deleted the records.
   After an abort or an empty commit, the next transaction merged the same
   records again → duplicate page IDs in the pool → one physical page allocated
   to two logical B-tree pages. Fixed by transaction-scoped consumption
   (`PgHeadLocal`/`PgLastLocal`, published to `Env.PgHead` only after
   `FreelistSave` deletes the records).
2. **Named-database reopen double-free** (`FreelistIntegrityTests`). Opening an
   already-open named DB in the same write transaction re-read the stale
   committed record; the second handle COW'd and freed the superseded root
   again, and writeback dropped the first handle's changes. Requires no abort —
   this is the likely seed of the persisted duplicate freelist record in
   corrupt-1525 (P3's index maintenance opens `records:*` databases repeatedly
   per transaction). Fixed: reopen returns a handle sharing the transaction's
   existing mutable record.
3. **DUPSORT delete mutated the committed snapshot in place**
   (`DupSortSnapshotIsolationTests`). `DeleteCurrent` used an xcursor positioned
   before `TouchPath`, so dup deletions were written into the pre-COW page —
   editing the durable snapshot under any live reader — while the COW copy kept
   the entry. Historically this bug and (2) masked each other in the Objects
   layer. Fixed by re-initializing the xcursor against the dirty leaf and
   COW-ing sub-DB dup paths; cursor-level `Put`/`Delete`/`DeleteCurrent` now set
   `Written` so raw-cursor commits are not silently dropped.

### Integrity gates (always on)

- dirty-page list rejects duplicate allocations (sorted insert; also fixes
  binary-search breakage when reused low pages interleave with fresh
  multi-page allocations);
- `FreelistSave` refuses to persist a record containing duplicate or
  out-of-range page IDs;
- `LoadPgHead` refuses a reusable pool containing duplicates.

A recurrence now fails the transaction loudly instead of writing corruption.
The gates cannot repair the preserved environments — pre-existing tree aliasing
is only detectable by the offline walker.

### Verification so far

- 97 Lmdb.Tests + 25 Lmdb.Objects.Tests + 44 LiveView.Tests pass;
- randomized soak: 60 seeds × 80 transactions with reuse enabled (aborts,
  no-write commits, nested transactions, held readers, environment reopens,
  overflow values) — walker-clean after every commit, shadow-model consistent;
- in-suite seeded soak (`FreelistSoakTests`) runs three seeds per test run.

### Remaining before recommending reuse to P3

- P3-side soak with `ReuseFreePages = true` (vertical + stress suites, then a
  supervised live period);
- crash-point tests around dirty-page flush, sub-DB writeback, freelist
  persistence and alternate meta publication (priority 7 — not yet built);
- native cross-validation of reuse-enabled environments beyond the existing
  `NativeLmdb` coverage.

Known non-corruption defect (space leak only): updating a value that lived on
overflow pages never frees the old overflow chain (`Cursor.Write.Put` update
path); deletes free it correctly.

## Investigation priorities

1. Build a read-only integrity walker that reports page ownership from both
   meta snapshots through main, free, and every named database.
2. Detect duplicate page ownership, reachable-and-free pages, duplicate IDs in
   freelist records/PgHead, invalid page references, and metadata/tree count
   disagreements.
3. Run the walker against copies of both preserved environments and identify
   the earliest contradictory invariant available from their two meta pages.
4. Instrument allocation and freeing with transaction ID, database identity,
   page ID, and ownership transitions. Assert uniqueness before commit.
5. Generate deterministic randomized workloads covering named databases,
   splits, rebalance, deletion, reopen, concurrent readers, and long-lived
   readers. Persist seeds and shrink every failure.
6. Differentially validate databases with native LMDB tools and the existing
   `NativeLmdb`/`LmdbCompare` infrastructure.
7. Add crash-point tests around dirty-page flush, sub-database writeback,
   freelist persistence, and alternate meta publication.

## Hypotheses to test, not assume

- duplicate page IDs enter a freelist record or `PgHead` and are allocated twice;
- a page is freed twice during split/rebalance or named-database main-tree churn;
- consumed freelist records and unconsumed `PgHead` entries diverge across an
  environment lifetime or restart;
- reader registration or oldest-reader calculation permits premature reuse;
- named sub-database record writeback frees or republishes an incorrect root;
- alternate meta snapshots expose a transition that current tests do not model.

## Required result

A repair is complete only when it includes:

- a deterministic regression that fails before the fix;
- an explanation of the violated page-ownership invariant;
- integrity assertions that would catch recurrence before commit;
- extended randomized, restart, concurrency, crash, and native compatibility
  verification;
- a clean soak using P3 as an external consumer.

Passing the pre-existing suite or failing to reproduce the issue is not evidence
that freed-page reuse is safe.
