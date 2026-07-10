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
namespace Lmdb.LiveView;

public static class ClientRuntime
{
    public const string JavaScript = """
// LMDB LiveView client runtime (no dependencies)
window.LiveView = (function() {
    'use strict';
    let ws = null, root = null, url = null, attempts = 0, heartbeat = 0;
    const nodes = new Map();        // data-lvid -> element
    let queue = [], raf = 0;
    const busy = [];
    const debounces = new WeakMap();
    let bound = false;

    function connect(u, rootSelector) {
        url = u;
        root = document.querySelector(rootSelector) || document.body;
        bindEvents();
        open();
    }

    function open() {
        ws = new WebSocket(url);
        ws.onopen = () => { attempts = 0; };
        ws.onclose = () => { stopHeartbeat(); clearBusy(); reconnect(); };
        ws.onerror = () => {};
        ws.onmessage = (e) => {
            let msg;
            try { msg = JSON.parse(e.data); } catch (err) { return; }
            clearBusy();
            handleMessage(msg);
        };
        startHeartbeat();
    }

    function reconnect() {
        const base = Math.min(30000, 500 * Math.pow(2, attempts++));
        setTimeout(open, base / 2 + Math.random() * base / 2);
    }

    function startHeartbeat() {
        stopHeartbeat();
        heartbeat = setInterval(() => {
            if (ws && ws.readyState === 1) ws.send('{"t":"__ping"}');
        }, 25000);
    }
    function stopHeartbeat() { if (heartbeat) { clearInterval(heartbeat); heartbeat = 0; } }

    function handleMessage(msg) {
        if (msg.t === 'init') {
            queue = [];
            if (raf) { cancelAnimationFrame(raf); raf = 0; }
            root.innerHTML = msg.html;
            nodes.clear();
            indexTree(root);
            return;
        }
        if (Array.isArray(msg)) {
            queue.push.apply(queue, msg);
            if (!raf) raf = requestAnimationFrame(flush);
        }
    }

    function flush() {
        raf = 0;
        const q = queue; queue = [];
        for (const p of q) {
            try { applyPatch(p); }
            catch (err) { console.error('[LiveView] patch failed', p, err); }
        }
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

    // ── patches ──

    function applyPatch(p) {
        const el = findNode(p.id);
        if (!el) return;
        switch (p.t) {
            case 'attr':
                if (p.name === 'value' && 'value' in el) {
                    if (document.activeElement !== el) { el.value = p.val; el.setAttribute('value', p.val); }
                } else {
                    el.setAttribute(p.name, p.val);
                    if (p.name === 'checked' && 'checked' in el && document.activeElement !== el) el.checked = true;
                }
                break;
            case 'delattr':
                el.removeAttribute(p.name);
                if (p.name === 'checked' && 'checked' in el) el.checked = false;
                else if (p.name === 'value' && 'value' in el && document.activeElement !== el) el.value = '';
                break;
            case 'text':
                el.textContent = p.val;
                break;
            case 'replace': {
                const n = fragment(p.html);
                const focus = captureFocus(el);
                unindexTree(el);
                el.replaceWith(n);
                if (n.nodeType === 1) { indexTree(n); restoreFocus(focus, n); }
                break;
            }
            case 'insert': {
                const n = fragment(p.html);
                if (p.pos >= el.children.length) el.appendChild(n);
                else el.insertBefore(n, el.children[p.pos]);
                if (n.nodeType === 1) indexTree(n);
                break;
            }
            case 'remove':
                unindexTree(el);
                el.remove();
                break;
        }
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
            const el = e.target.closest('[data-event]');
            if (!el || el.tagName === 'FORM' || el.tagName === 'INPUT' ||
                el.tagName === 'SELECT' || el.tagName === 'TEXTAREA') return;
            e.preventDefault();
            markBusy(el);
            send(el.dataset.event, payload(el));
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
        if (ws && ws.readyState === 1) ws.send(JSON.stringify({ t: event, d: data || {} }));
    }

    return { connect, send };
})();
""";
}
