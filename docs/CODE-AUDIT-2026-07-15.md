# Code Audit — 2026-07-15

Systematic correctness review of the whole engine (three parallel deep reviews:
rebalance/split, DUPSORT/drop, transaction/environment/locking), following the
freed-page-reuse investigation. Every fix must land with a regression that
fails first. Status: `open` / `fixed <commit>` / `accepted` (documented
limitation).

**Status 2026-07-15 (end of day): every `open` item below is FIXED** — each with
a regression that failed first, on branch `agent/harden-storage-integrity`.
Additional defects found and fixed during verification hardening: main-DB
`md_entries` never counted named-DB records; freed overflow chains never
decremented `md_overflow_pages`; plain deletes could silently destroy a named
database's record (now `Incompatible`; use `Drop`). Remaining `accepted` items
are documented deviations, not defects.

Verification at close: 139 Lmdb.Tests + 25 Objects + 44 LiveView tests; strict
soak (25 seeds × 150 txns: walker warnings fatal, per-DB entry counts vs model,
zero leaked pages, reopens/aborts/nested/held readers/dup conversions/deep
trees); kill soak (10 SIGKILLs, ~55k acked commits, zero durability
violations, file size stable at 34 pages); differential equivalence vs C LMDB
(python binding) on identical op sequences; torn-commit matrix (12 crash-point
combinations); preserved corrupt environments still correctly diagnosed.

## Severity 1 — data corruption (confirmed by reading)

| # | Area | Defect | Status |
|---|------|--------|--------|
| S1-1 | Cursor.Split `SplitParent` | Stack copy-back after recursive parent split: `_ki[ptop]` off-by-one, and when the parent split grows the root the caller's stack silently drops a level (`mn._snum == _snum` so adoption never fires). Sequential appends via the append fast path then `NodeAdd` into the wrong page/index — committed root corruption, lost entries. | fixed |
| S1-2 | Cursor.Rebalance `NodeMove` | Branch destination, insert at slot 0: C first updates dst slot 0 with its subtree's lowest key; the port never assigns the displaced node's key and then pushes an EMPTY key into the parent separator — misrouted searches, unreachable keys. | fixed |
| S1-3 | Cursor.Rebalance root collapse | Stack levels above `_snum` are not shifted down (loop bounded by `_snum` instead of depth), so after a depth-reducing merge cascade the cursor's leaf slot indexes a branch page — next same-cursor op corrupts a dirty page header. | fixed |
| S1-4 | Cursor.Rebalance `PageMerge` | `FindLowestKey` runs on the live source cursor in the `fromleft` branch and never restores clobbered stack entries above `_top` — following same-txn ops mutate the wrong leaf. C uses a copy cursor. | fixed |
| S1-5 | Cursor.DupWrite `ConvertSubPageToSubDB` | Assumes every existing dup has the NEW value's size: `stackalloc` overrun + every transferred value truncated/padded. Any non-DUPFIXED sub-page→sub-DB conversion with varied value sizes corrupts all dups. | fixed |
| S1-6 | Cursor.DupWrite `NodeAddLeaf2` | Room check `ksize-2 > SizeLeft-(ksize-2)` is wrong for ksize<4 (never full for ksize=2 → writes past the sub-page into the adjacent node) and prematurely full for ksize>4. Correct check: `SizeLeft < ksize`. | fixed |
| S1-7 | Cursor.DupWrite `PutDupSort` | No DUPSORT value-size cap and no F_BIGDATA handling: an oversized first value goes to an overflow page; the second put for the key reads the 8-byte overflow pgno as inline data (OOB read, garbage sub-page, leak). C returns MDB_BAD_VALSIZE. | fixed |
| S1-8 | Transaction (readers) | Long-lived reader pins a POINTER into meta page `T&1`; the writer's commit T+2 overwrites that page in place while the reader is live — snapshot isolation broken, torn MDB_db possible. Read txns must copy the core DB records at begin. | fixed |
| S1-9 | Database `OpenNamed` (read txns) | Resolves names against the env's CURRENT meta, not the txn's snapshot — one intervening commit gives mixed-snapshot reads. | fixed |
| S1-10 | Transaction.Nested + named DBs | (a) child-opened named DBs are never merged to the parent: child writes lost AND old root pages persisted as free while still referenced (reachable-and-free). (b) a parent-opened handle used in a child mutates the parent's record in place; child abort cannot roll it back. | fixed |
| S1-11 | Commit durability | Single flush+fsync covers data pages AND meta: OS writeback may persist meta before data → crash can leave a winning meta referencing never-written pages. Needs data-sync barrier before meta write, then meta sync. | fixed |

## Severity 2 — wrong results / drift / availability

