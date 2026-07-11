// Tests for session resume (seq'd envelopes + replay ring) and topic routing.
using System.Text.Json;
using Lmdb.LiveView;

namespace LiveView.Tests;

public class SessionResumeTests
{
    private sealed class CounterView : DeltaLiveView
    {
        public int Value;
        public override void Mount() { }
        public override HtmlElement RenderTree()
            => H.Div(H.Span(Value.ToString()));
        public override void HandleEvent(string name, JsonElement? data) { }
        public override void ApplyDelta(LiveDelta delta) { if (delta.Data is int v) Value = v; }
    }

    private static List<JsonDocument> Drain(CounterView view)
    {
        var list = new List<JsonDocument>();
        while (view.Outbound.Reader.TryRead(out var m)) list.Add(JsonDocument.Parse(m));
        return list;
    }

    private static CounterView Rendered()
    {
        var view = new CounterView { SessionId = "test-session" };
        view.SendInitialRender(); // seq 1
        return view;
    }

    [Fact]
    public void Resume_ReplaysExactlyTheMissedMessages()
    {
        var view = Rendered();
        for (int i = 1; i <= 3; i++) { view.Value = i; view.PushUpdate(); } // seq 2..4

        // Client saw up to seq 2; server replays 3 and 4 after a resume ack.
        view.ResumeOutbound(clientSeq: 2);
        var msgs = Drain(view);

        Assert.Equal(3, msgs.Count);
        Assert.Equal("r", msgs[0].RootElement.GetProperty("t").GetString());
        Assert.Equal(3, msgs[1].RootElement.GetProperty("s").GetInt64());
        Assert.Equal(4, msgs[2].RootElement.GetProperty("s").GetInt64());
        Assert.Equal("3", msgs[2].RootElement.GetProperty("p")[0].GetProperty("val").GetString());
    }

    [Fact]
    public void Resume_UpToDateClient_GetsOnlyAck()
    {
        var view = Rendered();
        view.Value = 1; view.PushUpdate(); // seq 2

        view.ResumeOutbound(clientSeq: 2);
        var msgs = Drain(view);
        var ack = Assert.Single(msgs);
        Assert.Equal("r", ack.RootElement.GetProperty("t").GetString());
    }

    [Fact]
    public void Resume_GapEvicted_FallsBackToFullInit()
    {
        var view = Rendered();
        // Push far beyond the ring capacity (128) so seq 2 is long evicted.
        for (int i = 1; i <= 200; i++) { view.Value = i; view.PushUpdate(); }

        view.ResumeOutbound(clientSeq: 1);
        var msgs = Drain(view);
        var init = Assert.Single(msgs);
        Assert.Equal("init", init.RootElement.GetProperty("t").GetString());
        Assert.Contains("200", init.RootElement.GetProperty("html").GetString());
        Assert.Equal("test-session", init.RootElement.GetProperty("sid").GetString());
    }

    [Fact]
    public void Resume_BogusSeqAhead_FallsBackToFullInit()
    {
        var view = Rendered();
        view.ResumeOutbound(clientSeq: 999);
        var msgs = Drain(view);
        Assert.Equal("init", Assert.Single(msgs).RootElement.GetProperty("t").GetString());
    }
}

public class TopicTests
{
    private sealed class NullView : DeltaLiveView
    {
        public override void Mount() { }
        public override HtmlElement RenderTree() => H.Div();
        public override void HandleEvent(string name, JsonElement? data) { }
        public override void ApplyDelta(LiveDelta delta) { }
    }

    private static int PendingDeltas(DeltaLiveView view)
    {
        int n = 0;
        while (view.Inbox.Reader.TryRead(out var m)) { if (m.Kind == InboxKind.Delta) n++; }
        return n;
    }

    [Fact]
    public void BroadcastTopic_ReachesOnlySubscribers()
    {
        var hub = new LiveViewHub(_ => new NullView());
        var a = new NullView { SessionId = "a", Hub = hub };
        var b = new NullView { SessionId = "b", Hub = hub };

        hub.Subscribe(a, "room:1");
        hub.Subscribe(b, "room:2");

        hub.BroadcastTopic("room:1", "x");
        Assert.Equal(1, PendingDeltas(a));
        Assert.Equal(0, PendingDeltas(b));
    }

    [Fact]
    public void BroadcastTopicFrom_SkipsSender()
    {
        var hub = new LiveViewHub(_ => new NullView());
        var a = new NullView { SessionId = "a", Hub = hub };
        var b = new NullView { SessionId = "b", Hub = hub };
        hub.Subscribe(a, "t");
        hub.Subscribe(b, "t");

        hub.BroadcastTopicFrom(a, "t", new LiveDelta("x", null));
        Assert.Equal(0, PendingDeltas(a));
        Assert.Equal(1, PendingDeltas(b));
    }

    [Fact]
    public void UnsubscribeAll_StopsDelivery()
    {
        var hub = new LiveViewHub(_ => new NullView());
        var a = new NullView { SessionId = "a", Hub = hub };
        hub.Subscribe(a, "t1");
        hub.Subscribe(a, "t2");

        hub.UnsubscribeAll(a);
        hub.BroadcastTopic("t1", "x");
        hub.BroadcastTopic("t2", "x");
        Assert.Equal(0, PendingDeltas(a));
    }

    [Fact]
    public void SsrThrowawayViews_NeverSubscribe()
    {
        var hub = new LiveViewHub(_ => new NullView());
        var ssr = new NullView { Hub = hub }; // no SessionId — like RenderInitialHtml views
        hub.Subscribe(ssr, "t");
        hub.BroadcastTopic("t", "x");
        Assert.Equal(0, PendingDeltas(ssr));
    }

    [Fact]
    public void PayloadPassesByReference_ThroughTopics()
    {
        var hub = new LiveViewHub(_ => new NullView());
        var a = new NullView { SessionId = "a", Hub = hub };
        hub.Subscribe(a, "t");

        var payload = new List<int> { 1, 2, 3 };
        hub.BroadcastTopic("t", "x", payload);
        Assert.True(a.Inbox.Reader.TryRead(out var msg));
        Assert.Same(payload, msg.Delta.Data);
    }
}
