// DeltaLiveView: in-memory state, delta broadcasts, stable-ID tree diffing.
//
// Design:
//   1. State lives in memory — no DB reads on render. The DB is only for
//      persistence (writes) and initial load (mount).
//   2. Broadcasts send deltas (what changed) instead of "everyone reload."
//      Other clients apply the delta to their in-memory state — no DB read.
//   3. All view code (HandleEvent, ApplyDelta, render) runs on a single
//      per-session inbox loop — no locks needed in user code, and a slow
//      view never blocks the client that triggered the broadcast.
//   4. Unchanged subtrees can be memoized with Memo(key, version, build):
//      the differ skips reference-equal nodes, so re-render + diff cost is
//      proportional to what changed, not to the page size.
//
// Subclass DeltaLiveView<TState> and implement:
//   - Mount(): load initial state from DB
//   - RenderTree(): render state to a DOM tree (no DB reads here!)
//   - HandleEvent(): update in-memory state + persist to DB + broadcast delta
//   - ApplyDelta(): apply a delta from another client to local state
using System.Buffers;
using System.Text.Json;
using System.Threading.Channels;

namespace Lmdb.LiveView;

/// <summary>A state change notification sent between LiveView instances.</summary>
public readonly record struct LiveDelta(string Type, JsonElement? Data);

internal enum InboxKind : byte { Event, Delta, Update, Resync }

internal readonly record struct InboxMessage(InboxKind Kind, string? Name, JsonElement? Data, LiveDelta Delta);

/// <summary>LiveView with in-memory state and delta-based broadcasts.</summary>
public abstract class DeltaLiveView
{
    protected HtmlElement? _lastTree;
    private int _nextId;
    private int _resyncNeeded;

    private Dictionary<object, (object Version, HtmlElement Node)> _memo = new();
    private Dictionary<object, (object Version, HtmlElement Node)>? _memoNext;

    /// <summary>Outbound messages (UTF-8 JSON) awaiting delivery to the client.
    /// Bounded: if a slow client falls too far behind, patches are dropped and a
    /// full resync (fresh init) is sent once the socket drains.</summary>
    internal Channel<byte[]> Outbound { get; } = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(256)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>Inbound work (client events, broadcast deltas). Processed by a
    /// single loop per session, so view code is effectively single-threaded.</summary>
    internal Channel<InboxMessage> Inbox { get; } = Channel.CreateUnbounded<InboxMessage>(
        new UnboundedChannelOptions { SingleReader = true });

    public string SessionId { get; internal set; } = "";

    /// <summary>The hub, set by LiveViewHub on connection. Used for broadcasting.</summary>
    protected internal LiveViewHub? Hub { get; set; }

    /// <summary>Called on connect. Load initial state from DB.</summary>
    public abstract void Mount();

    /// <summary>Render state to an HTML string. Only needed if you don't override
    /// RenderTree(); building the tree directly skips string generation + parsing.</summary>
    public virtual string Render()
        => throw new NotSupportedException($"{GetType().Name} must override Render() or RenderTree().");

    /// <summary>Render state directly to a DOM tree. NO DB READS HERE — use the
    /// in-memory state. Override this for performance — the default parses Render().</summary>
    public virtual HtmlElement RenderTree() => HtmlParser.Parse(Render());

    /// <summary>Handle a client event. Update in-memory state, persist to DB,
    /// then call BroadcastDelta to notify other clients.</summary>
    public abstract void HandleEvent(string name, JsonElement? data);

    /// <summary>Apply a delta from another client to this view's in-memory state.</summary>
    public abstract void ApplyDelta(LiveDelta delta);

    // ── Memoization ──

    /// <summary>Reuse the cached subtree for <paramref name="key"/> if
    /// <paramref name="version"/> is unchanged since the last render. Reused
    /// subtrees are skipped entirely by the differ (reference equality), making
    /// re-render + diff O(changed items) instead of O(page). Entries not touched
    /// during a render are evicted afterwards.</summary>
    protected HtmlElement Memo(object key, object version, Func<HtmlElement> build)
    {
        if (_memoNext == null)
            return build(); // called outside a render cycle

        if (_memo.TryGetValue(key, out var entry) && Equals(entry.Version, version))
        {
            _memoNext[key] = entry;
            return entry.Node;
        }

        var node = build();
        _memoNext[key] = (version, node);
        return node;
    }

