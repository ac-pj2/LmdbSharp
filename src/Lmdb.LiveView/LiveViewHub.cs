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

namespace Lmdb.LiveView;

public sealed class LiveViewHub
{
    /// <summary>Upper bound for a single client event message.</summary>
    private const int MaxEventBytes = 1 << 20;

    private readonly ConcurrentDictionary<string, DeltaLiveView> _sessions = new();
    private readonly Func<string, DeltaLiveView> _factory;

    public LiveViewHub(Func<string, DeltaLiveView> factory) => _factory = factory;

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

    /// <summary>Handle a WebSocket connection. Creates a LiveView, mounts it, and
    /// pumps messages in both directions until the connection closes.
    /// <paramref name="clientFingerprint"/> is the SSR fingerprint echoed by the
    /// client (?fp= query param); when it still matches, the initial render is a
    /// tiny {"t":"ok"} instead of the full HTML.</summary>
    public async Task HandleConnectionAsync(WebSocket ws, string viewName, string? clientFingerprint = null)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var view = _factory(viewName);
        view.SessionId = sessionId;
        view.Hub = this;
        _sessions[sessionId] = view;

        try
        {
            // Mount + queue initial render before processing any events/deltas.
            view.Mount();
            view.SendInitialRender(clientFingerprint);

            var processTask = view.ProcessInboxAsync();
            var pumpTask = PumpOutboundAsync(ws, view);

            await ReceiveLoopAsync(ws, view);

            view.Inbox.Writer.TryComplete();
            await processTask;
            view.Outbound.Writer.TryComplete();
            await pumpTask;
        }
        finally
        {
            _sessions.TryRemove(sessionId, out _);
            view.Inbox.Writer.TryComplete();
            view.Outbound.Writer.TryComplete();
        }
    }

    /// <summary>Push outbound messages (diffs, broadcasts) to the WebSocket.</summary>
    private static async Task PumpOutboundAsync(WebSocket ws, DeltaLiveView view)
    {
        try
        {
            await foreach (var msg in view.Outbound.Reader.ReadAllAsync())
            {
                if (ws.State != WebSocketState.Open) break;
                await ws.SendAsync(msg, WebSocketMessageType.Text, endOfMessage: true,
                    CancellationToken.None);

                // If patches were dropped (client fell behind), schedule a full
                // resync on the view's own loop once we've drained the backlog.
                if (view.ConsumeResyncFlag())
                    view.Inbox.Writer.TryWrite(new InboxMessage(InboxKind.Resync, null, null, default));
            }
        }
        catch { /* connection closed */ }
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
}

/// <summary>Extension methods for mapping LiveView WebSocket endpoints.</summary>
public static class LiveViewExtensions
{
    /// <summary>Map a WebSocket endpoint that creates LiveView instances via the
    /// factory. Also serves the client runtime at "{path}/client.js" with ETag
    /// caching. Returns the hub for SSR (RenderInitialHtml) and broadcasts.</summary>
    public static LiveViewHub MapLiveView<TView>(this WebApplication app, string path,
        Func<string, TView> factory) where TView : DeltaLiveView
    {
        var hub = new LiveViewHub(name => factory(name));

        app.MapGet(path, async (HttpContext ctx) =>
        {
            if (ctx.WebSockets.IsWebSocketRequest)
            {
                // permessage-deflate: patch JSON is repetitive and compresses well.
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync(
                    new WebSocketAcceptContext { DangerousEnableCompression = true });
                await hub.HandleConnectionAsync(ws, typeof(TView).Name,
                    ctx.Request.Query["fp"].FirstOrDefault());
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
