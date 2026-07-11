// Mission Control — a live fleet dashboard pushing the LiveView stack:
//   · 200 nodes streamed from a background simulator (LMDB writes + broadcast)
//   · memoized cards: a tick re-renders only the nodes it touched
//   · collaborative incident feed (ack/resolve sync across every browser)
//   · debounced per-session search + filters
//   · client-owned canvas sparkline inside data-lv-ignore, fed by attr patches
//   · client-side commands with transitions (dev drawer)
//   · full observability: server render/diff/memo stats + client wire stats
using Lmdb.AspNetCore;
using Lmdb.LiveView;
using Lmdb.Objects;
using MissionControl;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLmdbObjectDatabase(builder.Configuration["FleetDbPath"] ?? "./missioncontrol-data");
builder.Services.AddCollection<FleetNode>("nodes");
builder.Services.AddCollection<Incident>("incidents");

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

var sim = new FleetSimulator(
    app.Services.GetRequiredService<Collection<FleetNode>>(),
    app.Services.GetRequiredService<Collection<Incident>>());

LiveViewHub? hubRef = null;
hubRef = new LiveViewHub(_ => new MissionControlView(
    sim, app.Services.GetRequiredService<Collection<Incident>>(), hubRef!));
sim.Hub = hubRef;
sim.Start(TimeSpan.FromMilliseconds(500));

app.MapGet("/", () =>
{
    var ssr = hubRef!.RenderInitialHtml("MissionControlView");
    return Results.Content(
        Page.Html.Replace("<!--SSR-->", ssr)
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
        await hubRef!.HandleConnectionAsync(ws, "MissionControlView",
            ctx.Request.Query["fp"].FirstOrDefault());
    }
    else ctx.Response.StatusCode = 400;
});

app.Run();

