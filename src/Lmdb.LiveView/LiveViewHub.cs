// LiveViewHub: manages WebSocket connections, routes events to LiveView instances,
// and broadcasts updates. One hub per application (registered as a singleton).
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Lmdb.LiveView;

public sealed class LiveViewHub
{
    private readonly ConcurrentDictionary<string, LiveView> _sessions = new();
    private readonly Func<string, LiveView> _factory;

    public LiveViewHub(Func<string, LiveView> factory) => _factory = factory;

    /// <summary>Handle a WebSocket connection. Creates a LiveView, mounts it, and
    /// pumps messages in both directions until the connection closes.</summary>
    public async Task HandleConnectionAsync(WebSocket ws, string viewName)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var view = _factory(viewName);
        view.SessionId = sessionId;
        _sessions[sessionId] = view;

        try
        {
            // Mount + send initial render.
            view.Mount();
            view.SendInitialRender();

            // Start the outbound pump (pushes diffs to the client).
            var pumpTask = PumpOutboundAsync(ws, view);

            // Receive loop (reads events from the client).
            await ReceiveLoopAsync(ws, view);

            // Wait for the pump to finish.
            view.Outbound.Writer.TryComplete();
            await pumpTask;
        }
        finally
        {
            _sessions.TryRemove(sessionId, out _);
        }
    }

    /// <summary>Push outbound messages (diffs, broadcasts) to the WebSocket.</summary>
    private static async Task PumpOutboundAsync(WebSocket ws, LiveView view)
    {
        try
        {
            await foreach (var msg in view.Outbound.Reader.ReadAllAsync())
            {
                if (ws.State != WebSocketState.Open) break;
                var bytes = Encoding.UTF8.GetBytes(msg);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true,
                    CancellationToken.None);
            }
        }
        catch { /* connection closed */ }
    }

    /// <summary>Read events from the WebSocket and dispatch to the LiveView.</summary>
    private static async Task ReceiveLoopAsync(WebSocket ws, LiveView view)
    {
        var buffer = new byte[16 * 1024];
        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
                break;

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                ProcessEvent(json, view);
            }
        }
    }

    private static void ProcessEvent(string json, LiveView view)
    {
        try
        {
            var evt = JsonSerializer.Deserialize<JsonElement>(json);
            string name = evt.GetProperty("t").GetString() ?? "";
            JsonElement? data = evt.TryGetProperty("d", out var d) ? d : null;
            view.HandleEvent(name, data);
        }
        catch { /* malformed event, ignore */ }
    }

    /// <summary>Broadcast an update to all connected sessions (e.g., when shared
    /// data changes). Each LiveView re-renders and pushes its diff.</summary>
    public void BroadcastUpdate()
    {
        foreach (var (_, view) in _sessions)
            view.PushUpdate();
    }
}

/// <summary>Extension methods for mapping LiveView WebSocket endpoints.</summary>
public static class LiveViewExtensions
{
    /// <summary>Map a WebSocket endpoint that creates LiveView instances via the factory.</summary>
    public static WebApplication MapLiveView<TView>(this WebApplication app, string path,
        Func<string, TView> factory) where TView : LiveView
    {
        var hub = new LiveViewHub(name => factory(name));

        app.MapGet(path, async (HttpContext ctx) =>
        {
            if (ctx.WebSockets.IsWebSocketRequest)
            {
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                await hub.HandleConnectionAsync(ws, typeof(TView).Name);
            }
            else
            {
                ctx.Response.StatusCode = 400;
            }
        });

        // Serve the client runtime JS.
        app.MapGet(path + "/client.js", () => Results.Text(ClientRuntime.JavaScript, "application/javascript"));

        return app;
    }
}
