"""Session resume protocol test against LiveTodo:
disconnect a client, change state from another client, reconnect with the
session id + last seq → the server replays exactly the missed patches
(no full init). Also: bogus resume falls back to init.
"""
import asyncio, json, sys
import websockets

URL = "ws://127.0.0.1:5199/ws"
fails = []

def check(name, cond, detail=""):
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else " — " + str(detail)[:300]))
    if not cond: fails.append(name)

async def drain(ws, timeout=1.5):
    out = []
    try:
        while True:
            out.append(json.loads(await asyncio.wait_for(ws.recv(), timeout)))
            timeout = 0.5
    except asyncio.TimeoutError:
        return out

def last_seq(msgs, current):
    for m in msgs:
        if isinstance(m, dict) and isinstance(m.get("s"), (int, float)):
            current = max(current, int(m["s"]))
    return current

async def main():
    # c1 connects and notes its session id + seq.
    c1 = await websockets.connect(URL)
    init = json.loads(await asyncio.wait_for(c1.recv(), 3))
    check("init carries session id + seq", init.get("t") == "init" and "sid" in init and "s" in init, init.keys())
    sid, seq = init["sid"], init["s"]

    # c2 joins and adds a todo — c1 sees it live.
    c2 = await websockets.connect(URL)
    await asyncio.wait_for(c2.recv(), 3)
    seq = last_seq(await drain(c1), seq)  # DevPanel sessions-count patch etc.

    await c2.send(json.dumps({"t": "add", "d": {"title": "seen live", "priority": "1"}}))
    live = await drain(c1)
    check("c1 received live patch before disconnect",
          any(m.get("t") == "p" for m in live if isinstance(m, dict)), live)
    seq = last_seq(live, seq)

    # c1 drops. While it's gone, c2 makes a change c1 never saw.
    await c1.close()
    await c2.send(json.dumps({"t": "add", "d": {"title": "missed while offline", "priority": "3"}}))
    await drain(c2)
    await asyncio.sleep(0.3)

    # c1 reconnects with resume — expect ack + replayed patches, NO init.
    c1b = await websockets.connect(f"{URL}?resume={sid}&seq={seq}")
    resumed = await drain(c1b, timeout=3)
    check("resume ack is first message",
          len(resumed) > 0 and resumed[0].get("t") == "r", resumed[:1])
    check("no full init on resume", not any(m.get("t") == "init" for m in resumed), [m.get("t") for m in resumed])
    replay_text = json.dumps(resumed)
    check("missed change replayed", "missed while offline" in replay_text, replay_text[:300])
    replayed_seqs = [m["s"] for m in resumed if isinstance(m.get("s"), int)]
    check("replayed seqs continue past client seq", replayed_seqs and min(replayed_seqs) == seq + 1,
          f"client seq {seq}, replayed {replayed_seqs}")

    # The resumed session keeps working: c2 changes again, c1b sees it.
    await c2.send(json.dumps({"t": "add", "d": {"title": "after resume", "priority": "2"}}))
    after = await drain(c1b)
    check("resumed session receives new patches", "after resume" in json.dumps(after), after)

    # Bogus session id → fresh init.
    c3 = await websockets.connect(f"{URL}?resume=deadbeefdeadbeef&seq=5")
    first = json.loads(await asyncio.wait_for(c3.recv(), 3))
    check("bogus resume falls back to full init", first.get("t") == "init", first.get("t"))

    # Cleanup todos so reruns start clean.
    ids = []
    import re
    for m in resumed + after + live:
        if m.get("t") == "p":
            for p in m["p"]:
                if p.get("t") == "insert":
                    found = re.search(r'data-id="(\d+)"', p.get("html", ""))
                    if found: ids.append(found.group(1))
    for tid in set(ids):
        await c2.send(json.dumps({"t": "delete", "d": {"id": tid}}))
    await drain(c2, timeout=0.6)

    await c1b.close(); await c2.close(); await c3.close()
    print()
    print("RESULT:", "ALL PASS" if not fails else f"{len(fails)} FAILURES: {fails}")
    sys.exit(1 if fails else 0)

asyncio.run(main())
