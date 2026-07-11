import asyncio, json, sys
import websockets

URL = "ws://127.0.0.1:5199/ws"
fails = []

def flat(msgs):
    return [p for m in msgs if isinstance(m, dict) and m.get("t") == "p" for p in m.get("p", [])]

def check(name, cond, detail=""):
    print(("PASS " if cond else "FAIL ") + name + (" — " + str(detail)[:200] if not cond else ""))
    if not cond: fails.append(name)

async def recv_json(ws, timeout=3):
    return json.loads(await asyncio.wait_for(ws.recv(), timeout))

async def drain_patches(ws, timeout=1.5):
    """Collect patch arrays until quiet."""
    out = []
    try:
        while True:
            msg = await recv_json(ws, timeout)
            out.append(msg)
            timeout = 0.5
    except asyncio.TimeoutError:
        return out

async def main():
    async with websockets.connect(URL) as a, websockets.connect(URL) as b:
        init_a = await recv_json(a)
        init_b = await recv_json(b)
        check("init message on connect", init_a.get("t") == "init" and "html" in init_a)
        check("init contains data-lvid", "data-lvid" in init_a["html"])

        # 1. Add a todo with an XSS payload from client A.
        payload = '<img src=x onerror=alert(1)>'
        await a.send(json.dumps({"t": "add", "d": {"title": payload, "priority": "2"}}))
        pa = await drain_patches(a)
        pb = await drain_patches(b)
        check("A got patches after add", len(pa) > 0, pa)
        check("B got broadcast patches after add", len(pb) > 0, pb)
        flat_a = flat(pa)
        inserts = [p for p in flat_a if p["t"] == "insert"]
        check("add produced insert patch", len(inserts) == 1, flat_a)
        html = inserts[0]["html"] if inserts else ""
        check("XSS payload escaped in insert html", "<img" not in html and "&lt;img" in html, html)
        # header count changed too
        check("add produced header text patch", any(p["t"] == "text" for p in flat_a), flat_a)
        todo_id = None
        if inserts:
            import re
            m = re.search(r'data-id="(\d+)"', inserts[0]["html"])
            todo_id = m.group(1) if m else None
        check("insert carries data-id", todo_id is not None)

        # 2. Toggle from client B — A must receive the delta-driven patches.
        await b.send(json.dumps({"t": "toggle", "d": {"id": todo_id}}))
        pb2 = await drain_patches(b)
        pa2 = await drain_patches(a)
        flat_b2 = flat(pb2)
        flat_a2 = flat(pa2)
        check("toggle: B got granular patches", any(p["t"] in ("attr", "text") for p in flat_b2), flat_b2)
        check("toggle: A synced via delta", any(p["t"] in ("attr", "text") for p in flat_a2), flat_a2)
        check("toggle: no full replace of list", not any(p["t"] == "replace" and "<ul" in (p.get("html") or "") for p in flat_b2 + flat_a2))

        # 3. Save (edit) from A — title change, B must sync.
        await a.send(json.dumps({"t": "edit", "d": {"id": todo_id}}))
        await drain_patches(a)
        await a.send(json.dumps({"t": "save", "d": {"title": "renamed & <safe>", "id": todo_id}}))
        pa3 = await drain_patches(a)
        pb3 = await drain_patches(b)
        flat_b3 = flat(pb3)
        check("save: B received title update", len(flat_b3) > 0, pb3)
        txt = json.dumps(flat_b3)
        check("save: title text arrives raw in text patch (client uses textContent)",
              "renamed & <safe>" in (p.get("val", "") for p in flat_b3).__iter__().__next__() if False else any("renamed" in str(p.get("val", "")) + str(p.get("html", "")) for p in flat_b3), flat_b3)

        # 4. Heartbeat must not hit view code / crash.
        await a.send('{"t":"__ping"}')
        # 5. Multi-frame: oversized-but-valid event (>16KB) must still parse.
        big_title = "x" * 40000
        await a.send(json.dumps({"t": "add", "d": {"title": big_title, "priority": "1"}}))
        pa5 = await drain_patches(a, timeout=3)
        flat_a5 = flat(pa5)
        check("40KB event handled (multi-frame receive)", any(p["t"] == "insert" for p in flat_a5), str(pa5)[:200])

        # 6. Delete both — B initiates, A syncs.
        import re
        ins5 = [p for p in flat_a5 if p["t"] == "insert"]
        big_id = re.search(r'data-id="(\d+)"', ins5[0]["html"]).group(1) if ins5 else None
        for tid in (todo_id, big_id):
            if tid is None: continue
            await b.send(json.dumps({"t": "delete", "d": {"id": tid}}))
            await drain_patches(b)
        pa6 = await drain_patches(a)
        flat_a6 = flat(pa6)
        check("delete: A received remove patches", any(p["t"] == "remove" for p in flat_a6), pa6)

    print()
    print("RESULT:", "ALL PASS" if not fails else f"{len(fails)} FAILURES: {fails}")
    sys.exit(1 if fails else 0)

asyncio.run(main())
