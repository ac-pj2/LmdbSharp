// LiveView: the core abstraction. Each LiveView manages one connected client's
// state, renders HTML, computes diffs, and pushes patches over the WebSocket.
//
// Subclass LiveView<TState> and implement Render(). The framework handles the
// WebSocket lifecycle, diffing, and event dispatch.
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace Lmdb.LiveView;

/// <summary>Base class for a live view. TState is the server-side state object.</summary>
public abstract class LiveView
{
    private string _lastRenderedHtml = "";
    private HtmlElement? _lastTree;

    /// <summary>The WebSocket channel for pushing patches to the client.</summary>
    internal Channel<string> Outbound { get; } = Channel.CreateUnbounded<string>();

    /// <summary>Unique session ID.</summary>
    public string SessionId { get; internal set; } = "";

    /// <summary>Called when a client connects. Return initial state and render.</summary>
    public abstract string Mount();

    /// <summary>Render the current state to an HTML string.</summary>
    public abstract string Render();

    /// <summary>Handle a client event (button click, form input, etc.).</summary>
    public abstract void HandleEvent(string name, JsonElement? data);

    /// <summary>Re-render and push the diff to the client. Called after state changes.</summary>
    public void PushUpdate()
    {
        var newHtml = Render();
        if (newHtml == _lastRenderedHtml) return;

        var newTree = HtmlParser.Parse(newHtml);
        var diff = HtmlDiff.Diff(_lastTree, newTree);

        if (diff != "[]")
            Outbound.Writer.TryWrite(diff);

        _lastRenderedHtml = newHtml;
        _lastTree = newTree;
    }

    /// <summary>Send the initial full render (no diff).</summary>
    internal void SendInitialRender()
    {
        _lastRenderedHtml = Render();
        _lastTree = HtmlParser.Parse(_lastRenderedHtml);
        Outbound.Writer.TryWrite(JsonSerializer.Serialize(new
        { t = "init", html = _lastRenderedHtml }));
    }
}

/// <summary>Strongly-typed LiveView with a state object.</summary>
public abstract class LiveView<TState> : LiveView where TState : new()
{
    protected TState State { get; set; } = new();
}
