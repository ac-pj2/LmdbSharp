// ConfigLiveView: renders p2's views/*.json component trees server-side.
//
// This is the PoC's whole argument in one class: the SAME config files the
// platform's React SPA interprets client-side are interpreted here in C# —
// component tree walked with the H builder, reactive expressions (visibleWhen)
// evaluated by the platform's OWN server-side engine (Core.Expressions.Reactive,
// referenced unmodified), actions dispatched as LiveView events, and every
// change delivered to all sessions as granular DOM patches over one WebSocket.
// No client framework, no client expression engine, no client query cache.
using System.Text.Json;
using Core.Expressions.Reactive;
using Lmdb.LiveView;

namespace ConfigViews;

public class ForumState
{
    public string? UserName { get; set; }                       // null = guest
    public string Path { get; set; } = "/forum-threads";
    public Dictionary<string, EntityRecord> Records { get; } = new();
    public string Search { get; set; } = "";
    public string CategoryFilter { get; set; } = "";
    public string PinnedFilter { get; set; } = "";
    public string SortField { get; set; } = "";
    public bool SortAsc { get; set; }
    public Dictionary<string, string> Draft { get; } = new();   // create-form retention on validation error
    public List<string> FormErrors { get; } = new();
}

public class ConfigLiveView : DeltaLiveView<ForumState>
{
    private readonly IEntityStore _store;
    private readonly RecordCache _cache;
    private readonly SystemConfigSet _config;
    private readonly IReactiveExpressionEvaluator _expr;
    private string? _presenceThread; // thread-detail presence topic we're currently on

    public ConfigLiveView(IEntityStore store, RecordCache cache, SystemConfigSet config,
        IReactiveExpressionEvaluator expr)
    {
        _store = store;
        _cache = cache;
        _config = config;
        _expr = expr;
    }

    /// <summary>Set by the host from the HTTP request path (SSR) or the WS
    /// ?path= query — runs via the hub's configure hook, before Mount.</summary>
    public void SetInitialPath(string path) => State.Path = NormalizePath(path);

    public override void Mount()
    {
        Subscribe("records");
        TrackPresence("forum", State.UserName ?? "guest");
        // Mount from the projection (RecordCache) — filled at startup, kept
        // fresh by local writes and the mutation bridge. No per-session DB hit.
        foreach (var r in _cache.Snapshot()) State.Records[r.Key] = r;
        TrackThreadPresence();
    }

    private static string NormalizePath(string path)
        => string.IsNullOrEmpty(path) || path == "/" ? "/forum-threads" : path.Split('?')[0];

    // ── Routing (the platform's resolution cascade, PoC-sized) ──

    private (ViewDefinition View, Dictionary<string, string> Params)? ResolveRoute()
    {
        foreach (var view in _config.Views)
        {
            var p = ConfigLoader.MatchRoute(view.Route, State.Path);
            if (p != null) return (view, p);
        }
        return null;
    }

    private void Navigate(string to, bool pushUrl = true)
    {
        State.Path = NormalizePath(to);
        State.FormErrors.Clear();
        State.Draft.Clear();
        TrackThreadPresence();
        PushUpdate();
        if (pushUrl) PushNavigate(State.Path);
    }

    /// <summary>Presence follows the route: viewers of a thread are tracked on
    /// "thread:{id}" so detail pages can show who's reading along.</summary>
    private void TrackThreadPresence()
    {
        var route = ResolveRoute();
        string? topic = route?.Params.TryGetValue("id", out var id) == true ? $"thread:{id}" : null;
        if (topic == _presenceThread) return;
        if (_presenceThread != null) { UntrackPresence(_presenceThread); Unsubscribe(_presenceThread); }
        if (topic != null) { Subscribe(topic); TrackPresence(topic, State.UserName ?? "guest"); }
        _presenceThread = topic;
    }

    // ── Events ──

    public override void HandleEvent(string name, JsonElement? data)
    {
        switch (name)
        {
            case "navigate":
                Navigate(Get(data, "to") ?? "/");
                break;

            case "__nav": // browser back/forward — URL already changed client-side
                State.Path = NormalizePath(Get(data, "path") ?? "/");
                State.FormErrors.Clear();
                TrackThreadPresence();
                PushUpdate();
                break;

            case "open":
                if (Get(data, "id") is string id)
                    Navigate($"/forum-threads/{id}");
                break;

            case "change" when Get(data, "name") == "loginas":
            {
                var who = Get(data, "value");
                State.UserName = string.IsNullOrEmpty(who) ? null : who;
                TrackPresence("forum", State.UserName ?? "guest"); // meta update
                if (_presenceThread != null) TrackPresence(_presenceThread, State.UserName ?? "guest");
                PushUpdate();
                break;
            }

            case "search":
                State.Search = Get(data, "value") ?? "";
                PushUpdate();
                break;

            case "filter":
            {
                var field = Get(data, "name") ?? "";
                var value = Get(data, "value") ?? "";
                if (field == "filter-category") State.CategoryFilter = value;
                if (field == "filter-pinned") State.PinnedFilter = value;
                PushUpdate();
                break;
            }

            case "sort":
            {
                var field = Get(data, "field") ?? "";
                if (State.SortField == field) State.SortAsc = !State.SortAsc;
                else { State.SortField = field; State.SortAsc = true; }
                PushUpdate();
                break;
            }

            case "create":
                HandleCreate(data);
                break;

            case "reply":
                HandleReply(data);
                break;
        }
    }

