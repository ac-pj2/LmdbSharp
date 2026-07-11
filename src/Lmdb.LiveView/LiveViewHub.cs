// LiveViewHub: manages WebSocket connections, routes events to LiveView instances,
// and broadcasts updates. One hub per application (registered as a singleton).
//
// Threading model: each session runs three loops —
//   receive:  socket → parse → session inbox (never runs view code)
//   process:  inbox → HandleEvent / ReceiveDelta / render (single-threaded view code)
//   pump:     outbound channel → socket
// Broadcasts enqueue into other sessions' inboxes and return immediately, so a
// slow session never blocks the sender.
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Lmdb.LiveView;

/// <summary>One tracked presence on a topic. Connected is false while the
/// session is parked (socket dropped, resume window still open).</summary>
public sealed record PresenceEntry(string SessionId, object? Meta, bool Connected);

public sealed class LiveViewHub
{
    /// <summary>Upper bound for a single client event message.</summary>
    private const int MaxEventBytes = 1 << 20;

    private readonly ConcurrentDictionary<string, DeltaLiveView> _sessions = new();
    private readonly ConcurrentDictionary<string, (DeltaLiveView View, DateTime Expires)> _parked = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DeltaLiveView>> _topics = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, (DeltaLiveView View, object? Meta)>> _presence = new();
    private readonly Func<string, DeltaLiveView> _factory;
    private readonly Timer _sweeper;

    /// <summary>How long a disconnected session stays alive for resume. While
    /// parked it keeps applying broadcasts and buffering patches; a client
    /// reconnecting with its session id + last seq gets exactly the missed
    /// messages replayed (or a fresh init if the gap was evicted).
    /// TimeSpan.Zero disables resume.</summary>
    public TimeSpan ResumeWindow { get; set; } = TimeSpan.FromSeconds(30);

