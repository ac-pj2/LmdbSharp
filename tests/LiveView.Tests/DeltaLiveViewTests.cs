// Tests for the DeltaLiveView inbox loop: burst coalescing (N deltas → one
// render) and zero-serialization payload passthrough.
using System.Text.Json;
using Lmdb.LiveView;

namespace LiveView.Tests;

public class DeltaLiveViewTests
{
    private sealed class TestView : DeltaLiveView
    {
        public int Applied;
        public readonly List<object?> Payloads = new();
        public int Value;
        public int EventsHandled;

        public override void Mount() { }

        public override HtmlElement RenderTree()
        {
            var root = new HtmlElement { Tag = "div" };
            var span = new HtmlElement { Tag = "span" };
            span.Children.Add(new HtmlText { Text = Value.ToString() });
            root.Children.Add(span);
            return root;
        }

        public override void HandleEvent(string name, JsonElement? data) => EventsHandled++;

        public override void ApplyDelta(LiveDelta delta)
        {
            Applied++;
            Payloads.Add(delta.Data);
            if (delta.Data is int v) Value = v;
        }
    }

    private static InboxMessage Delta(string type, object? data = null)
        => new(InboxKind.Delta, null, null, new LiveDelta(type, data));

    [Fact]
    public async Task BurstOfDeltas_AppliesAll_RendersOnce()
    {
        var view = new TestView();
        view.SendInitialRender();

        for (int i = 1; i <= 5; i++)
            view.Inbox.Writer.TryWrite(Delta("set", i));
        view.Inbox.Writer.TryComplete();
        await view.ProcessInboxAsync();

        Assert.Equal(5, view.Applied);
        Assert.Equal(1, view.Stats.Renders);

        // Outbound: the init message plus exactly ONE patch frame carrying the
        // final value — intermediate values were never rendered.
        var messages = new List<byte[]>();
        while (view.Outbound.Reader.TryRead(out var m)) messages.Add(m);
        Assert.Equal(2, messages.Count);
        using var doc = JsonDocument.Parse(messages[1]);
        Assert.Equal("p", doc.RootElement.GetProperty("t").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("s").GetInt64()); // init was seq 1
        Assert.Equal("5", doc.RootElement.GetProperty("p")[0].GetProperty("val").GetString());
    }

    [Fact]
    public async Task DeltaPayload_IsPassedByReference_NoSerialization()
    {
        var view = new TestView();
        view.SendInitialRender();

        var payload = new List<string> { "not", "serializable", "types", "are", "fine" };
        view.Inbox.Writer.TryWrite(Delta("x", payload));
        view.Inbox.Writer.TryComplete();
        await view.ProcessInboxAsync();

        Assert.Same(payload, Assert.Single(view.Payloads));
    }

    [Fact]
    public async Task MixedBatch_EventsRunInOrder_SingleRenderAtEnd()
    {
        var view = new TestView();
        view.SendInitialRender();

        view.Inbox.Writer.TryWrite(Delta("set", 1));
        view.Inbox.Writer.TryWrite(new InboxMessage(InboxKind.Event, "click", null, default));
        view.Inbox.Writer.TryWrite(Delta("set", 2));
        view.Inbox.Writer.TryComplete();
        await view.ProcessInboxAsync();

        Assert.Equal(2, view.Applied);
        Assert.Equal(1, view.EventsHandled);
        Assert.Equal(1, view.Stats.Renders);
    }

    [Fact]
    public async Task NoChanges_NoPatchFrame()
    {
        var view = new TestView();
        view.SendInitialRender();

        view.Inbox.Writer.TryWrite(Delta("noop"));   // ApplyDelta runs but Value unchanged
        view.Inbox.Writer.TryComplete();
        await view.ProcessInboxAsync();

        Assert.Equal(1, view.Stats.Renders);          // rendered once…
        Assert.Equal(0, view.Stats.PatchMessages);    // …but nothing changed, nothing sent
        var messages = new List<byte[]>();
        while (view.Outbound.Reader.TryRead(out var m)) messages.Add(m);
        Assert.Single(messages); // just the init
    }
}
