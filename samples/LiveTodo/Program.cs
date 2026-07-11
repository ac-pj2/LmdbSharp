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
        .lv-busy { opacity: 0.5; pointer-events: none; }
        .header { display: flex; align-items: center; justify-content: space-between; }
        .help-btn {
            background: var(--surface); border: 1px solid var(--border);
            color: var(--muted); width: 32px; height: 32px; padding: 0;
            border-radius: 50%; font-size: 15px; flex-shrink: 0;
        }
        #help {
            background: var(--surface); border: 1px solid var(--border);
            border-radius: var(--radius); padding: 12px 16px; margin: 8px 0 12px;
        }
        #help p { margin: 0; }
        /* "with fade" transition: <name>-in plays on show, <name>-out on hide */
        .fade-in { animation: lv-fade-in 0.18s ease-out; }
        .fade-out { animation: lv-fade-out 0.15s ease-in forwards; }
        @keyframes lv-fade-in { from { opacity: 0; transform: translateY(-6px); } to { opacity: 1; transform: none; } }
        @keyframes lv-fade-out { from { opacity: 1; } to { opacity: 0; transform: translateY(-6px); } }
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
        span[data-key="title"] { flex: 1; cursor: text; }
        span[data-key="priority"] { font-size: 14px; flex-shrink: 0; }
        .tag {
            background: var(--accent-dim); color: var(--accent); padding: 2px 10px;
            border-radius: 20px; font-size: 0.75rem; font-weight: 600;
            cursor: pointer; flex-shrink: 0; transition: opacity 0.15s;
        }
        .tag:hover { opacity: 0.7; }
    </style>
</head>
<body>
    <div id="app"><!--SSR--></div>
    <script>/*CLIENT_JS*/</script>
    <script>
        LiveView.connect((location.protocol === 'https:' ? 'wss:' : 'ws:') + '//' + location.host + '/ws', '#app', '/*FP*/');
    </script>
</body>
</html>
""";

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLmdbObjectDatabase(builder.Configuration["TodoDbPath"] ?? "./livetodo-data");
builder.Services.AddCollection<Todo>("todos");

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

LiveViewHub? hubRef = null;
hubRef = new LiveViewHub(name => new TodoLiveView(
    app.Services.GetRequiredService<Collection<Todo>>(),
    hubRef!));

// Server-side render: the user sees content on first paint, before the
// WebSocket connects. The runtime is inlined — zero extra requests. The
// fingerprint lets the WS connect skip re-sending identical HTML.
app.MapGet("/", () =>
{
    var ssr = hubRef!.RenderInitialHtml("TodoLiveView");
    return Results.Content(
        HtmlPage.Replace("<!--SSR-->", ssr)
                .Replace("/*CLIENT_JS*/", ClientRuntime.JavaScript)
                .Replace("/*FP*/", DeltaLiveView.Fingerprint(ssr)),
        "text/html");
});

app.MapGet("/ws", async (HttpContext ctx) =>
{
    if (ctx.WebSockets.IsWebSocketRequest)
    {
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync(
            new WebSocketAcceptContext { DangerousEnableCompression = true });
        await hubRef!.HandleConnectionAsync(ws, "TodoLiveView",
            ctx.Request.Query["fp"].FirstOrDefault());
    }
    else ctx.Response.StatusCode = 400;
});

app.Run();
