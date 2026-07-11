// ClientRuntime: the minimal JavaScript (~4KB) that runs in the browser.
// It opens the WebSocket, applies DOM patches, and sends user events to the server.
//
// Usage in HTML (inline for zero extra requests):
//   <div id="app">...server-rendered HTML...</div>
//   <script>/* ClientRuntime.JavaScript */</script>
//   <script>LiveView.connect("/ws", "#app")</script>
//
// Features:
//   - id → element Map (no document-wide selector scans per patch)
//   - patches batched and applied in one requestAnimationFrame
//   - exponential backoff + jitter on reconnect, app-level heartbeat
//   - focused inputs are never clobbered by patches (value + selection preserved)
//   - elements that trigger events get an lv-busy class (+ disabled) until the
//     server responds — style ".lv-busy { opacity: .6 }" for instant feedback
//   - data-debounce="300" on an input sends its data-event debounced while typing
//
// Client-side commands (zero network round trip, Phoenix JS-commands style):
//   data-client="toggle #menu"                 show/hide via the hidden property
//   data-client="show #menu" / "hide #menu"
//   data-client="class open #nav"              toggle a class
//   data-client="addclass x #a; removeclass y #b; focus #input"  (chain with ;)
//   Target "this" refers to the element itself. An element may have BOTH
//   data-client (runs instantly) and data-event (sent to the server).
//
// Transitions: append "with <name>" to toggle/show/hide —
//   data-client="toggle #menu with fade"
//   Showing removes hidden then plays the "<name>-in" class; hiding plays
//   "<name>-out" and sets hidden when the animation completes. Define both in
//   CSS (animation or transition); completion is animationend/transitionend
//   with a computed-duration timeout fallback, so a missing animation just
//   degrades to instant. Rapid re-toggles cancel the in-flight animation.
//   Honors prefers-reduced-motion (skips straight to the end state).
//   There is also a one-shot effect verb:
//   data-client="transition shake #form"       add class, remove when done
//   Local changes are remembered per node and re-applied when server patches
//   touch the same elements, so a locally-opened menu survives unrelated
//   updates. Use classes the server templates don't compute (e.g. "open"), so
//   local UI state and server state never fight over the same token.
//
//   data-lv-ignore on an element makes patches skip its children (and its own
//   replacement) — for client-owned DOM like charts or third-party widgets.
//   Attribute patches on the ignored element itself still apply.
namespace Lmdb.LiveView;

