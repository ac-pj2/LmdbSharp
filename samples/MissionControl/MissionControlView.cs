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

    public MissionControlView(FleetSimulator sim, Collection<Incident> incidents, LiveViewHub hub)
    {
        _sim = sim;
        _incidents = incidents;
        Hub = hub;
    }

    public override void Mount()
    {
        var (nodes, incidents) = _sim.LoadAll();
        foreach (var n in nodes) State.Nodes[n.Id] = n;
        State.Incidents = incidents;
    }

    // ── Rendering ──

    private static HtmlElement El(string tag, Dictionary<string, string>? attrs = null, params HtmlNode[] children)
    {
        var el = new HtmlElement { Tag = tag, Attributes = attrs ?? new() };
        foreach (var c in children) el.Children.Add(c);
        return el;
    }

    private static HtmlText Text(string s) => new() { Text = s };

    public override HtmlElement RenderTree()
    {
        var root = El("div", new() { ["class"] = "shell" });
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

        root.Children.Add(RenderHeader(avgCpu, critical, warn, openIncidents));
        root.Children.Add(RenderControls(visible.Count));

        var main = El("div", new() { ["class"] = "main" });
        main.Children.Add(RenderGrid(visible));
        main.Children.Add(RenderIncidents());
        root.Children.Add(main);

        root.Children.Add(RenderDevPanel());
        return root;
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
    {
        var header = El("header");
        header.Children.Add(El("h1", null, Text("Mission Control")));

        var kpis = El("div", new() { ["class"] = "kpis" });
        kpis.Children.Add(Kpi("nodes", State.Nodes.Count.ToString(), ""));
        kpis.Children.Add(Kpi("avg cpu", $"{avgCpu:f1}%", avgCpu > 75 ? "bad" : avgCpu > 60 ? "warn" : "good"));
        kpis.Children.Add(Kpi("critical", critical.ToString(), critical > 0 ? "bad" : "good"));
        kpis.Children.Add(Kpi("warning", warn.ToString(), warn > 0 ? "warn" : "good"));
        kpis.Children.Add(Kpi("open incidents", openIncidents.ToString(), openIncidents > 0 ? "bad" : "good"));
        kpis.Children.Add(Kpi("viewers", (Hub?.SessionCount ?? 1).ToString(), ""));
        header.Children.Add(kpis);

        // Cluster CPU trend: the canvas belongs to the CLIENT (data-lv-ignore).
        // The server only patches data-avg on the container; a MutationObserver
        // in the page feeds the sparkline. Server data, client rendering.
        header.Children.Add(El("div", new()
        {
            ["id"] = "trend",
            ["data-lv-ignore"] = "",
            ["data-avg"] = avgCpu.ToString("f1"),
        }, El("canvas", new() { ["width"] = "220", ["height"] = "48" })));

        var actions = El("div", new() { ["class"] = "actions" });
        var chaosBtn = El("button", new() { ["data-event"] = "chaos", ["class"] = "ghost" });
        chaosBtn.Children.Add(Text("💥 chaos"));
        actions.Children.Add(chaosBtn);
        var pauseBtn = El("button", new() { ["data-event"] = "pause", ["class"] = "ghost" });
        pauseBtn.Children.Add(Text(_sim.Paused ? "▶ resume feed" : "⏸ pause feed"));
        actions.Children.Add(pauseBtn);
        var devBtn = El("button", new()
        { ["data-client"] = "toggle #dev with slide", ["class"] = "ghost", ["type"] = "button" });
        devBtn.Children.Add(Text("⚙ dev"));
        actions.Children.Add(devBtn);
        header.Children.Add(actions);
        return header;
    }

    private static HtmlElement Kpi(string label, string value, string tone)
    {
        var k = El("div", new() { ["class"] = "kpi " + tone });
        k.Children.Add(El("b", null, Text(value)));
        k.Children.Add(El("small", null, Text(label)));
        return k;
    }

    private HtmlElement RenderControls(int visibleCount)
    {
        var bar = El("div", new() { ["class"] = "controls" });
        bar.Children.Add(El("input", new()
        {
            ["name"] = "q",
            ["placeholder"] = "search nodes… (debounced, per-session)",
            ["data-event"] = "search",
            ["data-debounce"] = "250",
            ["value"] = State.Search,
        }));

        foreach (var status in new[] { "", "ok", "warn", "critical" })
        {
            var chip = El("button", new()
            {
                ["data-event"] = "filter",
                ["data-status"] = status,
                ["class"] = "chip" + (State.StatusFilter == status ? " active" : ""),
            });
            chip.Children.Add(Text(status == "" ? "all" : status));
            bar.Children.Add(chip);
        }

        var count = El("span", new() { ["class"] = "count" });
        count.Children.Add(Text($"{visibleCount} shown"));
        bar.Children.Add(count);
        return bar;
    }

    private HtmlElement RenderGrid(List<FleetNode> visible)
    {
        var grid = El("div", new() { ["class"] = "grid" });
        foreach (var n in visible)
        {
            grid.Children.Add(Memo(n.Id, (n.Cpu, n.Mem, n.Reqs, n.Status), () => BuildCard(n)));
        }
        return grid;
    }

    private static HtmlElement BuildCard(FleetNode n)
    {
        var card = El("div", new() { ["class"] = "card " + n.Status, ["data-key"] = n.Id.ToString() });

        var top = El("div", new() { ["class"] = "card-top" });
        top.Children.Add(El("span", new() { ["class"] = "dot " + n.Status }));
        top.Children.Add(El("b", null, Text(n.Name)));
        top.Children.Add(El("span", new() { ["class"] = "region" }, Text(n.Region)));
        card.Children.Add(top);

        card.Children.Add(Meter("cpu", n.Cpu));
        card.Children.Add(Meter("mem", n.Mem));

        var reqs = El("div", new() { ["class"] = "reqs" });
        reqs.Children.Add(Text($"{n.Reqs} req/s"));
        card.Children.Add(reqs);
        return card;
    }

    private static HtmlElement Meter(string label, int pct)
    {
        var row = El("div", new() { ["class"] = "meter" });
        row.Children.Add(El("small", null, Text(label)));
        row.Children.Add(El("div", new() { ["class"] = "bar" },
            El("div", new()
            {
                ["class"] = "fill " + (pct > 92 ? "bad" : pct > 75 ? "warn" : "good"),
                ["style"] = $"width:{pct}%",
            })));
        row.Children.Add(El("small", new() { ["class"] = "pct" }, Text(pct + "%")));
        return row;
    }

    private HtmlElement RenderIncidents()
    {
        var aside = El("aside");
        var head = El("div", new() { ["class"] = "aside-head" });
        head.Children.Add(El("h2", null, Text("Incidents")));
        var clear = El("button", new() { ["data-event"] = "clearresolved", ["class"] = "ghost small" });
        clear.Children.Add(Text("clear resolved"));
        head.Children.Add(clear);
        aside.Children.Add(head);

        var list = El("ul", new() { ["class"] = "incidents" });
        foreach (var i in State.Incidents)
        {
            list.Children.Add(Memo($"i{i.Id}", (i.State, i.AckedBy), () => BuildIncident(i)));
        }
        if (State.Incidents.Count == 0)
        {
            var empty = El("li", new() { ["class"] = "empty" });
            empty.Children.Add(Text("No incidents. The feed will raise some — nodes drift toward trouble."));
            list.Children.Add(empty);
        }
        aside.Children.Add(list);
        return aside;
    }

    private static HtmlElement BuildIncident(Incident i)
    {
        var li = El("li", new() { ["class"] = "incident " + i.State, ["data-key"] = "i" + i.Id });
        var body = El("div", new() { ["class"] = "inc-body" });
        body.Children.Add(El("b", null, Text(i.NodeName)));
        body.Children.Add(El("span", null, Text(i.Message)));
        var meta = El("small", null, Text(
            i.CreatedAt.ToLocalTime().ToString("HH:mm:ss")
            + (i.State == "acked" ? $" · acked by {i.AckedBy}" : i.State == "resolved" ? " · resolved" : "")));
        body.Children.Add(meta);
        li.Children.Add(body);

        var btns = El("div", new() { ["class"] = "inc-actions" });
        if (i.State == "open")
        {
            var ack = El("button", new() { ["data-event"] = "ack", ["data-id"] = i.Id.ToString(), ["class"] = "small" });
            ack.Children.Add(Text("ack"));
            btns.Children.Add(ack);
        }
        if (i.State != "resolved")
        {
            var res = El("button", new() { ["data-event"] = "resolve", ["data-id"] = i.Id.ToString(), ["class"] = "small ghost" });
            res.Children.Add(Text("resolve"));
            btns.Children.Add(res);
        }
        li.Children.Add(btns);
        return li;
    }

    private HtmlElement RenderDevPanel()
    {
        var dev = El("div", new() { ["id"] = "dev", ["hidden"] = "" });

        // Server half: rendered by this view, updated live via normal patches.
        var server = El("div", new() { ["class"] = "dev-col" });
        server.Children.Add(El("h3", null, Text("server · this session")));
        var s = Stats;
        var uptime = DateTime.UtcNow - _sim.StartedAt;
        server.Children.Add(DevRow("renders", s.Renders.ToString()));
        server.Children.Add(DevRow("last render", $"{s.LastRenderMicros:f0} µs"));
        server.Children.Add(DevRow("last diff", $"{s.LastDiffMicros:f0} µs"));
        server.Children.Add(DevRow("memo hit rate", $"{s.MemoHitRate:p1} ({s.MemoHits}/{s.MemoHits + s.MemoMisses})"));
        server.Children.Add(DevRow("patch msgs", s.PatchMessages.ToString()));
        server.Children.Add(DevRow("patch bytes", FormatBytes(s.PatchBytes)));
        server.Children.Add(DevRow("sessions", (Hub?.SessionCount ?? 1).ToString()));
        server.Children.Add(DevRow("sim ticks", _sim.Ticks.ToString()));
        server.Children.Add(DevRow("db writes", _sim.DbWrites.ToString()));
        server.Children.Add(DevRow("uptime", $"{(int)uptime.TotalMinutes}m {uptime.Seconds}s"));
        dev.Children.Add(server);

        // Client half: OWNED by the page script (data-lv-ignore) — the runtime's
        // LiveView.debug() hook fills it. Patches never touch it.
        var client = El("div", new() { ["class"] = "dev-col", ["id"] = "dev-client", ["data-lv-ignore"] = "" });
        client.Children.Add(El("h3", null, Text("client · this browser")));
        dev.Children.Add(client);

        var log = El("div", new() { ["class"] = "dev-col wide", ["id"] = "dev-log", ["data-lv-ignore"] = "" });
        log.Children.Add(El("h3", null, Text("wire · last patches")));
        dev.Children.Add(log);

        return dev;
    }

    private static HtmlElement DevRow(string label, string value)
    {
        var row = El("div", new() { ["class"] = "dev-row" });
        row.Children.Add(El("small", null, Text(label)));
        row.Children.Add(El("span", null, Text(value)));
        return row;
    }

    private static string FormatBytes(long b) =>
        b > 1024 * 1024 ? $"{b / (1024.0 * 1024):f1} MB" : b > 1024 ? $"{b / 1024.0:f1} KB" : b + " B";

    // ── Events ──

    public override void HandleEvent(string name, JsonElement? data)
    {
        switch (name)
        {
            case "search":
                State.Search = data?.TryGetProperty("value", out var v) == true ? v.GetString() ?? "" : "";
                PushUpdate(); // per-session — no broadcast
                break;

            case "filter":
                State.StatusFilter = data?.TryGetProperty("status", out var st) == true ? st.GetString() ?? "" : "";
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

    public override void ApplyDelta(LiveDelta delta)
    {
        switch (delta.Type)
        {
            case "tick" when delta.Data.HasValue:
            {
                var changed = delta.Data.Value.GetProperty("nodes").Deserialize<List<FleetNode>>() ?? new();
                foreach (var n in changed) State.Nodes[n.Id] = n;

                var incidents = delta.Data.Value.GetProperty("incidents").Deserialize<List<Incident>>() ?? new();
                if (incidents.Count > 0)
                {
                    // Deltas can race the mount's DB read (a broadcast queued while
                    // Mount was loading) — ApplyDelta must be idempotent, so skip
                    // incidents we already have.
                    var fresh = incidents.Where(i => State.Incidents.All(e => e.Id != i.Id))
                                         .OrderByDescending(i => i.Id).ToList();
                    State.Incidents.InsertRange(0, fresh);
                    if (State.Incidents.Count > 50)
                        State.Incidents.RemoveRange(50, State.Incidents.Count - 50);
                }
                break;
            }

            case "incident" when delta.Data.HasValue:
            {
                var inc = delta.Data.Value.Deserialize<Incident>();
                if (inc != null)
                {
                    var idx = State.Incidents.FindIndex(i => i.Id == inc.Id);
                    if (idx >= 0) State.Incidents[idx] = inc;
                }
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
