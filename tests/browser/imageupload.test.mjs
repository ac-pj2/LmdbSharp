// Standalone DOM test of the data-file upload behavior. Loads the real client
// runtime straight from its C# raw-string source (no running server needed),
// binds events via attach(), and verifies that choosing a file reads it into a
// hidden field as a data: URL — the string-valued path the form then submits.
import { JSDOM } from "jsdom";
import { readFileSync } from "node:fs";

const source = readFileSync(
    new URL("../../src/Lmdb.LiveView/ClientRuntime.cs", import.meta.url), "utf8");
const runtime = source.split('"""')[1];   // the JS between the raw-string delimiters

const fails = [];
const check = (name, cond) => {
    console.log((cond ? "PASS " : "FAIL ") + name);
    if (!cond) fails.push(name);
};
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

const dom = new JSDOM(
    `<!doctype html><html><body><div id="app"><form>
        <input type="hidden" name="avatar" id="avatar" value="">
        <input type="file" data-file="#avatar" id="picker">
    </form></div></body></html>`,
    { url: "http://localhost/", pretendToBeVisual: true, runScripts: "dangerously" });

const w = dom.window, d = w.document;
const script = d.createElement("script");
script.textContent = runtime;
d.body.appendChild(script);   // executes in the window context, defining LiveView
check("runtime exposes LiveView.attach", typeof w.LiveView?.attach === "function");
w.LiveView.attach({}, "#app");

const picker = d.querySelector("#picker");
const file = new w.File([new Uint8Array([1, 2, 3])], "a.png", { type: "image/png" });
Object.defineProperty(picker, "files", { value: [file], configurable: true });
picker.dispatchEvent(new w.Event("change", { bubbles: true }));

await sleep(300);   // FileReader is async
const value = d.querySelector("#avatar").value;
check("file read into hidden field as a data: URL",
    value.startsWith("data:image/png;base64,"));
check("decoded bytes round-trip", value.endsWith(",AQID"));   // [1,2,3] -> base64

console.log(fails.length ? `\n${fails.length} FAILED` : "\nALL PASSED");
process.exit(fails.length ? 1 : 0);
