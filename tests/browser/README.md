# Browser-level tests (jsdom)

These drive the REAL client runtime end-to-end against a running sample
server: real page, real inline scripts, real WebSocket (Node's global).

```bash
npm install

# terminal 1                                                # terminal 2
dotnet run --project ../../samples/LiveTodo \
  -- TodoDbPath=/tmp/todos-test                             npm run test:livetodo
# (LiveTodo expects http://127.0.0.1:5199 — set ASPNETCORE_URLS)

dotnet run --project ../../samples/MissionControl \
  -- FleetDbPath=/tmp/fleet-test                            npm run test:missioncontrol
# (Mission Control expects http://127.0.0.1:5200)
```

livetodo.test.mjs        SSR adoption, client commands (zero network), local
                         state preservation, transitions + cancellation,
                         focused-input protection, cross-client sync
missioncontrol.test.mjs  two concurrent "browsers": live ticks, dev drawer
                         stats (memo hit rate), lv-ignore zones, chaos →
                         incident → cross-browser ack, per-session search

## Protocol tests (python, `pip install websockets`)

livetodo.protocol.py     raw wire assertions against LiveTodo (port 5199):
                         patch envelopes, escaping, multi-frame events,
                         cross-client sync
livetodo.resume.py       session resume: disconnect, miss changes, reconnect
                         with sid+seq → exact replay, no full init
