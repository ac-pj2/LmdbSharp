// DI + WebSocket endpoint wiring for LiveView. Lives in Lmdb.AspNetCore so
// the LiveView core (hub, views, diffing, client runtime) carries no ASP.NET
// dependency and embedded hosts — an Android WebView bridge, a desktop shell —
// can consume it on platforms without the ASP.NET shared framework.
// The namespace stays Lmdb.LiveView so existing call sites only add a project
// reference, which every current consumer already has.
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Lmdb.LiveView;

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
                    new WebSocketAcceptContext { DangerousEnableCompression = true }).ConfigureAwait(false);
                long.TryParse(ctx.Request.Query["seq"].FirstOrDefault(), out var seq);
                await hub.HandleConnectionAsync(ws, typeof(TView).Name,
                    ctx.Request.Query["fp"].FirstOrDefault(),
                    ctx.Request.Query["resume"].FirstOrDefault(), seq).ConfigureAwait(false);
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
