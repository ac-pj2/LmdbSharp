// Phase 2 browser suite: full coaching-hub coverage on the LiveView engine —
// generated default layouts, dataBindings + expression props, card grids with
// rollups, EntityChildren, Columns/Card/Section, nav gating by role, and
// query-param form prefill. Runs against the live p2-backed instance (5302).
import { JSDOM } from "jsdom";

const APP = "http://127.0.0.1:5302";
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
async function browser(path) {
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

// ── 1. Generated default layout: /articles (no explicit list view exists) ──
const a = await browser("/articles");
const d = a.window.document;
check("generated /articles list renders real articles",
    [...d.querySelectorAll("tr.row")].length >= 5, d.querySelectorAll("tr.row").length);
check("generated layout says so", d.body.textContent.includes("generated default layout"));
check("nav bar from navigation.json", [...d.querySelectorAll(".navlink")].length >= 4,
    d.querySelectorAll(".navlink").length);
check("admin nav item hidden for guest",
    ![...d.querySelectorAll(".navlink")].some(n => n.textContent.includes("Publish site")));

// 2. Role-gated nav: Coach Dana is Administrator (user.role expression).
setSelect(a, d.querySelector('select[name="loginas"]'), "Coach Dana");
let ok = await waitFor(() => [...d.querySelectorAll(".navlink")].some(n => n.textContent.includes("Publish site")));
check("admin nav item appears for Administrator (user.role via Jint)", ok);

// 3. Article detail: explicit view with dataBindings + contentExpr.
[...d.querySelectorAll("tr.row")][0].click();
ok = await waitFor(() => /\/articles\//.test(a.window.location.pathname) && d.querySelector(".text h1, h1"));
check("article row → detail via live nav", ok, a.window.location.pathname);
ok = await waitFor(() => d.body.textContent.includes("min read"));
check("contentExpr evaluated (reading-time line)", ok);
check("FieldValue richtext body rendered", [...d.querySelectorAll(".body p")].length >= 1);

// 4. Community: EntityCardGrid with rollups.
[...d.querySelectorAll(".navlink")].find(n => n.textContent.includes("Community")).click();
ok = await waitFor(() => d.querySelectorAll(".cardgrid .cfg-card").length >= 1);
check("category card grid renders", ok);
ok = await waitFor(() => /\d+ threads/.test(d.body.textContent));
check("rollup counts (N threads)", ok);

// 5. Category detail: EntityChildren + interpolated navigate ({{params.id}}).
const trainingCard = [...d.querySelectorAll(".cardgrid .cfg-card")].find(c => c.textContent.includes("Training"));
check("Training category card present", !!trainingCard);
trainingCard.click();
ok = await waitFor(() => d.querySelector(".childlist"));
check("EntityChildren thread list renders", ok);
const catId = a.window.location.pathname.split("/").pop();
const newBtn = [...d.querySelectorAll("button")].find(b => b.textContent === "New thread");
check("New thread button carries interpolated {{params.id}} query",
    newBtn?.getAttribute("data-to") === `/forum-threads/new?category=${catId}`,
    newBtn?.getAttribute("data-to"));

// 6. Query-param prefill: category select pre-selected from ?category=.
newBtn.click();
ok = await waitFor(() => d.querySelector(".entityform"));
check("create form via category CTA", ok);
const sel = d.querySelector('.entityform select[name="category"]');
check("category prefilled from query param", sel?.value === catId, sel?.value);

// 7. Member home: Columns / Card / Section / compact lists.
[...d.querySelectorAll(".navlink")].find(n => n.textContent.includes("My Hub")).click();
ok = await waitFor(() => d.querySelector(".cfg-columns"));
check("member-home Columns layout renders", ok);
check("Cards with titles", [...d.querySelectorAll(".cfg-card h3")].length >= 2,
    [...d.querySelectorAll(".cfg-card h3")].map(h => h.textContent).join("|"));
check("Section title from titleExpr (Welcome back, Coach)",
    d.body.textContent.includes("Welcome back, Coach"));
check("ArticleCardGrid renders article cards",
    [...d.querySelectorAll(".cardgrid .cfg-card")].length >= 1);

// 8. Zero unimplemented components anywhere we visited.
check("no 'has no server renderer yet' placeholders",
    !d.body.textContent.includes("has no server renderer yet"));

// 9. DevPanel reports the durable projection.
check("DevPanel shows lmdb projection stats", /lmdb · \d+ records · loaded in \d+µs/.test(
    [...d.querySelectorAll("#lv-dev .lv-dev-row")].map(r => r.textContent).join(" ")),
    [...d.querySelectorAll("#lv-dev .lv-dev-row")].map(r => r.textContent).join("|").slice(0, 200));

a.window.close();
console.log("\nRESULT:", fails.length ? `${fails.length} FAILURES: ${fails}` : "ALL PASS");
process.exit(fails.length ? 1 : 0);
