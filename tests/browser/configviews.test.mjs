// jsdom drive of the ConfigViews PoC: p2 config rendered by LiveView.
// Two browsers: navigation (pushState + back), member gating via the platform's
// expression engine, create with validation, live cross-browser updates,
// replies, presence, search/filter/sort.
import { JSDOM } from "jsdom";

const APP = "http://127.0.0.1:5301";
const fails = [];
const check = (name, cond, detail = "") => {
    console.log((cond ? "PASS " : "FAIL ") + name + (cond ? "" : " — " + String(detail).slice(0, 250)));
    if (!cond) fails.push(name);
};
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
async function waitFor(fn, ms = 6000) {
    const end = Date.now() + ms;
    while (Date.now() < end) { try { if (fn()) return true; } catch {} await sleep(60); }
    return false;
}

async function browser(path = "/forum-threads") {
    const html = await (await fetch(APP + path)).text();
    const dom = new JSDOM(html, { url: APP + path, runScripts: "dangerously", pretendToBeVisual: true,
        beforeParse(w) { w.WebSocket = WebSocket; } });
    await sleep(600);
    return dom;
}

const setSelect = (w, sel, value) => {
    sel.value = value;
    sel.dispatchEvent(new w.window.Event("change", { bubbles: true }));
};

// ── browser A: guest on the thread list ──
const a = await browser();
const d = a.window.document;

check("SSR: seeded threads render", [...d.querySelectorAll("tr.row")].length >= 4,
      d.querySelectorAll("tr.row").length);
check("SSR: pinned thread shows 📌", d.body.textContent.includes("📌"));
check("guest: no 'New thread' button (visibleWhen via p2's Jint engine)",
      ![...d.querySelectorAll("button")].some(b => b.textContent === "New thread"));

// 1. Row click → live navigation to detail (patches + pushState, no reload).
const firstRow = d.querySelector("tr.row");
const threadTitle = firstRow.children[1].textContent;
firstRow.click();
let ok = await waitFor(() => d.querySelector(".detail h2")?.textContent === threadTitle);
check("row click → detail view rendered via patches", ok, d.querySelector(".detail h2")?.textContent);
ok = await waitFor(() => a.window.location.pathname.startsWith("/forum-threads/"));
check("URL updated via pushState", ok, a.window.location.pathname);
check("guest: reply form gated", !!d.querySelector(".comments .gate"));

// 2. Back button → list again (popstate → __nav echo).
a.window.history.back();
ok = await waitFor(() => d.querySelector("tr.row"));
check("browser back → list re-rendered (live nav echo)", ok);

// 3. Sign in as Alice → New thread button appears (expression re-evaluated).
setSelect(a, d.querySelector('select[name="loginas"]'), "Alice");
ok = await waitFor(() => [...d.querySelectorAll("button")].some(b => b.textContent === "New thread"));
check("login → 'New thread' appears (server-side visibleWhen)", ok);

// 4. Navigate to create; submit invalid (missing required fields from entity-type config).
[...d.querySelectorAll("button")].find(b => b.textContent === "New thread").click();
ok = await waitFor(() => d.querySelector(".entityform"));
check("create view: EntityForm rendered from config sections", ok);
check("create view: reference field is a select with categories",
      [...d.querySelectorAll('.entityform select[name="category"] option')].length >= 4);

d.querySelector(".entityform").dispatchEvent(new a.window.Event("submit", { bubbles: true, cancelable: true }));
ok = await waitFor(() => d.querySelector(".errors"));
check("required-field validation from entity-type config", ok
      && d.querySelector(".errors").textContent.includes("Title is required"),
      d.querySelector(".errors")?.textContent);

// 5. Fill and submit — afterCreate:"detail" navigates to the new thread.
const form = d.querySelector(".entityform");
form.querySelector('input[name="title"]').value = "Trail shoes — worth it?";
form.querySelector('textarea[name="body"]').value = "Thinking about the muddy season.\n\nAny recommendations?";
setSelect(a, form.querySelector('select[name="category"]'), "3"); // Questions
form.dispatchEvent(new a.window.Event("submit", { bubbles: true, cancelable: true }));
ok = await waitFor(() => d.querySelector(".detail h2")?.textContent === "Trail shoes — worth it?");
check("create → afterCreate navigates to new thread detail", ok);
ok = await waitFor(() => /forum-threads\/\d+/.test(a.window.location.pathname));
check("new thread URL pushed", ok, a.window.location.pathname);

