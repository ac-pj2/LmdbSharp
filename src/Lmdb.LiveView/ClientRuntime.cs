// ClientRuntime: the minimal JavaScript (~3KB) that runs in the browser.
// It opens the WebSocket, applies DOM patches, and sends user events to the server.
//
// Usage in HTML:
//   <div id="app"></div>
//   <script src="/ws/client.js"></script>
//   <script>LiveView.connect("/ws", "app")</script>
namespace Lmdb.LiveView;

internal static class ClientRuntime
{
    internal const string JavaScript = """
// LMDB LiveView client runtime (~2KB, no dependencies)
window.LiveView = (function() {
    let ws = null;
    let rootId = null;

    function connect(url, rootSelector) {
        const root = document.querySelector(rootSelector) || document.body;
        ws = new WebSocket(url);
        ws.onopen = () => console.log('[LiveView] connected');
        ws.onclose = () => { console.log('[LiveView] disconnected, retrying...'); setTimeout(() => connect(url, rootSelector), 1000); };
        ws.onerror = (e) => console.error('[LiveView] error', e);
        ws.onmessage = (e) => {
            try { handleMessage(JSON.parse(e.data), root); }
            catch(err) { console.error('[LiveView] patch error', err, e.data); }
        };
        // Intercept clicks on [data-event] elements
        document.addEventListener('click', (e) => {
            const el = e.target.closest('[data-event]');
            if (el) { e.preventDefault(); send(el.dataset.event, el.dataset); }
        });
        // Intercept form submits with [data-event]
        document.addEventListener('submit', (e) => {
            const form = e.target.closest('[data-event]');
            if (form) {
                e.preventDefault();
                const data = {};
                new FormData(form).forEach((v, k) => data[k] = v);
                send(form.dataset.event, data);
            }
        });
        // Intercept input changes with [data-event="input"]
        document.addEventListener('change', (e) => {
            const el = e.target.closest('[data-event="change"]');
            if (el) { e.preventDefault(); send('change', {name: el.name, value: el.value}); }
        });
    }

    function handleMessage(msg, root) {
        if (msg.t === 'init') { root.innerHTML = msg.html; return; }
        if (Array.isArray(msg)) { msg.forEach(p => applyPatch(p)); }
    }

    function applyPatch(p) {
        const el = p.id != null ? findNode(p.id) : null;
        switch(p.t) {
            case 'attr': el.setAttribute(p.name, p.val); break;
            case 'delattr': el.removeAttribute(p.name); break;
            case 'text': el.textContent = p.val; break;
            case 'replace':
                const tmp = document.createElement('div');
                tmp.innerHTML = p.html;
                el.replaceWith(tmp.firstElementChild || document.createTextNode(p.html));
                break;
            case 'insert':
                const ins = document.createElement('div');
                ins.innerHTML = p.html;
                const child = ins.firstElementChild || document.createTextNode(p.html);
                if (p.pos >= el.children.length) el.appendChild(child);
                else el.insertBefore(child, el.children[p.pos]);
                break;
            case 'remove': el.remove(); break;
        }
    }

    // Node lookup via data-lvid attribute (assigned during init/patch).
    function findNode(id) {
        return document.querySelector('[data-lvid="' + id + '"]') || document.body;
    }

    function send(event, data) {
        if (ws && ws.readyState === 1) ws.send(JSON.stringify({t: event, d: data}));
    }

    return { connect };
})();
""";
}
