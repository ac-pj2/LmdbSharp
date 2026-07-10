// Optimized LiveView: in-memory state, delta broadcasts, cached tree.
//
// Key changes from the original:
//   1. State lives in memory — no DB reads on render. The DB is only for
//      persistence (writes) and initial load (mount).
//   2. Broadcasts send deltas (what changed) instead of "everyone reload."
//      Other clients apply the delta to their in-memory state — no DB read.
//   3. The rendered DOM tree is cached. On re-render, we only re-parse if
//      the HTML actually changed. The diff runs against the cached tree.
//
// Subclass DeltaLiveView<TState, TDelta> and implement:
//   - Mount(): load initial state from DB
//   - Render(): render state to HTML (no DB reads here!)
//   - HandleEvent(): update in-memory state + persist to DB + broadcast delta
//   - ApplyDelta(): apply a delta from another client to local state
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace Lmdb.LiveView;

/// <summary>A state change notification sent between LiveView instances.</summary>
public readonly record struct LiveDelta(string Type, JsonElement? Data);

/// <summary>LiveView with in-memory state and delta-based broadcasts.</summary>
public abstract partial class DeltaLiveView
{
    protected string _lastRenderedHtml = "";
    protected HtmlElement? _lastTree;
    protected ConcurrentDictionary<string, HtmlElement>? _keyIndex;

    internal Channel<string> Outbound { get; } = Channel.CreateUnbounded<string>();
    public string SessionId { get; internal set; } = "";

    /// <summary>Called on connect. Load initial state from DB.</summary>
    public abstract void Mount();

    /// <summary>Render state to HTML. NO DB READS HERE — use the in-memory state.</summary>
    public abstract string Render();

    /// <summary>Render state directly to a DOM tree (skips HTML string generation + parsing).
    /// Override this for performance — the default builds a tree from Render() output.</summary>
    public virtual HtmlElement RenderTree()
    {
        var html = Render();
        return HtmlParser.Parse(html);
    }

    /// <summary>Assign sequential IDs to all nodes in a tree. Call after building
    /// a tree directly (not via HtmlParser.Parse).</summary>
    protected static void AssignTreeIds(HtmlElement root)
    {
        int id = 0;
        AssignIdsInternal(root, ref id);
    }

    private static void AssignIdsInternal(HtmlNode node, ref int id)
    {
        node.Id = id++;
        if (node is HtmlElement el)
            foreach (var child in el.Children)
            { child.Parent = el; AssignIdsInternal(child, ref id); }
    }

    /// <summary>Handle a client event. Update in-memory state, persist to DB,
    /// then call BroadcastDelta to notify other clients.</summary>
    public abstract void HandleEvent(string name, JsonElement? data);

    /// <summary>Apply a delta from another client to this view's in-memory state.</summary>
    public abstract void ApplyDelta(LiveDelta delta);

    /// <summary>The hub, set by LiveViewHub on connection. Used for broadcasting.</summary>
    protected internal LiveViewHub? Hub { get; set; }

    /// <summary>Re-render from in-memory state and push the diff.</summary>
    public void PushUpdate()
    {
        var newTree = RenderTree();
        var newHtml = HtmlDiff.Render(newTree);
        if (newHtml == _lastRenderedHtml) return;

        var diff = HtmlDiff.Diff(_lastTree, newTree);

        if (diff != "[]")
            Outbound.Writer.TryWrite(diff);

        _lastRenderedHtml = newHtml;
        _lastTree = newTree;
    }

    /// <summary>Broadcast a delta to ALL other connected clients. Each client
    /// applies the delta to its in-memory state (no DB read) and re-renders.</summary>
    protected void BroadcastDelta(string type, object? data = null)
    {
        Hub?.BroadcastDelta(this, new LiveDelta(type,
            data != null ? JsonSerializer.SerializeToElement(data) : null));
    }

    /// <summary>Broadcast a full state reload (fallback for complex changes).</summary>
    protected void BroadcastFullReload()
    {
        Hub?.BroadcastFullReload(this);
    }

    internal void SendInitialRender()
    {
        _lastTree = RenderTree();
        _lastRenderedHtml = HtmlDiff.Render(_lastTree);
        RebuildKeyIndex();
        Outbound.Writer.TryWrite(JsonSerializer.Serialize(new
        { t = "init", html = _lastRenderedHtml }));
    }

    /// <summary>Apply a delta and re-render. Called by the hub when another
    /// client broadcasts a change. Override for incremental updates.</summary>
    internal virtual void ReceiveDelta(LiveDelta delta)
    {
        ApplyDelta(delta);
        PushUpdate();
    }
}

/// <summary>Strongly-typed version with a state object.</summary>
public abstract class DeltaLiveView<TState> : DeltaLiveView where TState : new()
{
    protected TState State { get; set; } = new();
}
