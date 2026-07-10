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
    <div id="log" style="position:fixed;bottom:0;right:0;width:400px;height:200px;overflow:auto;background:#f5f5f5;border:1px solid #ccc;font-family:monospace;font-size:11px;padding:8px;"></div>
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
