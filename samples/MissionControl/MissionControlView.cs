// MissionControlView: one session of the fleet dashboard.
//
// Everything renders from in-memory state. The simulator broadcasts "tick"
// deltas (changed nodes + new incidents); sessions apply them and re-render.
// Node cards and incident rows are memoized, so a tick that touched 15 nodes
// re-builds 15 cards and diffs nothing else — watch the dev panel's memo hit
// rate and diff timings to see it happen.
using System.Text.Json;
using Lmdb.LiveView;
using Lmdb.Objects;

namespace MissionControl;

public class FleetState
{
    public Dictionary<long, FleetNode> Nodes { get; } = new();
    public List<Incident> Incidents { get; set; } = new();
    public string Search { get; set; } = "";
    public string StatusFilter { get; set; } = "";
}

public class MissionControlView : DeltaLiveView<FleetState>
{
    private readonly FleetSimulator _sim;
    private readonly Collection<Incident> _incidents;

    public MissionControlView(FleetSimulator sim, Collection<Incident> incidents)
    {
        _sim = sim;
        _incidents = incidents;
    }

    public override void Mount()
    {
        // Tick deltas arrive via the "fleet" topic — only subscribers get them.
        Subscribe("fleet");

        var (nodes, incidents) = _sim.LoadAll();
        foreach (var n in nodes) State.Nodes[n.Id] = n;
        State.Incidents = incidents;
    }

    // ── Rendering ──

    public override HtmlElement RenderTree()
    {
        var visible = VisibleNodes();
        int critical = 0, warn = 0, cpuSum = 0;
        foreach (var n in State.Nodes.Values)
        {
            cpuSum += n.Cpu;
            if (n.Status == "critical") critical++;
            else if (n.Status == "warn") warn++;
        }
        double avgCpu = State.Nodes.Count == 0 ? 0 : (double)cpuSum / State.Nodes.Count;
        int openIncidents = State.Incidents.Count(i => i.State != "resolved");
        var uptime = DateTime.UtcNow - _sim.StartedAt;

        return H.Div(
            RenderHeader(avgCpu, critical, warn, openIncidents),
            RenderControls(visible.Count),
            H.Div(
                RenderGrid(visible),
                RenderIncidents()
            ).Cls("main"),
            DevPanel.Render(this,
                ("sim ticks", _sim.Ticks.ToString()),
                ("db writes", _sim.DbWrites.ToString()),
                ("uptime", $"{(int)uptime.TotalMinutes}m {uptime.Seconds}s"))
        ).Cls("shell");
    }

