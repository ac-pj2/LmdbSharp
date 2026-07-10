using Lmdb.AspNetCore;
using Lmdb.LiveView;
using Lmdb.Objects;
using LiveTodo;

const string HtmlPage = """
<!DOCTYPE html>
<html>
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>Live Todos</title>
    <style>
        :root {
            --bg: #0f1117; --surface: #1a1d27; --text: #e4e6eb; --muted: #8b8f9a;
            --accent: #5b8def; --accent-dim: #3a5a8a; --danger: #ef4444;
            --success: #22c55e; --warning: #f59e0b; --border: #2a2d3a;
            --radius: 10px; --shadow: 0 2px 8px rgba(0,0,0,0.3);
        }
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', system-ui, sans-serif;
            background: var(--bg); color: var(--text);
            display: flex; justify-content: center; min-height: 100vh; padding: 24px;
        }
        #app { width: 100%; max-width: 640px; }
        h1 { font-size: 1.75rem; font-weight: 700; margin-bottom: 4px; }
        h1 small { color: var(--muted); font-weight: 400; font-size: 0.9rem; }
        p { color: var(--muted); margin-bottom: 16px; font-size: 0.9rem; }
        form { display: flex; gap: 8px; margin-bottom: 20px; }
        input, select {
            background: var(--surface); border: 1px solid var(--border);
            color: var(--text); padding: 10px 14px; border-radius: var(--radius);
            font-size: 15px; outline: none; transition: border 0.15s;
        }
        input:focus, select:focus { border-color: var(--accent); }
        input[name="title"] { flex: 1; }
        select { width: auto; cursor: pointer; }
        button {
            background: var(--accent); color: white; border: none; padding: 10px 16px;
            border-radius: var(--radius); font-size: 15px; font-weight: 600;
            cursor: pointer; transition: opacity 0.15s;
        }
        button:hover { opacity: 0.85; }
        button:active { transform: scale(0.96); }
        ul { list-style: none; }
        li {
            display: flex; align-items: center; gap: 12px; padding: 12px 16px;
            background: var(--surface); border: 1px solid var(--border);
            border-radius: var(--radius); margin-bottom: 8px;
            transition: opacity 0.15s; box-shadow: var(--shadow);
        }
        li.done span[data-key="title"] { text-decoration: line-through; color: var(--muted); }
        li.done { opacity: 0.6; }
        li button[data-event="toggle"] {
            background: var(--surface); border: 1px solid var(--border);
            color: var(--success); width: 36px; height: 36px; padding: 0;
            border-radius: 50%; font-size: 16px; flex-shrink: 0;
        }
        li button[data-event="toggle"]:hover { border-color: var(--success); }
        li button[data-event="delete"] {
            background: transparent; color: var(--danger); width: 32px; height: 32px;
            padding: 0; border-radius: 50%; font-size: 18px; flex-shrink: 0;
        }
        li button[data-event="delete"]:hover { background: rgba(239,68,68,0.15); }
        li button[data-event="cancel"] {
            background: var(--surface); border: 1px solid var(--border);
            color: var(--muted); padding: 8px 12px; font-size: 14px;
        }
        li button[data-key="cancel"] {
            background: transparent; border: 1px solid var(--border);
            color: var(--muted); padding: 6px 10px; font-size: 13px;
        }
        span[data-key="title"] { flex: 1; cursor: text; }
        span[data-key="priority"] { font-size: 14px; flex-shrink: 0; }
        .tag {
            background: var(--accent-dim); color: var(--accent); padding: 2px 10px;
            border-radius: 20px; font-size: 0.75rem; font-weight: 600;
            cursor: pointer; flex-shrink: 0; transition: opacity 0.15s;
        }
        .tag:hover { opacity: 0.7; }
        #log {
            position: fixed; bottom: 8px; right: 8px; width: 360px; height: 160px;
            overflow: auto; background: rgba(0,0,0,0.85); border: 1px solid var(--border);
            border-radius: 8px; font-family: 'SF Mono', monospace; font-size: 11px;
            padding: 8px; color: #0f0; display: none;
        }
        div[data-key="tagpanel"] {
            margin-top: 16px; padding: 16px; background: var(--surface);
            border: 1px solid var(--border); border-radius: var(--radius);
        }
        div[data-key="tagpanel"] p { margin-bottom: 8px; }
    </style>
</head>
<body>
    <div id="app"><p style="color:var(--muted)">Connecting...</p></div>
    <div id="log"></div>
    <script src="/ws/client.js"></script>
    <script>
        LiveView.connect((location.protocol === 'https:' ? 'wss:' : 'ws:') + '//' + location.host + '/ws', '#app');
    </script>
</body>
</html>
""";

