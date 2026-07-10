using Lmdb.AspNetCore;
using Lmdb.LiveView;
using Lmdb.Objects;
using LiveTodo;

const string HtmlPage = """
<!DOCTYPE html>
<html>
<head>
    <meta charset="utf-8">
    <title>Live Todos</title>
    <style>
        body { font-family: system-ui, sans-serif; max-width: 600px; margin: 40px auto; }
        .done span { text-decoration: line-through; color: #999; }
        li { list-style: none; padding: 8px 0; display: flex; align-items: center; gap: 8px; }
        form { display: flex; gap: 8px; margin-bottom: 20px; }
        input { flex: 1; padding: 8px; font-size: 16px; }
        button { padding: 8px 12px; cursor: pointer; }
    </style>
</head>
<body>
    <div id="app"><p>Connecting...</p></div>
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

    function connect(url, rootSelector) {
        const root = document.querySelector(rootSelector) || document.body;
        ws = new WebSocket(url);
        ws.onopen = () => console.log('[LiveView] connected');
        ws.onclose = () => { console.log('[LiveView] retrying...'); setTimeout(() => connect(url, rootSelector), 1000); };
        ws.onmessage = (e) => {
            try {
                const msg = JSON.parse(e.data);
                if (msg.t === 'init') { root.innerHTML = msg.html; bindEvents(root); }
                else if (Array.isArray(msg)) { msg.forEach(p => applyPatch(p)); }
            } catch(err) { console.error('[LiveView]', err, e.data); }
        };
    }

    function bindEvents(root) {
        root.addEventListener('click', (e) => {
            const el = e.target.closest('[data-event]');
            if (el && el.tagName !== 'FORM') { e.preventDefault(); send(el.dataset.event, {id: el.dataset.id || ''}); }
        });
        root.addEventListener('submit', (e) => {
            const form = e.target.closest('[data-event]');
            if (form) {
                e.preventDefault();
                const data = {};
                new FormData(form).forEach((v, k) => data[k] = v);
                send(form.dataset.event, data);
                form.reset();
            }
        });
    }

    function applyPatch(p) {
        const el = p.id != null ? document.querySelector('[data-lvid="' + p.id + '"]') : null;
        if (!el && p.t !== 'init') return;
        switch(p.t) {
            case 'attr': el.setAttribute(p.name, p.val); break;
            case 'delattr': el.removeAttribute(p.name); break;
            case 'text': el.textContent = p.val; break;
            case 'replace':
                var tmp = document.createElement('div'); tmp.innerHTML = p.html;
                el.replaceWith(tmp.firstElementChild || document.createTextNode(p.html));
                break;
            case 'insert':
                var ins = document.createElement('div'); ins.innerHTML = p.html;
                var child = ins.firstElementChild || document.createTextNode(p.html);
                if (p.pos >= el.children.length) el.appendChild(child);
                else el.insertBefore(child, el.children[p.pos]);
                break;
            case 'remove': el.remove(); break;
        }
    }

    function send(event, data) {
        if (ws && ws.readyState === 1) ws.send(JSON.stringify({t: event, d: data}));
    }

    return { connect };
})();
""";

var builder = WebApplication.CreateBuilder(args);

// Register the object database.
builder.Services.AddLmdbObjectDatabase(builder.Configuration["TodoDbPath"] ?? "./livetodo-data");
builder.Services.AddCollection<Todo>("todos");

var app = builder.Build();
app.UseWebSockets();

// Serve the HTML page.
app.MapGet("/", () => Results.Content(HtmlPage, "text/html"));

// LiveView hub — needs lazy init for the circular reference (hub passes itself to views).
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
    else
    {
        ctx.Response.StatusCode = 400;
    }
});

app.MapGet("/ws/client.js", () => Results.Text(ClientJs, "application/javascript"));

app.Run();
