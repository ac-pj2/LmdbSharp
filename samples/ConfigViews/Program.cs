// ConfigViews PoC — p2's config-driven views rendered by the LiveView engine.
//
//   · reads the REAL coaching-hub config (views/*.json, entity-types/*.json)
//     from the p2 checkout — the same files its React SPA interprets
//   · evaluates the config's reactive expressions with p2's own server-side
//     engine (Core.Expressions.Reactive/Jint), referenced unmodified
//   · SSR first paint + fingerprint join, live navigation (pushState),
//     granular patches, topics, presence, session resume, DevPanel — all on
//     a ~5KB inline runtime; no React, no client expression engine
//   · entities live in LMDB for the demo (a real integration would put
//     EF Core/PostgreSQL behind the same EntityStore surface)
//
// Run:  dotnet run --project samples/ConfigViews -- ForumDbPath=/tmp/forum
//       (ConfigDir overrides the default p2 checkout location)
using ConfigViews;
using Core.Expressions.Reactive;
using Lmdb.AspNetCore;
using Lmdb.LiveView;
using Lmdb.Objects;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IReactiveExpressionEvaluator, JintReactiveExpressionEvaluator>();
builder.Services.AddSingleton(_ => ConfigLoader.Load(
    builder.Configuration["ConfigDir"]
    ?? "/home/devuser1/code/p2/coaching-config-staging/DEFAULT-001/coaching-hub"));
builder.Services.AddLiveView<ConfigLiveView>();

// Data backend: "lmdb" (self-contained demo, seeded) or "p2" (Phase 1 —
// reads the live platform's PostgreSQL, writes through its REST API, and
// bridges its mutation stream into LiveView topics).
var storeMode = builder.Configuration["Store"] ?? "lmdb";
if (storeMode == "p2")
{
    var p2 = new P2Options();
    builder.Configuration.GetSection("P2").Bind(p2);
    builder.Services.AddSingleton(p2);
    builder.Services.AddSingleton<P2EntityStore>();
    builder.Services.AddSingleton<IEntityStore>(sp => sp.GetRequiredService<P2EntityStore>());
    // Phase 2: the durable LMDB projection — loads from disk in µs, reconciles
    // against PostgreSQL for anything missed while down, rebuildable any time.
    builder.Services.AddSingleton<IRecordProjection>(sp => new LmdbProjection(
        sp.GetRequiredService<P2Options>(),
        builder.Configuration["ProjectionPath"] ?? "./configviews-projection",
        rebuild: builder.Configuration["RebuildProjection"] == "true"));
    builder.Services.AddHostedService<MutationBridge>();
}
else
{
    builder.Services.AddLmdbObjectDatabase(builder.Configuration["ForumDbPath"] ?? "./configviews-data");
    builder.Services.AddCollection<EntityRecord>("records");
    builder.Services.AddSingleton<IEntityStore, LmdbEntityStore>();
    builder.Services.AddSingleton<IRecordProjection, RecordCache>();
}

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

var projection = app.Services.GetRequiredService<IRecordProjection>();
if (storeMode != "p2")   // LmdbProjection fills itself (disk load + reconcile)
    projection.Fill(app.Services.GetRequiredService<IEntityStore>().LoadAll());
Console.WriteLine($"[projection] {projection.Describe()}");

var hub = app.Services.GetRequiredService<LiveViewHub>();

app.MapGet("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync(
        new WebSocketAcceptContext { DangerousEnableCompression = true });
    var path = ctx.Request.Query["path"].FirstOrDefault() ?? "/";
    long.TryParse(ctx.Request.Query["seq"].FirstOrDefault(), out var seq);
    await hub.HandleConnectionAsync(ws, "ConfigLiveView",
        ctx.Request.Query["fp"].FirstOrDefault(),
        ctx.Request.Query["resume"].FirstOrDefault(), seq,
        configure: view => ((ConfigLiveView)view).SetInitialPath(path));
});

