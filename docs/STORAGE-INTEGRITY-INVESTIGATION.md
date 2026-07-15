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
