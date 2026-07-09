#!/usr/bin/env python3
"""Generate reference LMDB databases for cross-validating the C# port.

The C# Lmdb library must be able to read these files byte-for-byte compatibly
with the real liblmdb (shipped inside the Python `lmdb` wheel). Run with:
    python3 test/crosscheck/gen_fixtures.py [output_dir]
Output dir defaults to /tmp/lmdb-ref.
"""
import lmdb, struct, os, shutil, sys

root = sys.argv[1] if len(sys.argv) > 1 else "/tmp/lmdb-ref"
shutil.rmtree(root, ignore_errors=True)
os.makedirs(root)

def bigval(n=20000):
    return bytes(i & 0xFF for i in range(n))  # deterministic 20000-byte value

# hello: single key/value
env = lmdb.open(root + "/hello", map_size=1048576)
t = env.begin(write=True); t.put(b"hello", b"world"); t.commit(); env.close()

# seq: 1000 keys key00000..key00999 -> val_00000.. (forces a multi-level B+tree)
env = lmdb.open(root + "/seq", map_size=16 * 1024 * 1024, max_dbs=4)
t = env.begin(write=True)
for i in range(1000):
    t.put(f"key{i:05d}".encode(), f"val_{i:05d}_payload".encode())
t.commit(); env.close()

# big: an overflow (>1 page) value + a tiny one
env = lmdb.open(root + "/big", map_size=16 * 1024 * 1024)
t = env.begin(write=True)
t.put(b"bigkey", bigval())
t.put(b"small", b"s")
t.commit(); env.close()

# intkey: named sub-DB with MDB_INTEGERKEY (uint64 keys, native LE byte order)
env = lmdb.open(root + "/intkey", map_size=8 * 1024 * 1024, max_dbs=4)
dbi = env.open_db(b"ints", integerkey=True, create=True)
t = env.begin(write=True)
for i in range(0, 500, 7):
    t.put(struct.pack("<Q", i), struct.pack("<Q", i * 1000 + 7), db=dbi)
t.commit(); env.close()

# empty: created, never written
env = lmdb.open(root + "/empty", map_size=1048576); env.close()

# dupsort: named sub-DB with MDB_DUPSORT — multiple values per key
env = lmdb.open(root + "/dupsort", map_size=4 * 1024 * 1024, max_dbs=4)
dbi = env.open_db(b"dups", dupsort=True, create=True)
t = env.begin(write=True)
for v in [b"apple", b"banana", b"cherry", b"date"]:
    t.put(b"fruits", v, db=dbi)
for v in [b"one", b"two", b"three"]:
    t.put(b"nums", v, db=dbi)
t.put(b"single", b"only", db=dbi)
t.commit(); env.close()

print("fixtures generated at", root)
for d in sorted(os.listdir(root)):
    print(" ", d, os.listdir(root + "/" + d))