    public LiveViewHub(Func<string, DeltaLiveView> factory)
    {
        _factory = factory;
        _sweeper = new Timer(_ => CleanupExpired(), null,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    /// <summary>Render the view's initial HTML for embedding in the page response
    /// (server-side rendering). The user sees content on first paint; the WebSocket
    /// connection then takes over with a fresh init.</summary>
    public string RenderInitialHtml(string viewName)
    {
        var view = _factory(viewName);
        view.Hub = this;
        view.Mount();
        return view.RenderInitialHtml();
    }

    /// <summary>Handle a WebSocket connection. Creates a LiveView (or resumes a
    /// parked one), mounts it, and pumps messages in both directions until the
    /// connection closes. <paramref name="clientFingerprint"/> is the SSR
    /// fingerprint (?fp=); <paramref name="resumeSession"/>/<paramref name="resumeSeq"/>
    /// identify a parked session to resume (?resume=&amp;seq=).</summary>
    public async Task HandleConnectionAsync(WebSocket ws, string viewName,
        string? clientFingerprint = null, string? resumeSession = null, long resumeSeq = -1)
    {
        CleanupExpired();

        DeltaLiveView view;
        if (resumeSession != null
            && _parked.TryRemove(resumeSession, out var parked)
            && DateTime.UtcNow < parked.Expires)
        {
            // Resume: the session kept processing broadcasts while parked.
            view = parked.View;
            view.ResumeOutbound(resumeSeq);
            NotifyPresenceChanged(view, removing: false); // back online
        }
        else
        {
            var sessionId = Guid.NewGuid().ToString("N");
            view = _factory(viewName);
            view.SessionId = sessionId;
            view.Hub = this;
            _sessions[sessionId] = view;

            bool ready = false;
            try
            {
                // Mount + queue initial render before processing any events/deltas.
                view.Mount();
                view.SendInitialRender(clientFingerprint);
                view.ProcessTask = view.ProcessInboxAsync();
                ready = true;
            }
            finally
            {
                if (!ready) Teardown(view);
            }
        }

        // Pump + receive are per-connection; the view (and its inbox loop)
        // outlives them when resume is enabled.
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = PumpOutboundAsync(ws, view, pumpCts.Token);
        try
        {
            await ReceiveLoopAsync(ws, view);
        }
        finally
        {
            pumpCts.Cancel();
            try { await pumpTask; } catch { /* cancelled */ }

            if (ResumeWindow > TimeSpan.Zero)
            {
                _parked[view.SessionId] = (view, DateTime.UtcNow + ResumeWindow);
                NotifyPresenceChanged(view, removing: false); // shows as away
            }
            else
                Teardown(view);
        }
    }

    private void Teardown(DeltaLiveView view)
    {
        _sessions.TryRemove(view.SessionId, out _);
        _parked.TryRemove(view.SessionId, out _);
        NotifyPresenceChanged(view, removing: true);
        UnsubscribeAll(view);
        view.Inbox.Writer.TryComplete();
        view.Outbound.Writer.TryComplete();
    }

    private void CleanupExpired()
    {
        foreach (var (sid, entry) in _parked)
            if (DateTime.UtcNow >= entry.Expires && _parked.TryRemove(sid, out var gone))
                Teardown(gone.View);
    }

    /// <summary>Push outbound messages (diffs, broadcasts) to the WebSocket.</summary>
    private static async Task PumpOutboundAsync(WebSocket ws, DeltaLiveView view, CancellationToken ct)
    {
        try
        {
            await foreach (var msg in view.Outbound.Reader.ReadAllAsync(ct))
            {
                if (ws.State != WebSocketState.Open) break;
                await ws.SendAsync(msg, WebSocketMessageType.Text, endOfMessage: true, ct);

                // If patches were dropped (client fell behind), schedule a full
                // resync on the view's own loop once we've drained the backlog.
                if (view.ConsumeResyncFlag())
                    view.Inbox.Writer.TryWrite(new InboxMessage(InboxKind.Resync, null, null, default));
            }
        }
        catch { /* connection closed or pump cancelled */ }
    }

    /// <summary>Read events from the WebSocket and enqueue them on the view's inbox.
    /// Handles messages split across multiple frames.</summary>
    private static async Task ReceiveLoopAsync(WebSocket ws, DeltaLiveView view)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try { result = await ws.ReceiveAsync(buffer, CancellationToken.None); }
            catch { break; }

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxEventBytes)
                break; // oversized message — drop the connection

            if (!result.EndOfMessage)
                continue;

            if (result.MessageType == WebSocketMessageType.Text)
                EnqueueEvent(message.GetBuffer().AsSpan(0, (int)message.Length), view);
            message.SetLength(0);
        }
    }

    private static void EnqueueEvent(ReadOnlySpan<byte> json, DeltaLiveView view)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            string name = doc.RootElement.GetProperty("t").GetString() ?? "";
            if (name.Length == 0 || name.StartsWith("__", StringComparison.Ordinal))
                return; // internal messages (heartbeat) never reach view code

            JsonElement? data = doc.RootElement.TryGetProperty("d", out var d)
                ? d.Clone() // detach from the document so it outlives this scope
                : null;
            view.Inbox.Writer.TryWrite(new InboxMessage(InboxKind.Event, name, data, default));
        }
        catch { /* malformed event, ignore */ }
    }

    /// <summary>Broadcast an update to all connected sessions (e.g., when shared
    /// data changes outside a LiveView). Each session re-renders on its own loop.</summary>
    public void BroadcastUpdate()
    {
        foreach (var (_, view) in _sessions)
            view.Inbox.Writer.TryWrite(new InboxMessage(InboxKind.Update, null, null, default));
    }

    /// <summary>Number of currently connected sessions.</summary>
    public int SessionCount => _sessions.Count;

    // ── Delta broadcast support ──

    public void BroadcastDelta(DeltaLiveView sender, LiveDelta delta)
    {
        foreach (var (_, view) in _sessions)
        {
            if (ReferenceEquals(view, sender)) continue;
            view.Inbox.Writer.TryWrite(new InboxMessage(InboxKind.Delta, null, null, delta));
        }
    }

    /// <summary>Broadcast a delta to ALL sessions — for server-initiated pushes
    /// (background jobs, timers, external feeds) with no originating session.
    /// The payload object is delivered as-is to every session (in-process, zero
    /// serialization) — treat it as immutable after broadcasting.</summary>
    public void Broadcast(string type, object? data = null)
    {
        var delta = new LiveDelta(type, data);
        foreach (var (_, view) in _sessions)
            view.Inbox.Writer.TryWrite(new InboxMessage(InboxKind.Delta, null, null, delta));
    }

    public void BroadcastFullReload(DeltaLiveView sender)
        => BroadcastDelta(sender, new LiveDelta("reload", null));

    // ── Topics (rooms) ──
    // Sessions subscribe to named topics; topic broadcasts reach only the
    // subscribers instead of every session. Subscriptions survive parking
    // (resume) and are removed at teardown.

    /// <summary>Subscribe a session to a topic. No-op for SSR throwaway views
    /// (they have no session id and must never accumulate broadcasts).</summary>
    public void Subscribe(DeltaLiveView view, string topic)
    {
        if (string.IsNullOrEmpty(view.SessionId)) return;
        _topics.GetOrAdd(topic, _ => new ConcurrentDictionary<string, DeltaLiveView>())
               .TryAdd(view.SessionId, view);
        lock (view.TopicsLock) view.Topics.Add(topic);
    }

    public void Unsubscribe(DeltaLiveView view, string topic)
    {
        if (_topics.TryGetValue(topic, out var subs))
            subs.TryRemove(view.SessionId, out _);
        lock (view.TopicsLock) view.Topics.Remove(topic);
    }

    internal void UnsubscribeAll(DeltaLiveView view)
    {
        string[] topics;
        lock (view.TopicsLock)
        {
            topics = view.Topics.ToArray();
            view.Topics.Clear();
        }
        foreach (var t in topics)
            if (_topics.TryGetValue(t, out var subs))
                subs.TryRemove(view.SessionId, out _);
    }

    /// <summary>Broadcast a delta to every subscriber of <paramref name="topic"/>.
    /// Same zero-serialization payload contract as Broadcast.</summary>
    public void BroadcastTopic(string topic, string type, object? data = null)
    {
        if (!_topics.TryGetValue(topic, out var subs)) return;
        var delta = new LiveDelta(type, data);
        foreach (var (_, view) in subs)
            view.Inbox.Writer.TryWrite(new InboxMessage(InboxKind.Delta, null, null, delta));
    }

    /// <summary>Topic broadcast that skips the sending session.</summary>
    public void BroadcastTopicFrom(DeltaLiveView sender, string topic, LiveDelta delta)
    {
        if (!_topics.TryGetValue(topic, out var subs)) return;
        foreach (var (_, view) in subs)
        {
            if (ReferenceEquals(view, sender)) continue;
            view.Inbox.Writer.TryWrite(new InboxMessage(InboxKind.Delta, null, null, delta));
        }
    }

    // ── Presence ──
    // Who is on a topic, with metadata. Any change (track/untrack/update,
    // session teardown, park/resume connectivity) broadcasts a "presence"
    // delta with the topic name to the topic's subscribers — views typically
    // just re-render and query Presence(topic) for the current list.

    /// <summary>Track (or update) this session's presence on a topic. No-op for
    /// SSR throwaway views. Presence survives parking (shown as Connected=false)
    /// and is removed at session teardown.</summary>
    public void TrackPresence(DeltaLiveView view, string topic, object? meta = null)
    {
        if (string.IsNullOrEmpty(view.SessionId)) return;
        _presence.GetOrAdd(topic, _ => new ConcurrentDictionary<string, (DeltaLiveView, object?)>())
                 [view.SessionId] = (view, meta);
        lock (view.TopicsLock) view.PresenceTopics.Add(topic);
        BroadcastTopic(topic, "presence", topic);
    }

    public void UntrackPresence(DeltaLiveView view, string topic)
    {
        lock (view.TopicsLock) view.PresenceTopics.Remove(topic);
        if (_presence.TryGetValue(topic, out var members) && members.TryRemove(view.SessionId, out _))
            BroadcastTopic(topic, "presence", topic);
    }

    /// <summary>Current presences on a topic, ordered by session id.</summary>
    public IReadOnlyList<PresenceEntry> Presence(string topic)
    {
        if (!_presence.TryGetValue(topic, out var members)) return Array.Empty<PresenceEntry>();
        return members
            .Select(kv => new PresenceEntry(kv.Key, kv.Value.Meta, !_parked.ContainsKey(kv.Key)))
            .OrderBy(e => e.SessionId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Broadcast presence deltas for every topic the view is tracked on —
    /// used when its connectivity flips (park/resume) or it is torn down.</summary>
    private void NotifyPresenceChanged(DeltaLiveView view, bool removing)
    {
        string[] topics;
        lock (view.TopicsLock) topics = view.PresenceTopics.ToArray();
        foreach (var topic in topics)
        {
            if (removing && _presence.TryGetValue(topic, out var members))
                members.TryRemove(view.SessionId, out _);
            BroadcastTopic(topic, "presence", topic);
        }
        if (removing)
            lock (view.TopicsLock) view.PresenceTopics.Clear();
    }
}

/// <summary>DI + endpoint wiring for LiveView.</summary>
public static class LiveViewExtensions
{
    /// <summary>Register a LiveView and its hub in DI. The view is resolved from
    /// the container per connection — constructor-inject collections, services,
    /// etc. Views never take the hub in their constructor: the hub assigns
    /// <see cref="DeltaLiveView.Hub"/> before Mount().</summary>
    public static IServiceCollection AddLiveView<TView>(this IServiceCollection services)
        where TView : DeltaLiveView
    {
        services.AddSingleton(sp => new LiveViewHub(
            _ => ActivatorUtilities.CreateInstance<TView>(sp)));
        return services;
    }

    /// <summary>Map the WebSocket endpoint for the hub registered via AddLiveView.
    /// Also serves the client runtime at "{path}/client.js". Returns the hub for
    /// SSR (RenderInitialHtml) and server-initiated broadcasts.</summary>
    public static LiveViewHub MapLiveView<TView>(this WebApplication app, string path)
        where TView : DeltaLiveView
    {
        var hub = app.Services.GetRequiredService<LiveViewHub>();
        return MapEndpoints<TView>(app, path, hub);
    }

    /// <summary>Map a WebSocket endpoint with an explicit view factory (no DI).</summary>
    public static LiveViewHub MapLiveView<TView>(this WebApplication app, string path,
        Func<string, TView> factory) where TView : DeltaLiveView
    {
        var hub = new LiveViewHub(name => factory(name));
        return MapEndpoints<TView>(app, path, hub);
    }

    private static LiveViewHub MapEndpoints<TView>(WebApplication app, string path, LiveViewHub hub)
        where TView : DeltaLiveView
    {
        app.MapGet(path, async (HttpContext ctx) =>
        {
            if (ctx.WebSockets.IsWebSocketRequest)
            {
                // permessage-deflate: patch JSON is repetitive and compresses well.
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync(
                    new WebSocketAcceptContext { DangerousEnableCompression = true });
                long.TryParse(ctx.Request.Query["seq"].FirstOrDefault(), out var seq);
                await hub.HandleConnectionAsync(ws, typeof(TView).Name,
                    ctx.Request.Query["fp"].FirstOrDefault(),
                    ctx.Request.Query["resume"].FirstOrDefault(), seq);
            }
            else
            {
                ctx.Response.StatusCode = 400;
            }
        });

        // Serve the client runtime JS (also available inline via ClientRuntime.JavaScript).
        var jsBytes = Encoding.UTF8.GetBytes(ClientRuntime.JavaScript);
        var etag = "\"" + Convert.ToHexString(SHA256.HashData(jsBytes))[..16] + "\"";
        app.MapGet(path + "/client.js", (HttpContext ctx) =>
        {
            ctx.Response.Headers.ETag = etag;
            ctx.Response.Headers.CacheControl = "public, max-age=3600";
            if (ctx.Request.Headers.IfNoneMatch == etag)
                return Results.StatusCode(304);
            return Results.Bytes(jsBytes, "application/javascript");
        });

        return hub;
    }
}
