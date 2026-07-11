// jsdom drive of the Mission Control demo: two browsers, live ticks, dev
// drawer observability, chaos → collaborative incident ack, search, lv-ignore.
import { JSDOM } from "jsdom";

const APP = "http://127.0.0.1:5200/";
const fails = [];
const check = (name, cond, detail = "") => {
    console.log((cond ? "PASS " : "FAIL ") + name + (cond ? "" : " — " + String(detail).slice(0, 200)));
    if (!cond) fails.push(name);
};
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
async function waitFor(fn, ms = 8000) {
    const end = Date.now() + ms;
    while (Date.now() < end) { try { if (fn()) return true; } catch {} await sleep(80); }
    return false;
}

async function browser() {
    const html = await (await fetch(APP)).text();
    const dom = new JSDOM(html, { url: APP, runScripts: "dangerously", pretendToBeVisual: true,
        beforeParse(w) { w.WebSocket = WebSocket; },
        virtualConsole: new (await import("jsdom")).VirtualConsole() });
    await sleep(600);
    return dom;
}

// ── browser A ──
const a = await browser();
const d = a.window.document;

check("SSR: 200 node cards on first paint", d.querySelectorAll(".grid .card").length === 200,
      d.querySelectorAll(".grid .card").length);
check("SSR: dev drawer present + hidden", d.querySelector("#lv-dev")?.hidden === true);

// 1. Live ticks: node metrics change without any user action.
const snapshot = () => [...d.querySelectorAll(".grid .card .pct")].map(e => e.textContent).join(",");
const before = snapshot();
let ok = await waitFor(() => snapshot() !== before, 5000);
check("simulator ticks patch node metrics live", ok);

// 2. Observability drawer: toggle client-side, both halves populated.
d.querySelector(".lv-dev-toggle").click();
check("dev drawer opens via client command", d.querySelector("#lv-dev").hidden === false);
ok = await waitFor(() => d.querySelector("#lv-dev-client .lv-dev-row"));
check("client stats populated by LiveView.debug hook", ok);
ok = await waitFor(() => d.querySelector("#lv-dev-log .lv-dev-op"));
check("wire log shows recent frames", ok);
ok = await waitFor(() => {
    const rows = [...d.querySelectorAll("#lv-dev .lv-dev-row")];
    const r = rows.find(x => x.querySelector("small")?.textContent === "memo hit rate");
    return r && parseFloat(r.querySelector("span").textContent) > 80;
});
check("server stats show memo hit rate > 80%", ok,
      [...d.querySelectorAll('#lv-dev .lv-dev-row')].map(r => r.textContent).join("|"));

// 3. data-lv-ignore: server patches data-avg on #trend root while never
//    touching the client-owned children.
const avg1 = d.querySelector("#trend").getAttribute("data-avg");
ok = await waitFor(() => d.querySelector("#trend").getAttribute("data-avg") !== avg1, 5000);
check("attr patches land on lv-ignore root (sparkline feed)", ok);
check("canvas child untouched by patches", !!d.querySelector("#trend canvas"));

// ── browser B joins ──
const b = await browser();
const db = b.window.document;
ok = await waitFor(() => db.querySelectorAll(".grid .card").length === 200);
check("second browser connected", ok);

// 4. Chaos from A → a NEW incident appears at the top in BOTH browsers.
// (The list caps at 50, so compare the newest incident's identity, not counts.)
const firstIncKey = (doc) => doc.querySelector(".incidents .incident:not(.empty)")?.getAttribute("data-key") || null;
const beforeKeyA = firstIncKey(d);
[...d.querySelectorAll("button")].find(x => x.textContent.includes("chaos")).click();
ok = await waitFor(() => firstIncKey(d) !== null && firstIncKey(d) !== beforeKeyA);
check("chaos → incident appears in browser A", ok, firstIncKey(d));
ok = await waitFor(() => firstIncKey(db) !== null && firstIncKey(db) === firstIncKey(d));
check("incident broadcast to browser B", ok, `${firstIncKey(db)} vs ${firstIncKey(d)}`);

// 5. B acks the incident → A sees "acked by <session>".
const ackBtn = await waitFor(() => db.querySelector('.incident button[data-event="ack"]'));
check("ack button present in B", ackBtn);
db.querySelector('.incident button[data-event="ack"]').click();
ok = await waitFor(() => db.querySelector(".incident.acked"));
check("B sees incident acked", ok);
ok = await waitFor(() => d.querySelector(".incident.acked small")?.textContent.includes("acked by"));
check("A sees who acked (cross-client sync)", ok, d.querySelector(".incident.acked small")?.textContent);

// 6. Debounced per-session search: filter in A, B unaffected.
const input = d.querySelector('input[data-event="search"]');
input.value = "eu-west";
input.dispatchEvent(new a.window.Event("input", { bubbles: true }));
ok = await waitFor(() => d.querySelectorAll(".grid .card").length === 50);
check("debounced search filters to 50 eu-west nodes", ok, d.querySelectorAll(".grid .card").length);
check("B still shows all 200 (per-session state)", db.querySelectorAll(".grid .card").length === 200);

// 7. Status filter chips.
[...d.querySelectorAll(".chip")].find(c => c.textContent === "all").click();
await sleep(100);
input.value = "";
input.dispatchEvent(new a.window.Event("input", { bubbles: true }));
ok = await waitFor(() => d.querySelectorAll(".grid .card").length === 200);
check("clearing search restores 200 cards", ok);

// 8. Dev drawer stayed open through everything (local state preservation).
check("dev drawer still open after all the patches", d.querySelector("#lv-dev").hidden === false);

// 9. Pause round-trips and syncs to B.
[...d.querySelectorAll("button")].find(x => x.textContent.includes("pause")).click();
ok = await waitFor(() => [...db.querySelectorAll("button")].some(x => x.textContent.includes("resume")));
check("pause syncs to every browser", ok);
[...d.querySelectorAll("button")].find(x => x.textContent.includes("resume"))?.click();
await waitFor(() => [...d.querySelectorAll("button")].some(x => x.textContent.includes("pause")));

// 10. Presence: both browsers show 2 viewers; a dropped third client
//     goes grey (parked, away) rather than vanishing.
ok = await waitFor(() => d.querySelectorAll(".viewer").length >= 2);
check("presence chips show both viewers", ok, d.querySelectorAll(".viewer").length);
const raw = new WebSocket("ws://127.0.0.1:5200/ws");
await new Promise(r => { raw.onmessage = r; });
ok = await waitFor(() => d.querySelectorAll(".viewer").length >= 3);
check("third viewer appears via presence broadcast", ok);
raw.close();
ok = await waitFor(() => d.querySelector(".viewer.away"));
check("dropped viewer shows as away (parked presence)", ok);

// 11. Template wire format: the dev panel's tpl cache row shows hits after
//     the chaos insert repeated an incident-row shape.
const tplRow = () => [...d.querySelectorAll("#lv-dev .lv-dev-row")]
    .find(x => x.querySelector("small")?.textContent === "tpl cache")?.querySelector("span")?.textContent || "";
check("dev panel reports template cache activity", /\d+ hits \/ \d+ defs/.test(tplRow()), tplRow());

a.window.close(); b.window.close();
console.log("\nRESULT:", fails.length ? `${fails.length} FAILURES: ${fails}` : "ALL PASS");
process.exit(fails.length ? 1 : 0);