// Catch-all: every route SSRs the matching configured view into the shell.
app.MapGet("/{**path}", (HttpContext ctx) =>
{
    var path = "/" + (ctx.Request.RouteValues["path"] as string ?? "");
    var ssr = hub.RenderInitialHtml("ConfigLiveView",
        view => ((ConfigLiveView)view).SetInitialPath(path));
    return Results.Content(
        Shell.Html.Replace("<!--SSR-->", ssr)
                  .Replace("/*CLIENT_JS*/", ClientRuntime.JavaScript)
                  .Replace("/*FP*/", DeltaLiveView.Fingerprint(ssr)),
        "text/html");
});

app.Run();

static class Shell
{
    public const string Html = """
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Coaching Hub — LiveView PoC</title>
<style>
    :root {
        --bg: #f6f7f9; --surface: #ffffff; --text: #1c2430; --muted: #66707f;
        --border: #e3e7ee; --accent: #2f6fed; --accent-soft: #e8effd;
        --pin: #b45309; --radius: 10px;
    }
    * { box-sizing: border-box; margin: 0; padding: 0; }
    body {
        font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', system-ui, sans-serif;
        background: var(--bg); color: var(--text); padding-bottom: 220px;
    }
    .app { max-width: 900px; margin: 0 auto; padding: 0 20px; }
    header {
        display: flex; align-items: center; gap: 14px; padding: 14px 0;
        border-bottom: 1px solid var(--border); margin-bottom: 20px;
    }
    .brand { font-weight: 700; font-size: 1.05rem; cursor: pointer; color: var(--accent); }
    .online { color: var(--muted); font-size: 0.78rem; }
    .auth { margin-left: auto; display: flex; align-items: center; gap: 8px; }
    .who { color: var(--muted); font-size: 0.8rem; }
    select, input, textarea {
        background: var(--surface); border: 1px solid var(--border); border-radius: 8px;
        padding: 7px 10px; font-size: 0.88rem; color: var(--text); outline: none; font-family: inherit;
    }
    input:focus, textarea:focus, select:focus { border-color: var(--accent); }
    h1 { font-size: 1.4rem; margin-bottom: 14px; }
    h2 { font-size: 1.2rem; margin: 6px 0; }
    h3 { font-size: 1rem; margin-bottom: 10px; }
    .stack { display: flex; flex-direction: column; }
    .text p { color: var(--muted); }
    .btn {
        background: var(--surface); color: var(--text); border: 1px solid var(--border);
        border-radius: 8px; padding: 8px 16px; font-size: 0.88rem; font-weight: 600;
        cursor: pointer; align-self: flex-start;
    }
    .btn.primary { background: var(--accent); border-color: var(--accent); color: #fff; }
    .btn:hover { opacity: 0.9; }
    .lv-busy { opacity: 0.5; pointer-events: none; }
    .lv-hidden { display: none; }

    .toolbar { display: flex; gap: 8px; margin-bottom: 12px; flex-wrap: wrap; }
    .toolbar input[name=q] { flex: 1; min-width: 220px; }
    table { width: 100%; border-collapse: collapse; background: var(--surface);
            border: 1px solid var(--border); border-radius: var(--radius); overflow: hidden; }
    th { text-align: left; font-size: 0.72rem; text-transform: uppercase; letter-spacing: 0.05em;
         color: var(--muted); padding: 10px 12px; border-bottom: 1px solid var(--border); }
    th.sortable { cursor: pointer; }
    th.sortable:hover { color: var(--accent); }
    td { padding: 10px 12px; border-bottom: 1px solid var(--border); font-size: 0.9rem; }
    tr.row { cursor: pointer; }
    tr.row:hover td { background: var(--accent-soft); }
    td.empty { color: var(--muted); text-align: center; padding: 28px; }
    .ref { font-family: ui-monospace, monospace; font-size: 0.78rem; color: var(--muted); }
    .tagchip { background: var(--accent-soft); color: var(--accent); border-radius: 12px;
               padding: 2px 10px; font-size: 0.78rem; }

    nav { display: flex; gap: 4px; flex-wrap: wrap; }
    .navlink { padding: 6px 10px; border-radius: 8px; font-size: 0.84rem; cursor: pointer; color: var(--muted); }
    .navlink:hover { background: var(--accent-soft); color: var(--accent); }
    .navlink.active { background: var(--accent-soft); color: var(--accent); font-weight: 600; }
    .cfg-section { margin: 6px 0; }
    .cfg-card { background: var(--surface); border: 1px solid var(--border);
                border-radius: var(--radius); padding: 14px 16px; }
    .cfg-card.clickable { cursor: pointer; transition: border-color 0.15s; }
    .cfg-card.clickable:hover { border-color: var(--accent); }
    .cfg-card p { color: var(--muted); font-size: 0.85rem; margin: 6px 0; }
    .cfg-columns { display: grid; gap: 16px; align-items: start; }
    .cardgrid { display: grid; grid-template-columns: repeat(auto-fill, minmax(240px, 1fr)); gap: 12px; }
    .cg-head { display: flex; align-items: center; gap: 8px; }
    .cg-icon { font-size: 1.2rem; }
    .compactlist { list-style: none; display: flex; flex-direction: column; gap: 4px; }
    .compactlist li { display: flex; justify-content: space-between; gap: 10px; padding: 7px 10px;
                      border-radius: 8px; cursor: pointer; }
    .compactlist li:hover { background: var(--accent-soft); }
    .compactlist small { color: var(--muted); flex-shrink: 0; }
    .cl-title { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .compact-empty, .todo { color: var(--muted); font-size: 0.82rem; font-style: italic; }
    .childlist { list-style: none; display: flex; flex-direction: column; gap: 8px; }
    .childlist .row { cursor: pointer; }
    .childlist .row:hover { border-color: var(--accent); }
    img.rounded { border-radius: var(--radius); }
    .fv-tags { display: inline-flex; gap: 6px; flex-wrap: wrap; }
    @media (max-width: 760px) { .cfg-columns { grid-template-columns: 1fr !important; } }

    .detail { background: var(--surface); border: 1px solid var(--border);
              border-radius: var(--radius); padding: 18px 20px; }
    .meta { display: flex; gap: 10px; align-items: center; margin-bottom: 6px; }
    .badge { font-size: 0.72rem; color: var(--pin); }
    .badge.closed { color: var(--muted); border: 1px solid var(--border); border-radius: 10px; padding: 1px 8px; }
    .viewing { margin-left: auto; font-size: 0.75rem; color: var(--accent); }
    .detail small { color: var(--muted); }
    .body { margin-top: 12px; display: flex; flex-direction: column; gap: 10px; }

    .comments { margin-top: 18px; }
    .commentlist { list-style: none; display: flex; flex-direction: column; gap: 8px; }
    .comment { background: var(--surface); border: 1px solid var(--border);
               border-radius: var(--radius); padding: 10px 14px; animation: c-in 0.3s ease-out; }
    @keyframes c-in { from { opacity: 0; transform: translateY(6px); } to { opacity: 1; } }
    .chead { display: flex; justify-content: space-between; margin-bottom: 4px; }
    .chead small { color: var(--muted); }
    .replyform { display: flex; flex-direction: column; gap: 8px; margin-top: 12px; }
    .gate { background: var(--accent-soft); border-radius: var(--radius); padding: 14px 16px;
            margin-top: 12px; color: var(--text); }
    .gate small { color: var(--muted); }

    .entityform { display: flex; flex-direction: column; gap: 12px; background: var(--surface);
                  border: 1px solid var(--border); border-radius: var(--radius); padding: 18px; max-width: 560px; }
    .formrow { display: flex; flex-direction: column; gap: 5px; }
    .formrow label { font-size: 0.8rem; font-weight: 600; }
    .errors { background: #fdeaea; color: #b3261e; border-radius: 8px; padding: 10px 14px 10px 28px; font-size: 0.85rem; }
    .membergate { display: flex; flex-direction: column; gap: 12px; }
    .todo { color: var(--muted); font-style: italic; }
</style>
</head>
<body>
    <div id="app"><!--SSR--></div>
    <script>/*CLIENT_JS*/</script>
    <script>
        LiveView.connect((location.protocol === 'https:' ? 'wss:' : 'ws:') + '//' + location.host
            + '/ws?path=' + encodeURIComponent(location.pathname), '#app', '/*FP*/');
    </script>
</body>
</html>
""";
}