// ── browser B joins the list as Ben ──
const b = await browser();
const db = b.window.document;
ok = await waitFor(() => [...db.querySelectorAll("tr.row")].some(r => r.textContent.includes("Trail shoes")));
check("B sees A's new thread in the list (broadcast, no refresh)", ok);

setSelect(b, db.querySelector('select[name="loginas"]'), "Ben");
[...db.querySelectorAll("tr.row")].find(r => r.textContent.includes("Trail shoes")).click();
ok = await waitFor(() => db.querySelector(".detail h2")?.textContent.includes("Trail shoes"));
check("B opens the same thread", ok);

// 6. Presence: A's detail page shows 2 people here.
ok = await waitFor(() => d.querySelector(".viewing")?.textContent.includes("2"));
check("thread presence: A sees 2 people here", ok, d.querySelector(".viewing")?.textContent);

// 7. B replies → appears live for A.
db.querySelector('.replyform textarea[name="body"]').value = "Get the grippy ones. Zero regrets.";
db.querySelector(".replyform").dispatchEvent(new b.window.Event("submit", { bubbles: true, cancelable: true }));
ok = await waitFor(() => [...d.querySelectorAll(".comment")].some(c => c.textContent.includes("grippy")));
check("B's reply appears live in A's browser", ok);
check("reply attributed to Ben", [...d.querySelectorAll(".comment")].some(c =>
      c.textContent.includes("Ben") && c.textContent.includes("grippy")));

// 8. Search + filter on the list (debounced, per-session).
a.window.history.back(); // → create page
await waitFor(() => d.querySelector(".entityform"));
a.window.history.back(); // → list
ok = await waitFor(() => d.querySelector('input[name="q"]'));
check("back×2 → list with toolbar", ok);
const search = d.querySelector('input[name="q"]');
search.value = "rest-day";
search.dispatchEvent(new a.window.Event("input", { bubbles: true }));
ok = await waitFor(() => d.querySelectorAll("tr.row").length === 1);
check("search filters to 1 row", ok, d.querySelectorAll("tr.row").length);
check("B's list unaffected (per-session state)", db.querySelector(".detail") !== null
      || [...db.querySelectorAll("tr.row")].length > 1);
search.value = "";
search.dispatchEvent(new a.window.Event("input", { bubbles: true }));
await waitFor(() => d.querySelectorAll("tr.row").length > 1);

// 9. Closed thread: replies are off.
[...d.querySelectorAll("tr.row")].find(r => r.textContent.includes("group call")).click();
ok = await waitFor(() => d.querySelector(".comments .gate")?.textContent.includes("closed"));
check("closed thread blocks replies (behavior from entity data)", ok,
      d.querySelector(".comments .gate")?.textContent);

// 10. XSS: title with markup renders inert.
const evil = await browser("/forum-threads/new");
const de = evil.window.document;
setSelect(evil, de.querySelector('select[name="loginas"]'), "Coach Dana");
ok = await waitFor(() => de.querySelector(".entityform"));
const ef = de.querySelector(".entityform");
ef.querySelector('input[name="title"]').value = '<img src=x onerror=window.__pwned=1>';
ef.querySelector('textarea[name="body"]').value = "body";
setSelect(evil, ef.querySelector('select[name="category"]'), "1");
ef.dispatchEvent(new evil.window.Event("submit", { bubbles: true, cancelable: true }));
ok = await waitFor(() => de.querySelector(".detail h2"));
check("XSS title renders as inert text", ok
      && de.querySelector(".detail h2").textContent.includes("<img")
      && !de.querySelector(".detail h2 img") && !evil.window.__pwned);

a.window.close(); b.window.close(); evil.window.close();
console.log("\nRESULT:", fails.length ? `${fails.length} FAILURES: ${fails}` : "ALL PASS");
process.exit(fails.length ? 1 : 0);