| # | Area | Defect | Status |
|---|------|--------|--------|
| S2-1 | DUPSORT md_entries | New-key insert counted twice (`addNode` + `done:`); no-op duplicate put still counted; `DeleteCurrent` of a non-last dup never decrements; plain `Delete(key)` of a dup key decrements by 1 not N. Count drift breaks Stat/Entries consumers. | fixed |
| S2-2 | Cursor.Write `Delete` | Deleting an `F_DUPDATA|F_SUBDATA` node leaks the whole dup sub-DB tree (also the path `Drop` uses per-key). Needs mdb_drop0-style sub-tree free. | fixed |
| S2-3 | Write-txn ctor | Not exception-safe after `LockWrite` — an `AssertNoDuplicates` throw leaves the writer lock held forever. | fixed |
| S2-4 | Environment `OpenCore` | Lockfile open failure silently ignored → NOLOCK behavior (no writer mutex, readers invisible to freelist) without the user asking. Must throw unless NoLock. | fixed |
| S2-5 | Transaction | `OpenDatabase(name, Create)` with no other writes commits nothing — DB creation silently dropped (`Written` never set). | fixed |
| S2-6 | Transaction | No error flag: a structural op that throws mid-mutation (e.g. MapFull inside the update path AFTER NodeDel removed the old node) leaves a committable half-mutated txn — silent data loss if the caller catches and commits. | fixed |
| S2-7 | Lockfile | `ClaimReaderSlot` not atomic (two readers can share a slot; one release orphans the other). Reader registration also reads meta BEFORE publishing txnid (GC-pause window lets a writer recycle the snapshot's pages); slot-full (-1) silently ignored. | fixed |
| S2-8 | Reader-slot lifetime | Finalizer never releases the slot; dead-process slots never swept — `oldest` pinned forever, file grows unbounded, slots exhaust. | fixed |
| S2-9 | Multi-process | Writers never re-read meta committed by another process (`UpdateLastTxnid` written, never read) — second process overwrites the first's commits. Either implement remap-on-lock or document single-process-writer. | fixed |
| S2-10 | Environment open | `MaxPg` derived from meta mapsize but the view is mapped with `options.MapSize` — opening a big DB with small options allows out-of-view page access (AV) during commit flush. | fixed |
| S2-11 | Environment.Copy | No snapshot: copies live mmap without lock or reader slot → torn copy; pages recycled mid-copy. | fixed |
| S2-12 | Transaction.Drop | `Drop(delete:true)` frees the record while live handles still point at it (UAF on reuse/double-drop); name-pointer comparison truncates to 32 bits (`(uint)`). | fixed |
| S2-13 | Nested txns | Parent usable while child active (LMDB rejects); `CommitChild` ignores `Dirty.Insert` rc → silent dropped child page on duplicate. | fixed |

## Severity 3 — leaks / deviations / minor

| # | Area | Defect | Status |
|---|------|--------|--------|
| S3-1 | Freelist | Volatile pool remainder (`Env.PgHead`) is lost on every process exit (records already deleted) — permanent page leak per restart; kill-soak showed last_pg → map-full. Plan: persist the remainder in the free-DB each commit (retry-until-stable), drop the volatile env pool entirely. | fixed |
| S3-2 | Freelist | FreelistSave single-pass: pages COW'd by the freelist write itself leak (~1-3/commit). Folded into S3-1's retry loop. | fixed |
| S3-3 | Cursor.DupWrite | `PutDupSort` MDB_CURRENT branch: old overflow chain not freed on update (plain Put path already fixed). | fixed |
| S3-4 | Cursor.DupWrite | Sub-page→sub-DB conversion drops DUPFIXED/LEAF2 format (self-consistent but diverges from C layout). | fixed |
| S3-5 | Cursor.Rebalance | Emptying a tree never decrements `md_leaf_pages` ("corrected by the caller" — no caller does). | fixed |
| S3-6 | Environment | `AllocDbi()` unsynchronized (concurrent named opens can alias Dbi → cached-cursor crosstalk). | fixed |
| S3-7 | Lockfile | Header validated only on create; garbage `NumReaders` from an existing corrupt lockfile walks past the view. | fixed |
| S3-8 | Nested txns | Child COW of a parent-dirty page reassigns the pgno (C keeps it) — wasted flush of a freed page; benign but wasteful. | accepted |

## Verified-correct (traced, no action)

Split index math (virtual-node linearization), PageMerge/NodeMove single-free
discipline, F_BIGDATA node handling in split/merge, child/parent free-page
merge, freelist phase ordering vs WriteSubDbRecords, Idl algorithms
(Search/Sort/TryPop/FindContiguous/RemoveRange), meta toggle math, PgHeadLocal
clone/publish symmetry.