const string ClientJs = """
window.LiveView = (function() {
    let ws = null;

    function log(msg) {
        const el = document.getElementById('log');
        if (el) { el.innerHTML += msg + '<br>'; el.scrollTop = el.scrollHeight; }
        console.log('[LiveView]', msg);
    }

    function connect(url, rootSelector) {
        const root = document.querySelector(rootSelector) || document.body;
        log('connecting to ' + url);
        ws = new WebSocket(url);
        ws.onopen = () => log('connected');
        ws.onclose = () => { log('disconnected, retrying...'); setTimeout(() => connect(url, rootSelector), 1000); };
        ws.onmessage = (e) => {
            try {
                const msg = JSON.parse(e.data);
                if (msg.t === 'init') {
                    root.innerHTML = msg.html;
                    bindEvents(root);
                    log('init: ' + msg.html.length + ' bytes');
                }
                else if (Array.isArray(msg)) {
                    log('patches: ' + msg.length + ' (' + msg.map(p=>p.t).join(',') + ')');
                    msg.forEach(p => applyPatch(p));
                }
            } catch(err) { log('ERROR: ' + err + ' ' + e.data.substring(0,100)); }
        };
    }

    function bindEvents(root) {
        root.addEventListener('click', (e) => {
            const el = e.target.closest('[data-event]');
            log('click target=' + e.target.tagName + ' closest=' + (el ? el.tagName + ' event=' + el.dataset.event + ' id=' + el.dataset.id : 'null'));
            if (el && el.tagName !== 'FORM') { e.preventDefault(); send(el.dataset.event, {id: el.dataset.id || ''}); }
        });
        root.addEventListener('submit', (e) => {
            const form = e.target.closest('[data-event]');
            if (form) {
                e.preventDefault();
                const data = {};
                new FormData(form).forEach((v, k) => data[k] = v);
                // Include data-* attributes from the form (e.g., data-id)
                if (form.dataset.id) data.id = form.dataset.id;
                log('submit event=' + form.dataset.event + ' data=' + JSON.stringify(data));
                send(form.dataset.event, data);
                form.reset();
            }
        });
    }

    function applyPatch(p) {
        const el = p.id != null ? document.querySelector('[data-lvid="' + p.id + '"]') : null;
        if (!el && p.t !== 'init') { log('PATCH TARGET NOT FOUND: id=' + p.id + ' t=' + p.t); return; }
        switch(p.t) {
            case 'attr': el.setAttribute(p.name, p.val); log('  attr ' + p.id + ' ' + p.name + '=' + p.val); break;
            case 'delattr': el.removeAttribute(p.name); break;
            case 'text': el.textContent = p.val; log('  text ' + p.id + ' = ' + p.val); break;
            case 'replace':
                var tmp = document.createElement('div'); tmp.innerHTML = p.html;
                el.replaceWith(tmp.firstElementChild || document.createTextNode(p.html));
                break;
            case 'insert':
                var ins = document.createElement('div'); ins.innerHTML = p.html;
                var child = ins.firstElementChild || document.createTextNode(p.html);
                if (p.pos >= el.children.length) el.appendChild(child);
                else el.insertBefore(child, el.children[p.pos]);
                log('  insert into ' + p.id + ' pos=' + p.pos);
                break;
            case 'remove': el.remove(); log('  remove ' + p.id); break;
        }
    }

    function send(event, data) {
        log('SEND: t=' + event + ' d=' + JSON.stringify(data));
        if (ws && ws.readyState === 1) ws.send(JSON.stringify({t: event, d: data}));
    }

    return { connect };
})();
""";

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLmdbObjectDatabase(builder.Configuration["TodoDbPath"] ?? "./livetodo-data");
builder.Services.AddCollection<Todo>("todos");

var app = builder.Build();
app.UseWebSockets();

app.MapGet("/", () => Results.Content(HtmlPage, "text/html"));

LiveViewHub? hubRef = null;
hubRef = new LiveViewHub(name => new TodoLiveView(
    app.Services.GetRequiredService<Collection<Todo>>(),
    hubRef!));

app.MapGet("/ws", async (HttpContext ctx) =>
{
    if (ctx.WebSockets.IsWebSocketRequest)
    {
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        await hubRef!.HandleConnectionAsync(ws, "TodoLiveView");
    }
    else ctx.Response.StatusCode = 400;
});

app.MapGet("/ws/client.js", () => Results.Text(ClientJs, "application/javascript"));
app.Run();