    private List<FleetNode> VisibleNodes()
    {
        var q = State.Search.Trim();
        var list = new List<FleetNode>(State.Nodes.Count);
        foreach (var n in State.Nodes.Values.OrderBy(n => n.Id))
        {
            if (State.StatusFilter != "" && n.Status != State.StatusFilter) continue;
            if (q != "" && !n.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                        && !n.Region.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(n);
        }
        return list;
    }

    private HtmlElement RenderHeader(double avgCpu, int critical, int warn, int openIncidents)
        => H.Header(
            H.H1("Mission Control"),
            H.Div(
                Kpi("nodes", State.Nodes.Count.ToString(), ""),
                Kpi("avg cpu", $"{avgCpu:f1}%", avgCpu > 75 ? "bad" : avgCpu > 60 ? "warn" : "good"),
                Kpi("critical", critical.ToString(), critical > 0 ? "bad" : "good"),
                Kpi("warning", warn.ToString(), warn > 0 ? "warn" : "good"),
                Kpi("open incidents", openIncidents.ToString(), openIncidents > 0 ? "bad" : "good"),
                Kpi("viewers", (Hub?.SessionCount ?? 1).ToString(), "")
            ).Cls("kpis"),
            // Cluster CPU trend: the canvas belongs to the CLIENT (data-lv-ignore).
            // The server only patches data-avg on the container; a MutationObserver
            // in the page feeds the sparkline. Server data, client rendering.
            H.Div(H.Canvas().Attr("width", "220").Attr("height", "48"))
                .Id("trend").Ignore().Attr("data-avg", avgCpu.ToString("f1")),
            H.Div(
                H.Button("💥 chaos").On("chaos").Cls("ghost"),
                H.Button(_sim.Paused ? "▶ resume feed" : "⏸ pause feed").On("pause").Cls("ghost")
            ).Cls("actions")
        );

    private static HtmlElement Kpi(string label, string value, string tone)
        => H.Div(H.B(value), H.Small(label)).Cls("kpi " + tone);

    private HtmlElement RenderControls(int visibleCount)
    {
        var bar = H.Div(
            H.Input().Attr("name", "q")
                .Attr("placeholder", "search nodes… (debounced, per-session)")
                .Attr("value", State.Search)
                .On("search").Debounce(250)
        ).Cls("controls");

        foreach (var status in new[] { "", "ok", "warn", "critical" })
            bar.Add(H.Button(status == "" ? "all" : status)
                .On("filter").Attr("data-status", status)
                .Cls("chip" + (State.StatusFilter == status ? " active" : "")));

        return bar.Add(H.Span($"{visibleCount} shown").Cls("count"));
    }

    private HtmlElement RenderGrid(List<FleetNode> visible)
        => H.Div().Cls("grid").AddRange(
            visible.Select(n => (HtmlNode)Memo(n.Id, (n.Cpu, n.Mem, n.Reqs, n.Status), () => BuildCard(n))));

    private static HtmlElement BuildCard(FleetNode n)
        => H.Div(
            H.Div(
                H.Span().Cls("dot " + n.Status),
                H.B(n.Name),
                H.Span(n.Region).Cls("region")
            ).Cls("card-top"),
            Meter("cpu", n.Cpu),
            Meter("mem", n.Mem),
            H.Div($"{n.Reqs} req/s").Cls("reqs")
        ).Cls("card " + n.Status).Key(n.Id);

    private static HtmlElement Meter(string label, int pct)
        => H.Div(
            H.Small(label),
            H.Div(H.Div().Cls("fill " + (pct > 92 ? "bad" : pct > 75 ? "warn" : "good"))
                        .Attr("style", $"width:{pct}%")).Cls("bar"),
            H.Small(pct + "%").Cls("pct")
        ).Cls("meter");

    private HtmlElement RenderIncidents()
    {
        var list = H.Ul().Cls("incidents").AddRange(
            State.Incidents.Select(i => (HtmlNode)Memo($"i{i.Id}", (i.State, i.AckedBy), () => BuildIncident(i))));
        if (State.Incidents.Count == 0)
            list.Add(H.Li("No incidents. The feed will raise some — nodes drift toward trouble.")
                .Cls("incident empty"));

        return H.Aside(
            H.Div(
                H.H2("Incidents"),
                H.Button("clear resolved").On("clearresolved").Cls("ghost small")
            ).Cls("aside-head"),
            list
        );
    }

    private static HtmlElement BuildIncident(Incident i)
        => H.Li(
            H.Div(
                H.B(i.NodeName),
                H.Span(i.Message),
                H.Small(i.CreatedAt.ToLocalTime().ToString("HH:mm:ss")
                    + (i.State == "acked" ? $" · acked by {i.AckedBy}"
                     : i.State == "resolved" ? " · resolved" : ""))
            ).Cls("inc-body"),
            H.Div().Cls("inc-actions")
                .AddIf(i.State == "open", () => H.Button("ack").On("ack", i.Id).Cls("small"))
                .AddIf(i.State != "resolved", () => H.Button("resolve").On("resolve", i.Id).Cls("small ghost"))
        ).Cls("incident " + i.State).Key("i" + i.Id);

    // ── Events ──

    public override void HandleEvent(string name, JsonElement? data)
    {
        switch (name)
        {
            case "search":
                State.Search = GetStr(data, "value") ?? "";
                PushUpdate(); // per-session — no broadcast
                break;

            case "filter":
                State.StatusFilter = GetStr(data, "status") ?? "";
                PushUpdate(); // per-session
                break;

            case "pause":
                _sim.Paused = !_sim.Paused;
                Hub?.Broadcast("sim"); // everyone re-renders the pause button
                break;

            case "chaos":
                _sim.TriggerChaos(); // broadcasts a tick delta to every session
                break;

            case "ack":
            case "resolve":
                if (long.TryParse(GetStr(data, "id"), out var id))
                    UpdateIncident(id, name == "ack" ? "acked" : "resolved");
                break;

            case "clearresolved":
            {
                using (var txn = _incidents.Database.BeginWrite())
                {
                    foreach (var inc in State.Incidents.Where(i => i.State == "resolved"))
                        _incidents.Delete(txn, inc.Id);
                    txn.Commit();
                }
                State.Incidents.RemoveAll(i => i.State == "resolved");
                PushUpdate();
                BroadcastDelta("clearresolved");
                break;
            }
        }
    }

    private void UpdateIncident(long id, string newState)
    {
        Incident? updated = null;
        using (var txn = _incidents.Database.BeginWrite())
        {
            var inc = _incidents.Get(txn, id);
            if (inc == null || inc.State == "resolved") return;
            inc.State = newState;
            if (newState == "acked") inc.AckedBy = SessionId.Length >= 6 ? SessionId[..6] : SessionId;
            _incidents.Update(txn, inc);
            txn.Commit();
            updated = inc;
        }

        var local = State.Incidents.FindIndex(i => i.Id == id);
        if (local >= 0) State.Incidents[local] = updated!;
        PushUpdate();
        BroadcastDelta("incident", updated);
    }

    private static string? GetStr(JsonElement? data, string prop)
        => data?.TryGetProperty(prop, out var p) == true ? p.GetString() : null;

    // ── Deltas from the simulator and other sessions ──
    // Payloads arrive by reference (no serialization) and are shared across
    // sessions: replace state entries with them, never mutate them. Idempotent:
    // a delta can race the mount's DB read and describe changes already loaded.

    public override void ApplyDelta(LiveDelta delta)
    {
        switch (delta.Type)
        {
            case "tick" when delta.Data is TickDelta tick:
            {
                foreach (var n in tick.Nodes) State.Nodes[n.Id] = n; // upsert — idempotent

                if (tick.Incidents.Count > 0)
                {
                    var fresh = tick.Incidents.Where(i => State.Incidents.All(e => e.Id != i.Id))
                                              .OrderByDescending(i => i.Id).ToList();
                    State.Incidents.InsertRange(0, fresh);
                    if (State.Incidents.Count > 50)
                        State.Incidents.RemoveRange(50, State.Incidents.Count - 50);
                }
                break;
            }

            case "incident" when delta.Data is Incident inc:
            {
                var idx = State.Incidents.FindIndex(i => i.Id == inc.Id);
                if (idx >= 0) State.Incidents[idx] = inc;
                break;
            }

            case "clearresolved":
                State.Incidents.RemoveAll(i => i.State == "resolved");
                break;

            case "sim":
                break; // pause flag lives on the simulator; re-render reads it

            case "reload":
            {
                var (nodes, incidents) = _sim.LoadAll();
                State.Nodes.Clear();
                foreach (var n in nodes) State.Nodes[n.Id] = n;
                State.Incidents = incidents;
                break;
            }
        }
    }
}