    private void HandleCreate(JsonElement? data)
    {
        if (State.UserName == null) return; // gate: members only
        var et = _config.ResolveEntityType("forum-thread")!;

        State.Draft.Clear();
        State.FormErrors.Clear();
        var fields = new Dictionary<string, string>();
        foreach (var f in et.Fields)
        {
            var v = Get(data, f.Name)?.Trim() ?? "";
            if (v != "") fields[f.Name] = v;
            State.Draft[f.Name] = v;
            if (f.Required && v == "")
                State.FormErrors.Add($"{f.Label} is required.");
        }

        if (State.FormErrors.Count > 0) { PushUpdate(); return; }

        EntityRecord rec;
        try
        {
            // p2 mode: this POSTs through the platform's API — validation,
            // triggers, audit and mutation broadcast all run for real.
            rec = _store.CreateEntity("forum-thread", State.UserName, fields);
        }
        catch (Exception e)
        {
            State.FormErrors.Add($"The platform rejected the create: {e.Message}");
            PushUpdate();
            return;
        }
        State.Records[rec.Key] = rec;
        _cache.Upsert(rec);
        Hub?.Broadcast("record", rec);
        Navigate($"/forum-threads/{rec.Key}"); // the view's afterCreate: "detail"
    }

    private void HandleReply(JsonElement? data)
    {
        if (State.UserName == null) return;
        var body = Get(data, "body")?.Trim() ?? "";
        var route = ResolveRoute();
        if (body == "" || route?.Params.TryGetValue("id", out var threadKey) != true) return;
        if (State.Records.TryGetValue(threadKey, out var thread) && thread.Flag("closed")) return;

        EntityRecord rec;
        try { rec = _store.CreateReply(threadKey, body, State.UserName); }
        catch { return; }
        State.Records[rec.Key] = rec;
        _cache.Upsert(rec);
        Hub?.Broadcast("record", rec);
        PushUpdate();
    }

    private static string? Get(JsonElement? data, string prop)
        => data?.TryGetProperty(prop, out var v) == true ? v.GetString() : null;

    // ── Deltas ──

    public override void ApplyDelta(LiveDelta delta)
    {
        if (delta is { Type: "record", Data: EntityRecord rec })
            State.Records[rec.Key] = rec; // upsert — idempotent
        // "presence" deltas carry no state; the render queries Presence(topic).
    }

    // ── Expression context (evaluated by p2's own engine) ──

    private ReactiveExpressionContext ExprContext() => new()
    {
        User = State.UserName == null ? null : new Dictionary<string, object?>
        {
            ["id"] = State.UserName.ToLowerInvariant().Replace(" ", "-"),
            ["name"] = State.UserName,
        },
        System = new Dictionary<string, object?> { ["slug"] = "coaching-hub" },
        Route = new Dictionary<string, object?> { ["path"] = State.Path },
    };

    internal bool EvalVisible(string? expression)
        => expression == null || _expr.ToBoolean(_expr.Evaluate(expression, ExprContext()));

    // ── Rendering: walk the configured component tree ──

    public override HtmlElement RenderTree()
    {
        var resolved = ResolveRoute();
        var content = resolved == null
            ? H.Div(H.H2("Not found"), H.P($"No view matches {State.Path}")).Cls("notfound")
            : RenderNode(resolved.Value.View.Layout, resolved.Value.Params);

        return H.Div(
            RenderShell(),
            H.Div(content).Cls("content"),
            DevPanel.Render(this)
        ).Cls("app");
    }

    private HtmlElement RenderShell()
    {
        var online = Presence("forum");
        var header = H.Header(
            H.Span("Coaching Hub").Cls("brand").On("navigate").Attr("data-to", "/forum-threads"),
            H.Span($"{online.Count} online").Cls("online"),
            H.Div(
                H.Span(State.UserName == null ? "Browsing as guest" : $"Signed in as {State.UserName}").Cls("who"),
                RenderLoginSelect()
            ).Cls("auth")
        );
        return header;
    }

    private HtmlElement RenderLoginSelect()
    {
        var sel = H.Select().Attr("name", "loginas").On("change");
        foreach (var (value, label) in new[]
        { ("", "Guest"), ("Alice", "Alice"), ("Ben", "Ben"), ("Coach Dana", "Coach Dana") })
        {
            var opt = H.Option(label).Attr("value", value);
            if ((State.UserName ?? "") == value) opt.Attr("selected", "");
            sel.Add(opt);
        }
        return sel;
    }

    internal HtmlElement RenderNode(ViewNode node, Dictionary<string, string> routeParams)
    {
        // The config's reactive slot, evaluated by the platform's Jint engine.
        if (!EvalVisible(node.VisibleWhen))
            return H.Span().Cls("lv-hidden");

        return node.Component switch
        {
            "Page" => Components.Page(this, node, routeParams),
            "Stack" => Components.Stack(this, node, routeParams),
            "Text" => Components.Text(node),
            "Button" => Components.Button(node),
            "MemberGate" => Components.MemberGate(this, node, routeParams),
            "EntityList" => Components.EntityList(this, node),
            "EntityDetail" => Components.EntityDetail(this, node, routeParams),
            "EntityComments" => Components.EntityComments(this, node, routeParams),
            "EntityForm" => Components.EntityForm(this, node),
            _ => H.Div(H.Small($"[{node.Component}] has no server renderer yet")).Cls("todo"),
        };
    }

    // Accessors for the component renderers.
    internal ForumState S => State;
    internal SystemConfigSet Config => _config;
    internal new IReadOnlyList<PresenceEntry> Presence(string topic) => base.Presence(topic);
    internal HtmlElement MemoRow(object key, object version, Func<HtmlElement> build) => Memo(key, version, build);
}
