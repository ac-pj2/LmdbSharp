# LiveView + LMDB as p2's view and data layer — architectural review

*2026-07-11 · follows the ConfigViews PoC (samples/ConfigViews, commit 5ce452a)*

## The question

Could the LiveView engine render **all** of p2? Should it? And how should the
LMDB object database and PostgreSQL relate long-term?

## 1. Surface map — what p2 actually is

Three fundamentally different UI surfaces, with very different needs:

| Surface | Size | Character | Right renderer |
|---|---|---|---|
| **Config-driven runtime views** — entity lists/details/forms, boards, dashboards, portals, capture pages | ~89 pages, but really *one engine* interpreting `views/*.json` + generated default layouts | Server data, server permissions, server expressions, collaborative, mostly CRUD-and-navigate interactivity | **LiveView — the sweet spot** |
| **Explorer** — config editor: ERD builder, workflow canvas (ReactFlow), view builder, rich text (TipTap) | ~70 pages | An IDE. Deep local interactivity, drag-and-drop canvases, undo stacks, rarely collaborative in the same way | **Stays React** |
| **Rich leaf widgets inside runtime views** — charts, maps, Mermaid, rich text *display/light editing* | ~10 of the 113 catalog components | Client-rendered by nature, but data-fed by the server | **Islands** (`data-lv-ignore` + JS hooks) inside LiveView pages |

### Could LiveView do it all?

In theory yes — Phoenix shops run entire products this way, editors included,
via JS hooks. In practice the last stretch is the wrong trade:

- Rebuilding the workflow canvas or ERD builder server-side means reinventing
  ReactFlow. Months of work to make a worse version of a solved problem.
- The Explorer is used by *authors*, not tenants. It gains nothing from SSR
  first paint, tiny payloads, or presence-per-record. Its bundle weight
  doesn't hurt end users.
- The maintainability argument cuts the other way here: an island with a
  narrow contract (props in, events out) is *more* maintainable than a
  server-side re-implementation of a canvas.

**Verdict: "all" is possible, not wise. The right end-state is: LiveView owns
every config-driven surface (which is where the product actually lives and
grows), React owns the Explorer, and rich widgets are islands with narrow
contracts.** That's not a compromise — it's each tool at full strength. The
key discipline: the *config* stays the single contract. A view definition
doesn't know which engine renders it.

### Why this is a boost, not a lateral move

What LiveView adds that the SPA architecture can't easily reach:

1. **Collaboration becomes ambient.** Today the SSE mutation stream
   invalidates caches and React refetches. With LiveView, every runtime page
   is multiplayer by default: presence ("who's looking at this record"),
   instant patches, "N people here". For an approval/workflow product this is
   a *feature*, not plumbing.
2. **One reactivity engine.** `Core.Expressions.Reactive` becomes the only
   evaluator. The client twin, the parity corpus that guards it, LiveQueryCache,
   and the invalidation choreography all stop existing for live-rendered pages.
3. **Server-authoritative by construction.** `visibleWhen`, permissions, and
   field-level rules are enforced where they're defined. Nothing about the
   config or the data a user can't see ever reaches the browser.
4. **Public/first-paint surfaces get fast.** Capture pages, portals, the
   coaching-hub: 33.6KB total vs a 2.1MB entry chunk; SSR content in the
   first response (also: SEO).
5. **Less code per feature.** A new runtime component = one C# renderer over
   config + entity data. No DTO, no generated types, no React Query wiring,
   no MSW fixtures.
6. **Observability per session.** Render/diff/patch-bytes per user session
   (DevPanel) — nothing comparable exists in the SPA.

### The honest costs

- **Component parity is the long pole.** ~30 components cover the generated
  default layouts + common views; the tail is long. Mitigate with the
  component-catalog JSON as a checklist and a parity test harness (render both
  engines against the same config corpus — same discipline as the expression
  parity corpus).
- **Two renderers during migration** — bounded by the `"renderer": "live"`
  per-view opt-in, so every page has exactly one owner at any time and the SPA
  remains the fallback.
- **Latency model.** Reactive keystroke behavior becomes a debounced round
  trip. Fine on LAN/tailscale; for high-latency users, client commands cover
  pure-UI reactivity and the debounce covers the rest. Validate per-view.