    private HtmlElement RenderTreeWithMemo()
    {
        _memoNext = new Dictionary<object, (object, HtmlElement)>(_memo.Count);
        try
        {
            return RenderTree();
        }
        finally
        {
            _memo = _memoNext;
            _memoNext = null;
        }
    }

    // ── Render + diff pipeline ──

    /// <summary>Re-render from in-memory state and push the diff.</summary>
    public void PushUpdate()
    {
        if (_lastTree == null) return; // not rendered yet — init will carry current state

        var newTree = RenderTreeWithMemo();
        var patches = HtmlDiff.Diff(_lastTree, newTree, ref _nextId);
        _lastTree = newTree;

        if (patches != null)
            Enqueue(patches);
    }

    internal void SendInitialRender(string? clientFingerprint = null)
    {
        _lastTree = RenderTreeWithMemo();
        HtmlDiff.AssignIds(_lastTree, ref _nextId);
        var html = HtmlDiff.Render(_lastTree);

        // Phoenix-style connected render: if the client already has this exact
        // HTML from server-side rendering (fingerprint match), skip re-sending it.
        // ID assignment is deterministic, so the SSR DOM's data-lvid values align.
        if (clientFingerprint != null && clientFingerprint == Fingerprint(html))
            Enqueue("{\"t\":\"ok\"}"u8.ToArray());
        else
            Enqueue(InitMessage(html));
    }

    /// <summary>Fingerprint of rendered HTML, embedded in the SSR page and echoed
    /// by the client on first connect to avoid re-sending an identical render.</summary>
    public static string Fingerprint(string html)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(html)))[..16];

    /// <summary>Render the current state to HTML with IDs, for server-side rendering
    /// of the initial page (before the WebSocket connects).</summary>
    internal string RenderInitialHtml()
    {
        var tree = RenderTreeWithMemo();
        int id = 0;
        HtmlDiff.AssignIds(tree, ref id);
        return HtmlDiff.Render(tree);
    }

    private void Enqueue(byte[] message)
    {
        if (!Outbound.Writer.TryWrite(message))
            Interlocked.Exchange(ref _resyncNeeded, 1);
    }

    /// <summary>True once if patches were dropped because the client fell behind —
    /// the caller must schedule a full resync.</summary>
    internal bool ConsumeResyncFlag() => Interlocked.Exchange(ref _resyncNeeded, 0) == 1;

    private static byte[] InitMessage(string html)
    {
        var buffer = new ArrayBufferWriter<byte>(html.Length + 32);
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("t", "init");
        writer.WriteString("html", html);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    // ── Inbox loop (single-threaded view execution) ──

    internal async Task ProcessInboxAsync()
    {
        await foreach (var msg in Inbox.Reader.ReadAllAsync())
        {
            try
            {
                switch (msg.Kind)
                {
                    case InboxKind.Event: HandleEvent(msg.Name!, msg.Data); break;
                    case InboxKind.Delta: ReceiveDelta(msg.Delta); break;
                    case InboxKind.Update: PushUpdate(); break;
                    case InboxKind.Resync: SendInitialRender(); break;
                }
            }
            catch
            {
                // View code threw on one message — keep the session alive.
            }
        }
    }

    /// <summary>Apply a delta and re-render. Runs on this view's inbox loop.</summary>
    protected internal virtual void ReceiveDelta(LiveDelta delta)
    {
        ApplyDelta(delta);
        PushUpdate();
    }

    // ── Broadcasting ──

    /// <summary>Broadcast a delta to ALL other connected sessions. Each session
    /// applies the delta to its in-memory state (no DB read) and re-renders on
    /// its own inbox loop.</summary>
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
}

/// <summary>Strongly-typed version with a state object.</summary>
public abstract class DeltaLiveView<TState> : DeltaLiveView where TState : new()
{
    protected TState State { get; set; } = new();
}
