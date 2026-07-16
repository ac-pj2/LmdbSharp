# Deliberate divergences from C LMDB

LmdbSharp keeps LMDB 0.9.x **file-format compatibility** (verified by the
differential battery, which includes C LMDB reading files this engine wrote),
but a few internal behaviors intentionally differ from `mdb.c`. None of them
change what a valid database file looks like — they change which of the many
valid files/behaviors the engine produces.

## Write path

- **Pure-append leaf splits leave the left page untouched.** When the
  rightmost insert lands on a full leaf, the new entry goes alone to a fresh
  right sibling instead of C's copy-rebuild ~tail split. Sequentially loaded
  trees pack ~100% full (C: ~50% at the tail) and bulk loads skip the rebuild
  memcpy entirely. Branch splits keep C's shape (a 1-child branch would
  violate the min-fanout invariant).
- **No `MDB_WRITEMAP`-style in-place dirty pages.** Dirty pages are native
  buffers flushed into the map at commit. The commit protocol (data flush,
  fsync, meta write, fsync) matches C's non-WRITEMAP mode.
- **Spilled pages are not unspilled.** When a write transaction exceeds
  `EnvOpenOptions.MaxDirtyPages`, excess dirty pages are written to their
  final map location early (same safety envelope as the commit flush).
  Re-touching a spilled page takes the normal COW path under a fresh page
  number instead of C's in-place unspill; the page-number churn is absorbed
  by loose-page recycling.
- **Nested transactions do not spill.** A deeply nested bulk load can exceed
  the dirty budget; top-level transactions are always bounded.

## Cursors

- **Dup-cursor positions survive storage-shape conversions by value.** When
  a duplicate set converts from an inline sub-page to a sub-DB under a parked
  cursor, the parked cursor's dup position is captured by value and re-sought
  afterwards. C's `mdb_xcursor_init2` teleports parked dup cursors to the
  acting cursor's position instead.
- After a cursor's current entry is deleted by another cursor, both engines
  are slot-anchored (`C_DEL`): `Next` returns the entry that slid into the
  slot. Interleaved inserts below the slot shift that meaning identically in
  both engines.

## Environment / recovery

- **Meta-sync failure panics the environment for writes** (matching C's
  `MDB_FATAL_ERROR`), and `LmdbEnvironment.SetMapSize(0)` adopts a foreign
  grown map without reopening. Cross-process map growth requires the
  detect-and-adopt step (`MDB_MAP_RESIZED` → `SetMapSize`); there is no live
  remap of an environment with active transactions.
- **Freed-page reuse is optional** (`EnvOpenOptions.ReuseFreePages`, default
  on). Disabling it gives monotonic page allocation.

## Not implemented

- `MDB_WRITEMAP`, `MDB_NOSYNC`/`MDB_NOMETASYNC` modes.
- Cross-process live map remap (grow-by-reopen or `SetMapSize` instead).