- **Multi-replica deploys** need a broadcast backplane (Redis pub/sub bridging
  hub topics across instances). p2 currently deploys single-container, so this
  is a later, well-understood step.
- **Session memory** per connected user (view state + replay ring). Fine at
  team scale; measure before internet scale.

## 2. The database layer — Postgres and LMDB dovetailing

### The principle: Postgres owns facts, LMDB owns projections

Do **not** make LMDB canonical for tenant entity data. Postgres owns:
transactions across entities, multi-process access, backups/PITR, reporting,
retention/GDPR erasure, row-level security, Hangfire, migrations. That's not
LMDB's game and shouldn't be.

LMDB's game is **in-process reads at memory speed with zero ops burden** —
which is exactly what a server-side view engine needs, because rendering just
became a server concern:

```
   writes                    reads (rendering)
User ──► API ──► PostgreSQL      LiveView Mount/ApplyDelta
              (system of record)      │
                    │                 ▼
                    ▼ outbox /   LMDB read models (per instance)
              IMutationBroadcaster    ▲
                    │                 │
                    └──► Projector ───┘──► hub.BroadcastTopic(...)
                         (upsert projection + notify sessions)
```

**The projector is the keystone**: it subscribes to the existing mutation
pipeline (`IMutationBroadcaster` — already built), writes denormalized,
render-ready records into LMDB (entity + resolved reference titles +
permission facets), and broadcasts the delta to hub topics. One event, two
effects: the cache is fresh *and* every screen showing that entity patches
itself.

### Why this shape is safe and maintainable

- **LMDB is disposable.** It's a projection; rebuild from Postgres on deploy,
  corruption, or schema change. No backup story, no dual-write consistency
  problem, no migration rail. (Use the outbox pattern or replay-from-PG on
  startup for ordering guarantees.)
- **Each store covers the other's weakness.** Postgres gives LMDB
  durability-by-rebuild; LMDB gives Postgres read offload (every render, every
  live query served without a network hop or SQL) and gives LiveView its
  speed: Mount for a 200-row list is a µs-scale scan, not an EF query.
- **It largely replaces the Redis cache role** for view data — typed, LINQ-
  queryable, indexed, in-process, no serialization hop. Redis stays for
  cross-instance concerns (backplane, distributed locks).

### What LMDB should own outright (not projections)

High-write, instance-local, ephemeral state that pollutes Postgres today or
would: draft/autosave form data, session/resume state if it should survive
process restarts, notification cursors, rate counters, capture-page buffers.
These are *native* LMDB tenants — no projector needed, PG never sees them.

## 3. Investment roadmap

- **Phase 1 — productionize the seam** (small): EF adapter behind
  `EntityStore`'s three methods; JWT + `X-System-Slug` through the `configure`
  hook; bridge `IMutationBroadcaster` → hub topics; `"renderer": "live"`
  opt-in in view JSON. Ship the coaching-hub / capture surfaces on it.
- **Phase 2 — the projector + component build-out** (the real investment):
  outbox → LMDB read models; components to cover generated default
  list/detail/create layouts and the 19 field types; declarative action set;
  parity harness against the component catalog.
- **Phase 3 — interior surfaces**: boards/dashboards; islands API
  (`data-hook`) for charts/richtext/maps; presence and live-editing become
  product features.
- **Phase 4 — endgame**: runtime SPA retires route-by-route as parity lands;
  Explorer stays React permanently; add the Redis backplane when replicas > 1.
- **Cross-cutting**: multi-target `net8.0` for the LiveView + Lmdb packages;
  NuGet packaging; move ownership of the C# component renderers next to the
  component catalog so the CI guard covers all three render paths.

## Bottom line

Don't chase "LiveView does everything" — chase **"the config-driven product
runs on LiveView, authoring stays React, and the two never render the same
page."** Pair it with **"Postgres owns facts, LMDB owns projections, the
mutation stream is the spine"** and each piece strengthens the others instead
of competing: the same event that keeps the cache warm patches every screen.
That's a coherent, maintainable long-term architecture — and the PoC has
already de-risked its two scariest assumptions (renderer-agnostic config,
shared expression engine).
