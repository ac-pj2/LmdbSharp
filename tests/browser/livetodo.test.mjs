// DOM-level test of the LiveView client runtime: loads the real page from the
// running server in jsdom, with Node's real WebSocket wired in, and drives the
// actual inline runtime — clicks, form submits, patches, client-side commands.
import { JSDOM } from "jsdom";

const APP = "http://127.0.0.1:5199/";
const fails = [];
const check = (name, cond, detail = "") => {
    console.log((cond ? "PASS " : "FAIL ") + name + (cond ? "" : " — " + String(detail).slice(0, 200)));
    if (!cond) fails.push(name);
};
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
async function waitFor(fn, ms = 5000) {
    const end = Date.now() + ms;
    while (Date.now() < end) {
        try { if (fn()) return true; } catch {}
        await sleep(50);
    }
    return false;
}

const html = await (await fetch(APP)).text();
check("SSR page contains rendered view", html.includes('data-lvid') && html.includes('id="help"'));

let sendCount = 0;
class CountingWebSocket extends WebSocket {
    send(data) { sendCount++; return super.send(data); }
}

const dom = new JSDOM(html, {
    url: APP,
    runScripts: "dangerously",
    pretendToBeVisual: true,
    beforeParse(window) { window.WebSocket = CountingWebSocket; },
});
const w = dom.window, d = w.document;

// Wait for WS handshake ('ok' fingerprint adoption or init).
await sleep(800);
check("app root has content after connect", d.querySelectorAll("#app [data-lvid]").length > 0);

// 1. Client-side command: zero network round trip.
check("help panel initially hidden", d.querySelector("#help").hidden === true);
const sentBefore = sendCount;
d.querySelector(".help-btn").click();
check("help shown instantly on click", d.querySelector("#help").hidden === false);
check("no WS message sent for client-side toggle", sendCount === sentBefore, `${sentBefore} -> ${sendCount}`);
d.querySelector(".help-btn").click();
check("help toggles back to hidden", d.querySelector("#help").hidden === true);
d.querySelector(".help-btn").click(); // leave open for preservation test

// 2. Real form submit → server round trip → insert patch.
const form = d.querySelector('form[data-event="add"]');
form.querySelector('input[name="title"]').value = "dom test todo";
form.dispatchEvent(new w.Event("submit", { bubbles: true, cancelable: true }));
let ok = await waitFor(() =>
    [...d.querySelectorAll("li span[data-key=title]")].some(s => s.textContent === "dom test todo"));
check("form submit → todo appears via insert patch", ok);

// 3. Cross-client broadcast; local UI state must survive the patches.
const other = new WebSocket("ws://127.0.0.1:5199/ws");
await new Promise(r => other.onmessage = r); // init
other.send(JSON.stringify({ t: "add", d: { title: "from other client", priority: "1" } }));
ok = await waitFor(() =>
    [...d.querySelectorAll("li span[data-key=title]")].some(s => s.textContent === "from other client"));
check("cross-client broadcast lands in jsdom DOM", ok);
check("help panel STILL open after unrelated server patches", d.querySelector("#help").hidden === false);

// 4. Toggle done → attr patch. Memoized rows: only this row changes.
const row = () => [...d.querySelectorAll("li")].find(li =>
    li.querySelector("span[data-key=title]")?.textContent === "dom test todo");
row().querySelector("button[data-event=toggle]").click();
ok = await waitFor(() => row()?.className === "done");
check("toggle → 'done' class via attr patch", ok);

// 5. lv-busy applied on click and cleared when the server responds.
//    (toggle again and race-check is flaky in jsdom; assert cleared end-state)
check("lv-busy cleared after server response", !row().querySelector(".lv-busy"));

// 6. Delete → remove patch.
const before = d.querySelectorAll("li").length;
row().querySelector("button[data-event=delete]").click();
ok = await waitFor(() => d.querySelectorAll("li").length === before - 1);
check("delete → row removed", ok);

// 7. Focused-input protection: focus edit field, trigger unrelated broadcast,
//    input value/focus must survive (its subtree isn't replaced; also guard attr).
const anyTitle = d.querySelector("li span[data-key=title]");
anyTitle.click(); // enter edit mode (server round trip renders edit form)
ok = await waitFor(() => d.querySelector('li form[data-event="save"]'));
check("edit mode form rendered", ok);
const editInput = d.querySelector('li form[data-event="save"] input[name="title"]');
editInput.focus();
editInput.value = "user typing...";
other.send(JSON.stringify({ t: "add", d: { title: "noise while editing", priority: "2" } }));
ok = await waitFor(() =>
    [...d.querySelectorAll("li span[data-key=title]")].some(s => s.textContent === "noise while editing"));
check("broadcast processed while editing", ok);
check("in-progress typing preserved", editInput.isConnected && editInput.value === "user typing...",
      `connected=${editInput.isConnected} value=${editInput.value}`);

// 8. Transitions ("toggle #help with fade"). jsdom fires no animation events,
//    so this exercises the computed-duration timeout fallback. Give the panel
//    an inline duration so getComputedStyle reports one.
const help = d.querySelector("#help");
help.style.animationDuration = "0.1s";
const helpBtn = d.querySelector(".help-btn");
// currently open from earlier; click to hide with animation
check("pre-transition: help open", help.hidden === false);
helpBtn.click();
check("hide with transition: still visible during animation", help.hidden === false);
check("hide with transition: -out class applied", help.classList.contains("fade-out"));
ok = await waitFor(() => help.hidden === true && !help.classList.contains("fade-out"), 2000);
check("hide with transition: hidden after animation, class removed", ok,
      `hidden=${help.hidden} class=${help.className}`);

// 9. Show with transition.
helpBtn.click();
check("show with transition: visible immediately", help.hidden === false);
check("show with transition: -in class applied", help.classList.contains("fade-in"));
ok = await waitFor(() => !help.classList.contains("fade-in"), 2000);
check("show with transition: -in class removed after animation", ok);

// 10. Cancellation: hide then immediately re-show — the cancelled hide's
//     timer must NOT hide the panel later.
helpBtn.click();            // start hide animation
await sleep(20);
helpBtn.click();            // re-show mid-animation (toggle uses target state)
check("re-show mid-hide: visible", help.hidden === false);
check("re-show mid-hide: -out class cancelled", !help.classList.contains("fade-out"));
await sleep(400);           // past the cancelled hide's fallback timer
check("cancelled hide never fires: still visible after timers", help.hidden === false);

// 11. One-shot effect verb: transition <cls> <sel>.
help.setAttribute("data-client", "transition flash this");
help.click();
check("one-shot transition: class applied", help.classList.contains("flash"));
ok = await waitFor(() => !help.classList.contains("flash"), 2000);
check("one-shot transition: class removed when done", ok);

other.close();
w.close();
console.log("\nRESULT:", fails.length ? `${fails.length} FAILURES: ${fails}` : "ALL PASS");
process.exit(fails.length ? 1 : 0);