public static class ClientRuntime
{
    public const string JavaScript = """
// LMDB LiveView client runtime (no dependencies)
window.LiveView = (function() {
    'use strict';
    let ws = null, root = null, url = null, attempts = 0, heartbeat = 0, ssrFp = null;
    let sid = null, lastSeq = 0;    // session id + last applied seq (for resume)
    let closed = false;             // disconnect() sets — stops the reconnect loop
    let popstateHandler = null;
    const nodes = new Map();        // data-lvid -> element
    let queue = [], raf = 0;
    const busy = [];
    const debounces = new WeakMap();
    let bound = false;

    // Observability hook: LiveView.debug(fn) receives
    //   {t:'open'|'close'} | {t:'recv', kind, bytes, count, patches}
    //   {t:'applied', count, ms} | {t:'send', event, bytes}
    let dbg = null;
    function debug(fn) { dbg = fn; }
    function emit(evt) {
        devEvent(evt);
        if (dbg) { try { dbg(evt); } catch (err) {} }
    }

    // Built-in dev panel: activates when DevPanel markup (#lv-dev-client) exists.
    const devs = { recv: 0, sent: 0, bin: 0, bout: 0, ops: 0, applyMs: 0, reconnects: 0, state: 'connecting' };
    const devlog = [];
    function devEvent(e) {
        if (e.t === 'open') devs.state = 'connected';
        else if (e.t === 'close') { devs.state = 'reconnecting…'; devs.reconnects++; }
        else if (e.t === 'recv') {
            devs.recv++; devs.bin += e.bytes;
            let line;
            if (e.patches) {
                devs.ops += e.count;
                const ops = {};
                for (const p of e.patches) ops[p.t] = (ops[p.t] || 0) + 1;
                line = '<b>' + e.bytes + 'B</b> ' + Object.entries(ops).map(([k, v]) => k + '×' + v).join(' ');
            } else {
                line = '<b>' + e.bytes + 'B</b> ' + e.kind
                    + (e.kind === 'ok' ? ' (SSR adopted, HTML not re-sent)'
                     : e.kind === 'r' ? ' (session resumed, missed patches replayed)' : '');
            }
            devlog.unshift(line);
            if (devlog.length > 7) devlog.pop();
        }
        else if (e.t === 'applied') devs.applyMs = e.ms;
        else if (e.t === 'send') { devs.sent++; devs.bout += e.bytes; }
        renderDevPanel();
    }
    function fmtBytes(b) {
        return b > 1048576 ? (b / 1048576).toFixed(1) + ' MB'
             : b > 1024 ? (b / 1024).toFixed(1) + ' KB' : b + ' B';
    }
    function renderDevPanel() {
        const c = document.getElementById('lv-dev-client');
        if (!c) return;
        c.innerHTML = '<h3>client · this browser</h3>' + [
            ['status', devs.state],
            ['msgs in / out', devs.recv + ' / ' + devs.sent],
            ['bytes in / out', fmtBytes(devs.bin) + ' / ' + fmtBytes(devs.bout)],
            ['patch ops applied', devs.ops],
            ['last apply', devs.applyMs.toFixed(1) + ' ms'],
            ['reconnects', devs.reconnects],
        ].map(([k, v]) => '<div class="lv-dev-row"><small>' + k + '</small><span>' + v + '</span></div>').join('');
        const l = document.getElementById('lv-dev-log');
        if (l) l.innerHTML = '<h3>wire · last frames</h3>' + devlog.map(o => '<div class="lv-dev-op">' + o + '</div>').join('');
    }

    function connect(u, rootSelector, fp) {
        url = u;
        ssrFp = fp || null;
        closed = false;
        attempts = 0;
        root = document.querySelector(rootSelector) || document.body;
        bindEvents();
        // Back/forward buttons echo the new path to the server (live navigation).
        if (popstateHandler) window.removeEventListener('popstate', popstateHandler);
        popstateHandler = () => {
            send('__nav', { path: location.pathname + location.search });
        };
        window.addEventListener('popstate', popstateHandler);
        open();
    }

    // Tear the connection down for good — embedded hosts (SPA route unmount)
    // call this so the socket and reconnect loop don't outlive the surface.
    function disconnect() {
        closed = true;
        stopHeartbeat();
        if (popstateHandler) { window.removeEventListener('popstate', popstateHandler); popstateHandler = null; }
        if (ws) { ws.onclose = null; try { ws.close(); } catch (err) {} ws = null; }
        sid = null; lastSeq = 0;
    }

    function open() {
        // First connect: echo the SSR fingerprint — if server state is unchanged
        // it replies {"t":"ok"} instead of re-sending the page. Reconnects: send
        // the session id + last applied seq — the server replays exactly the
        // missed messages (or a fresh init if the session expired).
        const sep = () => url.includes('?') ? '&' : '?';
        let u = url;
        if (ssrFp) { u += sep() + 'fp=' + ssrFp; ssrFp = null; }
        else if (sid) { u += sep() + 'resume=' + sid + '&seq=' + lastSeq; }
        ws = new WebSocket(u);
        ws.onopen = () => { attempts = 0; emit({ t: 'open' }); };
        ws.onclose = () => { stopHeartbeat(); clearBusy(); emit({ t: 'close' }); reconnect(); };
        ws.onerror = () => {};
        ws.onmessage = (e) => {
            let msg;
            try { msg = JSON.parse(e.data); } catch (err) { return; }
            emit({ t: 'recv', kind: msg.t, bytes: e.data.length,
                   count: msg.p ? msg.p.length : 0, patches: msg.p || null });
            clearBusy();
            handleMessage(msg);
        };
        startHeartbeat();
    }

    function reconnect() {
        if (closed) return;
        const base = Math.min(30000, 500 * Math.pow(2, attempts++));
        setTimeout(() => { if (!closed) open(); }, base / 2 + Math.random() * base / 2);
    }

    function startHeartbeat() {
        stopHeartbeat();
        heartbeat = setInterval(() => {
            if (ws && ws.readyState === 1) ws.send('{"t":"__ping"}');
        }, 25000);
    }
    function stopHeartbeat() { if (heartbeat) { clearInterval(heartbeat); heartbeat = 0; } }

    function handleMessage(msg) {
        if (typeof msg.s === 'number') lastSeq = msg.s;
        if (msg.sid) sid = msg.sid;
        if (msg.t === 'ok' || msg.t === 'r') {  // SSR adopted / session resumed
            if (msg.t === 'ok') { nodes.clear(); indexTree(root); }
            return;
        }
        if (msg.t === 'nav') {  // live navigation — server already re-rendered
            history.pushState({ lv: true }, '', msg.url);
            return;
        }
        if (msg.t === 'init') {
            queue = [];
            if (raf) { cancelAnimationFrame(raf); raf = 0; }
            root.innerHTML = msg.html;
            nodes.clear();
            local.clear();  // fresh page — local UI state resets
            tpls.clear();   // server resets its sent-template tracking on init
            indexTree(root);
            return;
        }
        if (msg.t === 'p' && msg.p) {
            queue.push.apply(queue, msg.p);
            if (!raf) raf = requestAnimationFrame(flush);
        }
    }

    function flush() {
        raf = 0;
        const q = queue; queue = [];
        const t0 = performance.now();
        for (const p of q) {
            try { applyPatch(p); }
            catch (err) { console.error('[LiveView] patch failed', p, err); }
        }
        emit({ t: 'applied', count: q.length, ms: performance.now() - t0 });
    }

    // ── id → element map ──

    function indexTree(el) {
        if (el.nodeType !== 1) return;
        if (el.hasAttribute('data-lvid')) nodes.set(el.getAttribute('data-lvid'), el);
        const all = el.querySelectorAll('[data-lvid]');
        for (let i = 0; i < all.length; i++) nodes.set(all[i].getAttribute('data-lvid'), all[i]);
    }
    function unindexTree(el) {
        if (el.nodeType !== 1) return;
        if (el.hasAttribute('data-lvid')) nodes.delete(el.getAttribute('data-lvid'));
        const all = el.querySelectorAll('[data-lvid]');
        for (let i = 0; i < all.length; i++) nodes.delete(all[i].getAttribute('data-lvid'));
    }
    function findNode(id) {
        const key = String(id);
        let el = nodes.get(key);
        if (el && el.isConnected) return el;
        el = document.querySelector('[data-lvid="' + key + '"]');
        if (el) nodes.set(key, el);
        return el;
    }

    function fragment(html) {
        const t = document.createElement('template');
        t.innerHTML = html;
        return t.content.firstChild || document.createTextNode('');
    }

    // ── statics/dynamics templates ──
    // The server sends a subtree's shape (tags, attr names) once per structure;
    // repeats arrive as {tpl, bid, d:[values]} and are instantiated locally.
    // IDs are assigned depth-first from bid — the same walk the server used.
    // Values land via setAttribute/createTextNode, never innerHTML.
    const tpls = new Map();  // hash -> def

    function materialize(p) {
        if (p.html) {
            if (p.tpl && p.def) tpls.set(p.tpl, p.def);  // cache statics
            return fragment(p.html);
        }
        const def = tpls.get(p.tpl);
        if (!def) { console.error('[LiveView] unknown template', p.tpl); return null; }
        const st = { di: 0, id: p.bid };
        return buildTpl(def, st, p.d);
    }

    function buildTpl(def, st, d) {
        if (def === 0) { st.id++; return document.createTextNode(d[st.di++]); }
        const el = document.createElement(def.e);
        el.setAttribute('data-lvid', st.id++);
        if (def.a) for (const name of def.a) el.setAttribute(name, d[st.di++]);
        if (def.c) for (const c of def.c) el.appendChild(buildTpl(c, st, d));
        return el;
    }

    // ── patches ──

    function applyPatch(p) {
        const el = findNode(p.id);
        if (!el) return;
        // Client-owned subtrees: children are never patched; the ignored element
        // itself only accepts attribute patches.
        const ig = el.closest('[data-lv-ignore]');
        if (ig && (ig !== el || (p.t !== 'attr' && p.t !== 'delattr'))) return;
        switch (p.t) {
            case 'attr':
                if (p.name === 'value' && 'value' in el) {
                    if (document.activeElement !== el) { el.value = p.val; el.setAttribute('value', p.val); }
                } else {
                    el.setAttribute(p.name, p.val);
                    if (p.name === 'checked' && 'checked' in el && document.activeElement !== el) el.checked = true;
                    reapplyLocal(el); // server rewrote class/hidden — merge local UI state back
                }
                break;
            case 'delattr':
                el.removeAttribute(p.name);
                if (p.name === 'checked' && 'checked' in el) el.checked = false;
                else if (p.name === 'value' && 'value' in el && document.activeElement !== el) el.value = '';
                reapplyLocal(el);
                break;
            case 'text':
                el.textContent = p.val;
                break;
            case 'replace': {
                const n = materialize(p);
                if (!n) return;
                const focus = captureFocus(el);
                unindexTree(el);
                el.replaceWith(n);
                if (n.nodeType === 1) { indexTree(n); reapplyLocalTree(n); restoreFocus(focus, n); }
                break;
            }
            case 'insert': {
                const n = materialize(p);
                if (!n) return;
                if (p.pos >= el.children.length) el.appendChild(n);
                else el.insertBefore(n, el.children[p.pos]);
                if (n.nodeType === 1) indexTree(n);
                break;
            }
            case 'remove':
                unindexTree(el);
                dropLocal(el);
                el.remove();
                break;
        }
    }

    // ── client-side commands (no network) ──
    // Local overrides are keyed by data-lvid and re-applied when server patches
    // touch the same nodes, so pure-UI state survives unrelated updates.

    const local = new Map();  // data-lvid -> { hidden: bool|undefined, cls: Map<name, on> }

    function runClient(el) {
        for (const cmd of el.dataset.client.split(';')) {
            let parts = cmd.trim().split(/\s+/);
            if (parts.length < 2) continue;
            const verb = parts[0];
            let trans = null;
            if (parts.length >= 4 && parts[parts.length - 2] === 'with') {
                trans = parts[parts.length - 1];
                parts = parts.slice(0, -2);
            }
            const hasCls = verb === 'class' || verb === 'addclass' ||
                           verb === 'removeclass' || verb === 'transition';
            const cls = hasCls ? parts[1] : null;
            const sel = parts.slice(hasCls ? 2 : 1).join(' ');
            const t = sel === 'this' ? el : document.querySelector(sel);
            if (!t) continue;
            switch (verb) {
                case 'toggle': setHiddenWith(t, !effectiveHidden(t), trans); break;
                case 'show': setHiddenWith(t, false, trans); break;
                case 'hide': setHiddenWith(t, true, trans); break;
                case 'class': setClass(t, cls, !t.classList.contains(cls)); break;
                case 'addclass': setClass(t, cls, true); break;
                case 'removeclass': setClass(t, cls, false); break;
                case 'focus': t.focus(); break;
                case 'transition': {
                    t.classList.add(cls);
                    afterAnim(t, () => t.classList.remove(cls));
                    break;
                }
            }
        }
    }

    // ── transitions ──
    // Convention: "with fade" plays class "fade-in" on show and "fade-out" on
    // hide. hidden is removed before -in and set after -out completes.

    const anims = new WeakMap();  // el -> { dir, cancel }

    function reducedMotion() {
        try { return matchMedia('(prefers-reduced-motion: reduce)').matches; }
        catch (err) { return false; }
    }

    function effectiveHidden(t) {
        const a = anims.get(t);
        return a ? a.dir === 'out' : t.hidden;  // mid-animation, use the target state
    }

    function animMs(t) {
        const cs = getComputedStyle(t);
        const ms = (v) => (v || '').split(',')
            .reduce((m, s) => Math.max(m, (parseFloat(s) || 0) * (s.includes('ms') ? 1 : 1000)), 0);
        return Math.max(ms(cs.animationDuration) + ms(cs.animationDelay),
                        ms(cs.transitionDuration) + ms(cs.transitionDelay));
    }

    // Runs done() on animationend/transitionend, with a computed-duration
    // timeout fallback. Returns a disarm function (cancels without running
    // done), or null if there is no animation and done ran synchronously.
    function afterAnim(t, done) {
        const total = animMs(t);
        if (total <= 0) { done(); return null; }
        let armed = true;
        const disarm = () => {
            if (!armed) return false;
            armed = false;
            t.removeEventListener('animationend', onEnd);
            t.removeEventListener('transitionend', onEnd);
            clearTimeout(timer);
            return true;
        };
        const finish = () => { if (disarm()) done(); };
        const onEnd = (e) => { if (e.target === t) finish(); };
        t.addEventListener('animationend', onEnd);
        t.addEventListener('transitionend', onEnd);
        const timer = setTimeout(finish, total + 100);
        return disarm;
    }

    function cancelAnim(t) {
        const a = anims.get(t);
        if (a) { anims.delete(t); a.cancel(); }
    }

    function setHiddenWith(t, hidden, trans) {
        cancelAnim(t);
        if (!trans || reducedMotion()) { setHidden(t, hidden); return; }
        const inCls = trans + '-in', outCls = trans + '-out';
        // Record the TARGET state immediately so server patches merging local
        // state land on the intended end state, not a mid-animation frame.
        const s = localState(t);
        if (s) s.hidden = hidden;
        if (!hidden) {
            t.classList.remove(outCls);
            t.hidden = false;
            t.classList.add(inCls);
            const disarm = afterAnim(t, () => { anims.delete(t); t.classList.remove(inCls); });
            if (disarm) anims.set(t, { dir: 'in',
                cancel: () => { disarm(); t.classList.remove(inCls); } });
        } else {
            t.classList.remove(inCls);
            t.classList.add(outCls);
            const disarm = afterAnim(t, () => { anims.delete(t); t.classList.remove(outCls); t.hidden = true; });
            if (disarm) anims.set(t, { dir: 'out',
                cancel: () => { disarm(); t.classList.remove(outCls); } });
        }
    }

    function localState(t) {
        const id = t.getAttribute('data-lvid');
        if (!id) return null;
        let s = local.get(id);
        if (!s) { s = { hidden: undefined, cls: new Map() }; local.set(id, s); }
        return s;
    }
    function setHidden(t, hidden) {
        t.hidden = hidden;
        const s = localState(t);
        if (s) s.hidden = hidden;
    }
    function setClass(t, cls, on) {
        t.classList.toggle(cls, on);
        const s = localState(t);
        if (s) s.cls.set(cls, on);
    }
    function reapplyLocal(el) {
        const id = el.getAttribute('data-lvid');
        const s = id && local.get(id);
        if (!s) return;
        if (s.hidden !== undefined) el.hidden = s.hidden;
        s.cls.forEach((on, c) => el.classList.toggle(c, on));
    }
    function reapplyLocalTree(el) {
        reapplyLocal(el);
        const all = el.querySelectorAll('[data-lvid]');
        for (let i = 0; i < all.length; i++) reapplyLocal(all[i]);
    }
    function dropLocal(el) {
        if (el.hasAttribute('data-lvid')) local.delete(el.getAttribute('data-lvid'));
        const all = el.querySelectorAll('[data-lvid]');
        for (let i = 0; i < all.length; i++) local.delete(all[i].getAttribute('data-lvid'));
    }

    // Don't lose focus/typing when the subtree containing the focused input is replaced.
    function captureFocus(container) {
        const ae = document.activeElement;
        if (!ae || !container.contains(ae) || !ae.hasAttribute('data-lvid')) return null;
        return { id: ae.getAttribute('data-lvid'), value: ae.value,
                 start: ae.selectionStart, end: ae.selectionEnd };
    }
    function restoreFocus(f, tree) {
        if (!f) return;
        const el = tree.getAttribute('data-lvid') === f.id ? tree
            : tree.querySelector('[data-lvid="' + f.id + '"]');
        if (!el) return;
        el.focus();
        if (f.value !== undefined && 'value' in el) {
            el.value = f.value;
            try { el.setSelectionRange(f.start, f.end); } catch (err) {}
        }
    }

    // ── events (bound once; delegated, so they survive any patching) ──

    function bindEvents() {
        if (bound) return;
        bound = true;
        document.addEventListener('click', (e) => {
            const el = e.target.closest('[data-event],[data-client]');
            if (!el || el.tagName === 'FORM' || el.tagName === 'INPUT' ||
                el.tagName === 'SELECT' || el.tagName === 'TEXTAREA') return;
            e.preventDefault();
            if (el.dataset.client) runClient(el);       // instant, no network
            if (el.dataset.event) {                     // server round trip
                markBusy(el);
                send(el.dataset.event, payload(el));
            }
        });
        document.addEventListener('submit', (e) => {
            const form = e.target.closest('form[data-event]');
            if (!form) return;
            e.preventDefault();
            const data = payload(form);
            new FormData(form).forEach((v, k) => { data[k] = v; });
            markBusy(form.querySelector('button:not([type]),button[type=submit]'));
            send(form.dataset.event, data);
            if (!form.hasAttribute('data-no-reset')) form.reset();
        });
        document.addEventListener('change', (e) => {
            const el = e.target.closest('[data-event="change"]');
            if (el) send('change', { name: el.name, value: el.value });
        });
        document.addEventListener('input', (e) => {
            const el = e.target;
            if (!el.dataset || !el.dataset.event || el.dataset.debounce === undefined) return;
            const ms = parseInt(el.dataset.debounce, 10) || 300;
            clearTimeout(debounces.get(el));
            debounces.set(el, setTimeout(() => {
                const data = payload(el);
                data.name = el.name; data.value = el.value;
                send(el.dataset.event, data);
            }, ms));
        });
    }

    function payload(el) {
        const d = {};
        for (const k in el.dataset)
            if (k !== 'event' && k !== 'lvid' && k !== 'debounce' && k !== 'key') d[k] = el.dataset[k];
        return d;
    }

    function markBusy(el) {
        if (!el) return;
        el.classList.add('lv-busy');
        if ('disabled' in el) el.disabled = true;
        busy.push(el);
    }
    function clearBusy() {
        while (busy.length) {
            const el = busy.pop();
            el.classList.remove('lv-busy');
            if ('disabled' in el) el.disabled = false;
        }
    }

    function send(event, data) {
        if (ws && ws.readyState === 1) {
            const frame = JSON.stringify({ t: event, d: data || {} });
            emit({ t: 'send', event: event, bytes: frame.length });
            ws.send(frame);
        }
    }

    return { connect, disconnect, send, debug };
})();
""";
}
