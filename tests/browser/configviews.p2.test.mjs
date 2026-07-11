// Phase-1 E2E: the ConfigViews PoC in p2 mode, against the LIVE platform.
//
// Proves the full loop:
//   LiveView UI create → p2 REST API → PostgreSQL → mutation stream →
//   bridge → every LiveView session patches.
// And the reverse: an entity created directly through the p2 API (as the
// React SPA would) appears in connected LiveView browsers without refresh.
import { JSDOM } from "jsdom";
import { execSync } from "node:child_process";

const APP = "http://127.0.0.1:5302";
const P2 = "http://127.0.0.1:5211";
const RUN = Date.now().toString(36);   // unique per run — earlier runs leave real rows behind
const fails = [];
const check = (name, cond, detail = "") => {
    console.log((cond ? "PASS " : "FAIL ") + name + (cond ? "" : " — " + String(detail).slice(0, 250)));
    if (!cond) fails.push(name);
};
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
async function waitFor(fn, ms = 8000) {
    const end = Date.now() + ms;
    while (Date.now() < end) { try { if (fn()) return true; } catch {} await sleep(80); }
    return false;
}

// p2 API client (plays the role of the React SPA).
const login = await (await fetch(P2 + "/api/auth/login", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ email: "admin@test.com", password: "DevPass123!" }),
})).json();
const token = login.data.token ?? login.data.accessToken;
check("p2 API login", !!token);

async function p2Create(entityTypeSlug, formData) {
    const res = await (await fetch(P2 + "/api/entities", {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}`,
                   "X-System-Slug": "coaching-hub" },
        body: JSON.stringify({ entityTypeSlug, systemSlug: "coaching-hub", formData }),
    })).json();
    if (!res.success) throw new Error(JSON.stringify(res).slice(0, 200));
    return res.data;
}

function psql(q) {
    // The shared dev postgres can transiently hit max_connections (fleet slots).
    for (let attempt = 0; ; attempt++) {
        try {
            return execSync(`docker exec p2_postgres psql -U postgres -d workflow_system -tAc "${q}"`).toString().trim();
        } catch (e) {
            if (attempt >= 4) throw e;
            execSync("sleep 3");
        }
    }
}

async function browser(path = "/forum-threads") {
    const html = await (await fetch(APP + path)).text();
    const dom = new JSDOM(html, { url: APP + path, runScripts: "dangerously", pretendToBeVisual: true,
        beforeParse(w) { w.WebSocket = WebSocket; } });
    await sleep(700);
    return dom;
}
const setSelect = (w, sel, value) => {
    sel.value = value;
    sel.dispatchEvent(new w.window.Event("change", { bubbles: true }));
};

// ── two LiveView browsers on the live-platform-backed forum ──
const a = await browser();
const d = a.window.document;
const b = await browser();
const db = b.window.document;

// 1. A category created via the p2 API (as the SPA would) reaches both
//    LiveView browsers through the mutation bridge — no refresh.
const cat = await p2Create("forum-category", { name: `Training ${RUN}` });
let ok = await waitFor(() =>
    [...d.querySelectorAll('select[name="filter-category"] option')].some(o => o.textContent === `Training ${RUN}`));
check("SPA-side create → bridge → LiveView A updates live", ok);
ok = await waitFor(() =>
    [...db.querySelectorAll('select[name="filter-category"] option')].some(o => o.textContent === `Training ${RUN}`));
check("… and LiveView B updates live", ok);

// 2. Create a thread through the LiveView UI → runs the REAL platform pipeline.
setSelect(a, d.querySelector('select[name="loginas"]'), "Alice");
await waitFor(() => [...d.querySelectorAll("button")].some(x => x.textContent === "New thread"));
[...d.querySelectorAll("button")].find(x => x.textContent === "New thread").click();
await waitFor(() => d.querySelector(".entityform"));
const form = d.querySelector(".entityform");
form.querySelector('input[name="title"]').value = `First LiveView thread ${RUN}`;
form.querySelector('textarea[name="body"]').value = "This create went through p2's REST API — validation, audit, reference number and all.";
setSelect(a, form.querySelector('select[name="category"]'), cat.id);
form.dispatchEvent(new a.window.Event("submit", { bubbles: true, cancelable: true }));

ok = await waitFor(() => d.querySelector(".detail h2")?.textContent.includes(`First LiveView thread ${RUN}`));
check("UI create → p2 API → detail view (afterCreate)", ok);
const threadRef = d.querySelector(".detail .ref")?.textContent ?? "";
check("platform assigned a real reference number", /THRD-/.test(threadRef), threadRef);

// 3. It's genuinely in their PostgreSQL, written by their API.
const inPg = psql(`SELECT \\"ReferenceNumber\\" FROM \\"Entities\\" WHERE \\"SystemSlug\\"='coaching-hub' AND \\"EntityTypeSlug\\"='forum-thread' AND \\"FormData\\"->>'title' LIKE 'First LiveView thread ${RUN}%' AND NOT \\"IsDeleted\\"`);
check("thread row exists in p2's PostgreSQL", inPg === threadRef, `pg='${inPg}' vs ui='${threadRef}'`);

// 4. Browser B sees the new thread live (own-write broadcast + bridge echo dedupe).
ok = await waitFor(() => [...db.querySelectorAll("tr.row")].some(r => r.textContent.includes(`First LiveView thread ${RUN}`)));
check("thread appears live in browser B", ok);
check("B shows exactly one copy (bridge echo is idempotent)",
    [...db.querySelectorAll("tr.row")].filter(r => r.textContent.includes(`First LiveView thread ${RUN}`)).length === 1);

// 5. Reply through the UI → p2's real Comments subsystem.
setSelect(b, db.querySelector('select[name="loginas"]'), "Ben");
[...db.querySelectorAll("tr.row")].find(r => r.textContent.includes(`First LiveView thread ${RUN}`)).click();
await waitFor(() => db.querySelector(".replyform"));
db.querySelector('.replyform textarea[name="body"]').value = "Replying via the real Comments API.";
db.querySelector(".replyform").dispatchEvent(new b.window.Event("submit", { bubbles: true, cancelable: true }));
ok = await waitFor(() => [...d.querySelectorAll(".comment")].some(c => c.textContent.includes("Replying via the real")));
check("reply lands in A's browser live", ok);
const commentCount = psql(`SELECT count(*) FROM \\"Comments\\" c JOIN \\"Entities\\" e ON e.\\"Id\\"=c.\\"EntityId\\" WHERE e.\\"FormData\\"->>'title' LIKE 'First LiveView thread ${RUN}%'`);
check("comment row exists in p2's Comments table", commentCount === "1", commentCount);

// 6. A second thread created straight through the p2 API appears in both browsers.
await p2Create("forum-thread", { title: `Posted from the p2 API side ${RUN}`, body: "If you can read this in LiveView, the bridge works.", category: cat.id });
ok = await waitFor(() => {
    a.window.history.back(); // ensure A is on the list (detail also fine, but list shows it)
    return [...d.querySelectorAll("tr.row")].some(r => r.textContent.includes(`Posted from the p2 API side ${RUN}`));
}, 9000);
check("API-side thread appears in LiveView A", ok);

a.window.close(); b.window.close();
console.log("\nRESULT:", fails.length ? `${fails.length} FAILURES: ${fails}` : "ALL PASS");
process.exit(fails.length ? 1 : 0);
