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

/// <summary>A state change notification sent between LiveView sessions.
/// Deltas never leave the process, so <see cref="Data"/> is the actual object —
/// no serialization. The SAME instance is delivered to every session: treat
/// payloads as immutable. Receivers may store the reference (replace semantics)
/// but must never mutate it.</summary>
public readonly record struct LiveDelta(string Type, object? Data);

/// <summary>Per-session observability counters, updated by the render/diff
/// pipeline. Cheap enough to leave on; render them in a dev panel to watch
/// the framework work.</summary>
public sealed class LiveViewStats
{
    public long Renders;
    public long PatchMessages;
    public long PatchBytes;
    public long MemoHits;
    public long MemoMisses;
    public double LastRenderMicros;
    public double LastDiffMicros;

    public double MemoHitRate =>
        MemoHits + MemoMisses == 0 ? 0 : (double)MemoHits / (MemoHits + MemoMisses);
}

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

    /// <summary>Observability counters for this session's render/diff pipeline.</summary>
    public LiveViewStats Stats { get; } = new();

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

    /// <summary>Apply a delta to this view's in-memory state. The framework
    /// re-renders once after a batch of queued deltas — don't call PushUpdate here.
    ///
    /// MUST be idempotent: a session is registered for broadcasts before Mount()
    /// reads the database, so a delta raced with the mount can describe a change
    /// Mount already loaded. Upserts are naturally safe; list prepends/appends
    /// must check for existing entries.
    ///
    /// Payloads are shared across sessions — replace state entries with them,
    /// never mutate them.</summary>
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
            Stats.MemoHits++;
            _memoNext[key] = entry;
            return entry.Node;
        }

        Stats.MemoMisses++;
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

        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var newTree = RenderTreeWithMemo();
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        var patches = HtmlDiff.Diff(_lastTree, newTree, ref _nextId);
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        _lastTree = newTree;

        Stats.Renders++;
        Stats.LastRenderMicros = (t1 - t0) * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;
        Stats.LastDiffMicros = (t2 - t1) * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (patches != null)
        {
            Stats.PatchMessages++;
            Stats.PatchBytes += patches.Length;
            Enqueue(patches);
        }
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

    /// <summary>Processes queued work. Bursts are coalesced: every immediately
    /// available message is handled first (deltas applied, events dispatched),
    /// then the view renders ONCE — five broadcasts arriving together cost one
    /// render + diff, not five.</summary>
    internal async Task ProcessInboxAsync()
    {
        var reader = Inbox.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            bool render = false, resync = false;
            while (reader.TryRead(out var msg))
            {
                try
                {
                    switch (msg.Kind)
                    {
                        case InboxKind.Event: HandleEvent(msg.Name!, msg.Data); break;
                        case InboxKind.Delta: ApplyDelta(msg.Delta); render = true; break;
                        case InboxKind.Update: render = true; break;
                        case InboxKind.Resync: resync = true; break;
                    }
                }
                catch
                {
                    // View code threw on one message — keep the session alive.
                }
            }
            if (resync) SendInitialRender();
            else if (render) PushUpdate();
        }
    }

    // ── Broadcasting ──

    /// <summary>Broadcast a delta to ALL other connected sessions. The payload is
    /// handed to each session's ApplyDelta as-is (in-process, zero serialization) —
    /// treat it as immutable. Each session re-renders on its own inbox loop.</summary>
    protected void BroadcastDelta(string type, object? data = null)
    {
        Hub?.BroadcastDelta(this, new LiveDelta(type, data));
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
