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
    public string? UserToken { get; set; }                      // p2 mode: the user's OWN JWT
    public bool IsAdmin { get; set; }
    public string? LoginError { get; set; }
    public string Path { get; set; } = "/home";
    public Dictionary<string, string> Query { get; } = new();
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
    private readonly IRecordProjection _cache;
    private readonly SystemConfigSet _config;
    private readonly IReactiveExpressionEvaluator _expr;
    private string? _presenceThread; // thread-detail presence topic we're currently on

    public ConfigLiveView(IEntityStore store, IRecordProjection cache, SystemConfigSet config,
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

    private string NormalizePath(string path)
    {
        State.Query.Clear();
        var parts = (path ?? "/").Split('?', 2);
        if (parts.Length == 2)
            foreach (var kv in parts[1].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = kv.Split('=', 2);
                State.Query[Uri.UnescapeDataString(p[0])] = p.Length == 2 ? Uri.UnescapeDataString(p[1]) : "";
            }
        return string.IsNullOrEmpty(parts[0]) || parts[0] == "/" ? "/home" : parts[0];
    }

    // ── Routing (the platform's resolution cascade, PoC-sized) ──

    private (ViewDefinition View, Dictionary<string, string> Params)? ResolveRoute()
    {
        foreach (var view in _config.Views)
        {
            var p = ConfigLoader.MatchRoute(view.Route, State.Path);
            if (p != null) return (view, p);
        }
        return DefaultViews.Resolve(_config, State.Path); // generated list/detail/create
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
                    Navigate($"/{Pluralize(Get(data, "type") ?? "forum-thread")}/{id}");
                break;

            case "change" when Get(data, "name") == "loginas":
            {
                var who = Get(data, "value");
                State.UserName = string.IsNullOrEmpty(who) ? null : who;
                State.IsAdmin = who == "Coach Dana";
                TrackPresence("forum", State.UserName ?? "guest"); // meta update
                if (_presenceThread != null) TrackPresence(_presenceThread, State.UserName ?? "guest");
                PushUpdate();
                break;
            }

            case "login": // p2 mode: authenticate against the real platform
            {
                State.LoginError = null;
                var auth = (_store as P2EntityStore)?.Login(
                    Get(data, "email") ?? "", Get(data, "password") ?? "");
                if (auth == null)
                {
                    State.LoginError = "Sign-in failed — check the email and password.";
                }
                else
                {
                    State.UserName = auth.Value.Name;
                    State.UserToken = auth.Value.Token;
                    State.IsAdmin = auth.Value.IsAdmin;
                    TrackPresence("forum", State.UserName);
                    if (_presenceThread != null) TrackPresence(_presenceThread, State.UserName);
                }
                PushUpdate();
                break;
            }

            case "logout":
                State.UserName = null;
                State.UserToken = null;
                State.IsAdmin = false;
                TrackPresence("forum", "guest");
                if (_presenceThread != null) TrackPresence(_presenceThread, "guest");
                PushUpdate();
                break;

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
        var slug = Get(data, "entitytype") ?? "forum-thread";
        var et = _config.ResolveEntityType(slug);
        if (et == null) return;

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
            rec = _store.CreateEntity(et.Slug, State.UserName, fields, State.UserToken);
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
        Navigate($"/{Pluralize(et.Slug)}/{rec.Key}"); // the view's afterCreate: "detail"
    }

    internal static string Pluralize(string slug)
        => slug.EndsWith("y") ? slug[..^1] + "ies" : slug + "s";

    private void HandleReply(JsonElement? data)
    {
        if (State.UserName == null) return;
        var body = Get(data, "body")?.Trim() ?? "";
        var route = ResolveRoute();
        if (body == "" || route?.Params.TryGetValue("id", out var threadKey) != true) return;
        if (State.Records.TryGetValue(threadKey, out var thread) && thread.Flag("closed")) return;

        EntityRecord rec;
        try { rec = _store.CreateReply(threadKey, body, State.UserName, State.UserToken); }
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
        else if (delta is { Type: "remove-record", Data: string goneKey })
            State.Records.Remove(goneKey);
        // "presence" deltas carry no state; the render queries Presence(topic).
    }

    // ── Expression context (evaluated by p2's own engine) ──

    /// <summary>Resolved dataBindings for the current render (data.* in expressions).</summary>
    private Dictionary<string, object?> _renderData = new();
    private Dictionary<string, string> _renderParams = new();

    private ReactiveExpressionContext ExprContext() => new()
    {
        User = State.UserName == null ? null : new Dictionary<string, object?>
        {
            ["id"] = State.UserName.ToLowerInvariant().Replace(" ", "-"),
            ["name"] = State.UserName,
            // p2 mode: from the real account (isSystemAdmin); lmdb demo: Coach Dana.
            ["role"] = State.IsAdmin ? "Administrator" : "Member",
        },
        System = new Dictionary<string, object?> { ["slug"] = "coaching-hub" },
        Route = new Dictionary<string, object?> { ["path"] = State.Path },
        Data = _renderData,
    };

    internal bool EvalVisible(string? expression)
        => expression == null || _expr.ToBoolean(_expr.Evaluate(expression, ExprContext()));

    internal string EvalString(string expression)
        => _expr.Evaluate(expression, ExprContext())?.ToString() ?? "";

    /// <summary>{{path.to.value}} interpolation over params/user/data/query —
    /// the template syntax p2's views use inside prop strings.</summary>
    internal string Interpolate(string template)
    {
        if (!template.Contains("{{")) return template;
        return System.Text.RegularExpressions.Regex.Replace(template, @"\{\{\s*([^}]+?)\s*\}\}", m =>
        {
            var path = m.Groups[1].Value.Trim();
            if (path.StartsWith("params."))
                return _renderParams.GetValueOrDefault(path[7..], "");
            if (path.StartsWith("query."))
                return State.Query.GetValueOrDefault(path[6..], "");
            // Everything else (user.*, data.*) goes through the expression engine.
            return EvalString(path);
        });
    }

    /// <summary>Resolve the view's dataBindings from the projection — where the
    /// SPA fetches /api/entities/{id} per binding, the server renderer reads
    /// its own read model. Exposed to expressions as data.&lt;name&gt;.</summary>
    private void ResolveBindings(ViewDefinition view, Dictionary<string, string> routeParams)
    {
        _renderParams = routeParams;
        _renderData = new Dictionary<string, object?>();
        if (view.DataBindings == null) return;
        foreach (var (name, binding) in view.DataBindings)
        {
            var endpoint = Interpolate(binding.Endpoint);
            var key = endpoint.Split('/').LastOrDefault() ?? "";
            if (State.Records.TryGetValue(key, out var rec))
                _renderData[name] = new Dictionary<string, object?>
                {
                    ["id"] = rec.Key,
                    ["referenceNumber"] = rec.Ref,
                    ["createdAt"] = rec.CreatedAt.ToString("O"),
                    ["formData"] = rec.Fields.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                };
        }
    }

    // ── Rendering: walk the configured component tree ──

    public override HtmlElement RenderTree()
    {
        var resolved = ResolveRoute();
        HtmlElement content;
        if (resolved == null)
        {
            content = H.Div(
                H.H2("Not rendered here"),
                H.P($"No configured view matches {State.Path}."),
                H.Small("Routes like /sites/... belong to platform features (site publishing) outside this PoC renderer — they keep working in the p2 SPA.")
            ).Cls("gate");
        }
        else
        {
            ResolveBindings(resolved.Value.View, resolved.Value.Params);
            content = RenderNode(resolved.Value.View.Layout, resolved.Value.Params);
        }

        return H.Div(
            RenderShell(),
            H.Div(content).Cls("content"),
            DevPanel.Render(this, ("projection", _cache.Describe()))
        ).Cls("app");
    }

    private HtmlElement RenderShell()
    {
        var online = Presence("forum");

        // The nav bar comes from navigation.json — including its visibleWhen
        // expressions (e.g. admin-only items), evaluated by the platform engine.
        var nav = H.El("nav");
        foreach (var item in _config.Navigation)
        {
            if (!EvalVisible(item.VisibleWhen)) continue;
            var link = H.Span($"{item.Icon} {item.Label}".Trim())
                .Cls("navlink" + (State.Path == item.Route.Split('?')[0] ? " active" : ""))
                .On("navigate").Attr("data-to", item.Route);
            nav.Add(link);
        }

        return H.Header(
            H.Span("Coaching Hub").Cls("brand").On("navigate").Attr("data-to", "/home"),
            nav,
            H.Span($"{online.Count} online").Cls("online"),
            H.Div(
                H.Span(State.UserName == null ? "Browsing as guest" : $"Signed in as {State.UserName}").Cls("who"),
                RenderLoginSelect()
            ).Cls("auth")
        );
    }

    private HtmlElement RenderLoginSelect()
    {
        if (_store is not P2EntityStore)
        {
            // Self-contained demo: fake account picker.
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

        // p2 mode: REAL platform sign-in. The session holds the user's own JWT;
        // every write is attributed to them by the platform itself.
        if (State.UserName != null)
            return H.Button("Sign out").On("logout").Cls("btn small");

        var form = H.Form(
            H.Input().Attr("name", "email").Attr("type", "email").Attr("placeholder", "email")
                .Attr("value", "admin@test.com").Cls("login-input"),
            H.Input().Attr("name", "password").Attr("type", "password").Attr("placeholder", "password")
                .Cls("login-input"),
            H.Button("Sign in").Cls("btn small primary")
        ).On("login").Cls("loginform").Attr("data-no-reset", "");
        if (State.LoginError != null)
            form.Add(H.Small(State.LoginError).Cls("login-error"));
        return form;
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
            "__group" => Components.Group(this, node, routeParams),
            "Section" => Components.Section(this, node, routeParams),
            "Card" => Components.Card(this, node, routeParams),
            "Columns" => Components.Columns(this, node, routeParams),
            "Text" => Components.Text(this, node),
            "Image" => Components.Image(this, node),
            "FieldValue" => Components.FieldValue(this, node),
            "Button" => Components.Button(this, node),
            "MemberGate" => Components.MemberGate(this, node, routeParams),
            "EntityList" => Components.EntityList(this, node),
            "EntityCardGrid" => Components.EntityCardGrid(this, node),
            "EntityChildren" => Components.EntityChildren(this, node, routeParams),
            "ArticleCardGrid" => Components.ArticleCardGrid(this, node),
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