static class Page
{
    public const string Html = """
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Mission Control</title>
<style>
    :root {
        --bg: #0a0c12; --surface: #12151f; --surface2: #191d2b; --text: #dde1ea;
        --muted: #7c8394; --border: #232838; --accent: #5b8def;
        --good: #22c55e; --warn: #f59e0b; --bad: #ef4444; --radius: 8px;
    }
    * { box-sizing: border-box; margin: 0; padding: 0; }
    body {
        font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', system-ui, sans-serif;
        background: var(--bg); color: var(--text); padding: 20px 24px 220px;
    }
    .shell { max-width: 1400px; margin: 0 auto; }
    header { display: flex; align-items: center; gap: 20px; flex-wrap: wrap; margin-bottom: 14px; }
    h1 { font-size: 1.3rem; letter-spacing: 0.04em; }
    h1::before { content: "●"; color: var(--good); margin-right: 8px; font-size: 0.8em; }
    .kpis { display: flex; gap: 8px; flex-wrap: wrap; }
    .kpi {
        background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius);
        padding: 6px 14px; text-align: center; min-width: 74px;
    }
    .kpi b { display: block; font-size: 1.05rem; font-variant-numeric: tabular-nums; }
    .kpi small { color: var(--muted); font-size: 0.68rem; text-transform: uppercase; letter-spacing: 0.06em; }
    .kpi.good b { color: var(--good); } .kpi.warn b { color: var(--warn); } .kpi.bad b { color: var(--bad); }
    #trend { background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius); padding: 4px 8px 0; }
    .actions { margin-left: auto; display: flex; gap: 8px; }
    button {
        background: var(--accent); color: #fff; border: none; border-radius: var(--radius);
        padding: 8px 14px; font-size: 0.85rem; font-weight: 600; cursor: pointer;
    }
    button:hover { opacity: 0.85; }
    button.ghost { background: var(--surface); border: 1px solid var(--border); color: var(--text); }
    button.small { padding: 4px 10px; font-size: 0.75rem; }
    .lv-busy { opacity: 0.45; pointer-events: none; }

    .controls { display: flex; gap: 8px; align-items: center; margin-bottom: 14px; flex-wrap: wrap; }
    .controls input {
        background: var(--surface); border: 1px solid var(--border); color: var(--text);
        padding: 8px 14px; border-radius: var(--radius); width: 320px; outline: none; font-size: 0.9rem;
    }
    .controls input:focus { border-color: var(--accent); }
    .chip { background: var(--surface); border: 1px solid var(--border); color: var(--muted); padding: 6px 12px; font-weight: 500; }
    .chip.active { border-color: var(--accent); color: var(--accent); }
    .count { color: var(--muted); font-size: 0.8rem; margin-left: auto; font-variant-numeric: tabular-nums; }

    .main { display: grid; grid-template-columns: 1fr 330px; gap: 16px; align-items: start; }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(200px, 1fr)); gap: 8px; }
    .card {
        background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius);
        padding: 9px 11px; transition: border-color 0.3s;
    }
    .card.warn { border-color: #4d3c14; }
    .card.critical { border-color: #5c2222; background: #191016; }
    .card-top { display: flex; align-items: center; gap: 7px; margin-bottom: 7px; font-size: 0.8rem; }
    .card-top b { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .dot { width: 8px; height: 8px; border-radius: 50%; flex-shrink: 0; background: var(--good); }
    .dot.warn { background: var(--warn); } .dot.critical { background: var(--bad); animation: pulse 1s infinite; }
    @keyframes pulse { 50% { opacity: 0.35; } }
    .region { margin-left: auto; color: var(--muted); font-size: 0.68rem; }
    .meter { display: flex; align-items: center; gap: 6px; margin: 3px 0; }
    .meter small { width: 26px; color: var(--muted); font-size: 0.65rem; }
    .meter .pct { width: 32px; text-align: right; font-variant-numeric: tabular-nums; }
    .bar { flex: 1; height: 5px; background: var(--surface2); border-radius: 3px; overflow: hidden; }
    .fill { height: 100%; border-radius: 3px; transition: width 0.35s; background: var(--good); }
    .fill.warn { background: var(--warn); } .fill.bad { background: var(--bad); }
    .reqs { color: var(--muted); font-size: 0.7rem; margin-top: 5px; font-variant-numeric: tabular-nums; }

    aside { background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius); padding: 12px; }
    .aside-head { display: flex; align-items: center; justify-content: space-between; margin-bottom: 10px; }
    h2 { font-size: 0.95rem; }
    .incidents { list-style: none; max-height: 70vh; overflow-y: auto; display: flex; flex-direction: column; gap: 6px; }
    .incident {
        display: flex; gap: 8px; align-items: center; padding: 8px 10px; border-radius: var(--radius);
        background: var(--surface2); border: 1px solid var(--border); border-left: 3px solid var(--bad);
        animation: inc-in 0.35s ease-out;
    }
    .incident.acked { border-left-color: var(--warn); }
    .incident.resolved { border-left-color: var(--good); opacity: 0.55; }
    .incident.empty { border-left-color: var(--border); color: var(--muted); font-size: 0.8rem; }
    @keyframes inc-in { from { opacity: 0; transform: translateX(14px); } to { opacity: 1; transform: none; } }
    .inc-body { flex: 1; min-width: 0; }
    .inc-body b { display: block; font-size: 0.8rem; }
    .inc-body span { display: block; font-size: 0.74rem; color: var(--muted); }
    .inc-body small { font-size: 0.68rem; color: var(--muted); }
    .inc-actions { display: flex; flex-direction: column; gap: 4px; }

    /* dev drawer — toggled client-side: data-client="toggle #dev with slide" */
    #dev {
        position: fixed; left: 0; right: 0; bottom: 0; z-index: 10;
        background: #0d1017f2; border-top: 1px solid var(--border); backdrop-filter: blur(6px);
        display: flex; gap: 28px; padding: 14px 28px; font-family: 'SF Mono', ui-monospace, monospace;
        font-size: 0.72rem; max-height: 200px;
    }
    .slide-in { animation: lv-slide-in 0.2s ease-out; }
    .slide-out { animation: lv-slide-out 0.18s ease-in forwards; }
    @keyframes lv-slide-in { from { transform: translateY(100%); } to { transform: none; } }
    @keyframes lv-slide-out { from { transform: none; } to { transform: translateY(100%); } }
    .dev-col { min-width: 220px; }
    .dev-col.wide { flex: 1; overflow: hidden; }
    #dev h3 { font-size: 0.7rem; color: var(--accent); text-transform: uppercase; letter-spacing: 0.08em; margin-bottom: 8px; }
    .dev-row { display: flex; justify-content: space-between; gap: 16px; padding: 1.5px 0; }
    .dev-row small { color: var(--muted); }
    .dev-row span { font-variant-numeric: tabular-nums; }
    #dev-log .op { color: var(--muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; padding: 1.5px 0; }
    #dev-log .op b { color: var(--text); font-weight: 500; }

    @media (max-width: 1000px) { .main { grid-template-columns: 1fr; } }
</style>
</head>
<body>
    <div id="app"><!--SSR--></div>
    <script>/*CLIENT_JS*/</script>
    <script>
    LiveView.connect((location.protocol === 'https:' ? 'wss:' : 'ws:') + '//' + location.host + '/ws', '#app', '/*FP*/');

    // ── Observability: client half of the dev drawer ─────────────────────
    // Fed by the runtime's debug hook. Lives in data-lv-ignore zones, so
    // server patches never touch it.
    const stats = { recv: 0, sent: 0, bytesIn: 0, bytesOut: 0, patches: 0,
                    lastApply: 0, reconnects: 0, state: 'connecting' };
    const oplog = [];

    LiveView.debug((e) => {
        if (e.t === 'open') { stats.state = 'connected'; }
        if (e.t === 'close') { stats.state = 'reconnecting…'; stats.reconnects++; }
        if (e.t === 'recv') {
            stats.recv++; stats.bytesIn += e.bytes;
            if (e.kind === 'patches') {
                stats.patches += e.count;
                const ops = {};
                e.patches.forEach(p => ops[p.t] = (ops[p.t] || 0) + 1);
                oplog.unshift(`<b>${e.bytes}B</b> ${Object.entries(ops).map(([k, v]) => `${k}×${v}`).join(' ')}`);
                if (oplog.length > 7) oplog.pop();
            } else {
                oplog.unshift(`<b>${e.bytes}B</b> ${e.kind}${e.kind === 'ok' ? ' (SSR adopted, HTML not re-sent)' : ''}`);
                if (oplog.length > 7) oplog.pop();
            }
        }
        if (e.t === 'applied') stats.lastApply = e.ms;
        if (e.t === 'send') { stats.sent++; stats.bytesOut += e.bytes; }
        renderDev();
    });

    const fmtB = (b) => b > 1048576 ? (b / 1048576).toFixed(1) + ' MB' : b > 1024 ? (b / 1024).toFixed(1) + ' KB' : b + ' B';
    function renderDev() {
        const c = document.getElementById('dev-client');
        if (c) c.innerHTML = '<h3>client · this browser</h3>' + [
            ['status', stats.state],
            ['msgs in / out', stats.recv + ' / ' + stats.sent],
            ['bytes in / out', fmtB(stats.bytesIn) + ' / ' + fmtB(stats.bytesOut)],
            ['patch ops applied', stats.patches],
            ['last apply', stats.lastApply.toFixed(1) + ' ms'],
            ['reconnects', stats.reconnects],
        ].map(([k, v]) => `<div class="dev-row"><small>${k}</small><span>${v}</span></div>`).join('');
        const l = document.getElementById('dev-log');
        if (l) l.innerHTML = '<h3>wire · last frames</h3>' + oplog.map(o => `<div class="op">${o}</div>`).join('');
    }

    // ── Cluster CPU sparkline: client-owned canvas in a data-lv-ignore zone.
    // The server only patches data-avg on #trend; we watch it and draw.
    const history = [];
    function drawTrend() {
        const trend = document.getElementById('trend');
        const canvas = trend && trend.querySelector('canvas');
        if (!canvas) return;
        const ctx = canvas.getContext && canvas.getContext('2d');
        if (!ctx) return; // headless test environments
        const W = canvas.width, H = canvas.height;
        ctx.clearRect(0, 0, W, H);
        if (history.length < 2) return;
        ctx.beginPath();
        history.forEach((v, i) => {
            const x = (i / 59) * (W - 4) + 2, y = H - 4 - (v / 100) * (H - 10);
            i ? ctx.lineTo(x, y) : ctx.moveTo(x, y);
        });
        ctx.strokeStyle = '#5b8def'; ctx.lineWidth = 1.5; ctx.stroke();
        ctx.lineTo((history.length - 1) / 59 * (W - 4) + 2, H); ctx.lineTo(2, H);
        ctx.closePath(); ctx.fillStyle = 'rgba(91,141,239,0.12)'; ctx.fill();
        ctx.fillStyle = '#7c8394'; ctx.font = '9px ui-monospace';
        ctx.fillText('cluster cpu ' + history[history.length - 1].toFixed(1) + '%', 6, 10);
    }
    function pushAvg() {
        const trend = document.getElementById('trend');
        if (!trend) return;
        const v = parseFloat(trend.getAttribute('data-avg'));
        if (isNaN(v)) return;
        history.push(v);
        if (history.length > 60) history.shift();
        drawTrend();
    }
    new MutationObserver(pushAvg).observe(document.getElementById('app'),
        { subtree: true, attributes: true, attributeFilter: ['data-avg'] });
    pushAvg();
    </script>
</body>
</html>
""";
}
