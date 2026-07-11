// Tests for presence tracking and the statics/dynamics template wire format.
using System.Text.Json;
using Lmdb.LiveView;

namespace LiveView.Tests;

public class PresenceTests
{
    private sealed class NullView : DeltaLiveView
    {
        public override void Mount() { }
        public override HtmlElement RenderTree() => H.Div();
        public override void HandleEvent(string name, JsonElement? data) { }
        public override void ApplyDelta(LiveDelta delta) { }
    }

    private static int PendingDeltas(DeltaLiveView view, string? ofType = null)
    {
        int n = 0;
        while (view.Inbox.Reader.TryRead(out var m))
            if (m.Kind == InboxKind.Delta && (ofType == null || m.Delta.Type == ofType)) n++;
        return n;
    }

    [Fact]
    public void Track_AppearsInPresenceList_WithMeta()
    {
        var hub = new LiveViewHub(_ => new NullView());
        var a = new NullView { SessionId = "aaa", Hub = hub };
        var b = new NullView { SessionId = "bbb", Hub = hub };

        hub.TrackPresence(a, "room", "Alice");
        hub.TrackPresence(b, "room", "Bob");

        var list = hub.Presence("room");
        Assert.Equal(2, list.Count);
        Assert.Equal("Alice", list[0].Meta);
        Assert.True(list.All(e => e.Connected));
    }

    [Fact]
    public void Track_Again_UpdatesMeta()
    {
        var hub = new LiveViewHub(_ => new NullView());
        var a = new NullView { SessionId = "aaa", Hub = hub };
        hub.TrackPresence(a, "room", "Alice");
        hub.TrackPresence(a, "room", "Alice (typing…)");

        var entry = Assert.Single(hub.Presence("room"));
        Assert.Equal("Alice (typing…)", entry.Meta);
    }

    [Fact]
    public void PresenceChanges_BroadcastToTopicSubscribers()
    {
        var hub = new LiveViewHub(_ => new NullView());
        var watcher = new NullView { SessionId = "wat", Hub = hub };
        hub.Subscribe(watcher, "room");

        var a = new NullView { SessionId = "aaa", Hub = hub };
        hub.TrackPresence(a, "room", "Alice");
        Assert.Equal(1, PendingDeltas(watcher, "presence"));

        hub.UntrackPresence(a, "room");
        Assert.Equal(1, PendingDeltas(watcher, "presence"));
        Assert.Empty(hub.Presence("room"));
    }

    [Fact]
    public void SsrThrowawayViews_NeverTracked()
    {
        var hub = new LiveViewHub(_ => new NullView());
        var ssr = new NullView { Hub = hub }; // no SessionId
        hub.TrackPresence(ssr, "room", "ghost");
        Assert.Empty(hub.Presence("room"));
    }
}

public class TemplateWireFormatTests
{
    private static HtmlElement Row(string key, string title, string cls) =>
        H.Li(
            H.Button("✓").On("toggle", key),
            H.Span(title).Cls("title"),
            H.Small(cls)
        ).Key(key).Cls(cls);

    private static HtmlElement List(params HtmlElement[] rows)
    {
        var ul = H.Ul();
        foreach (var r in rows) ul.Children.Add(r);
        return H.Div(ul);
    }

    private static List<JsonElement> Diff(HtmlElement oldT, HtmlElement newT, ref int nextId,
        HtmlDiff.TemplateTracker tracker)
    {
        var bytes = HtmlDiff.Diff(oldT, newT, ref nextId, tracker);
        if (bytes == null) return new();
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    [Fact]
    public void FirstInsert_SendsHtmlPlusTemplateDef_SecondSendsOnlyDynamics()
    {
        var tracker = new HtmlDiff.TemplateTracker();
        int nextId = 0;
        var t0 = List(Row("1", "one", "open"));
        HtmlDiff.AssignIds(t0, ref nextId);

        // Insert row 2: first sighting of this shape → html + tpl + def.
        var t1 = List(Row("1", "one", "open"), Row("2", "two", "open"));
        var p1 = Diff(t0, t1, ref nextId, tracker);
        var first = Assert.Single(p1);
        Assert.Equal("insert", first.GetProperty("t").GetString());
        Assert.True(first.TryGetProperty("html", out _));
        Assert.True(first.TryGetProperty("tpl", out var tpl1));
        Assert.True(first.TryGetProperty("def", out var def));
        Assert.False(first.TryGetProperty("d", out _));
        Assert.Equal("li", def.GetProperty("e").GetString());

        // Insert row 3: same shape → tpl + bid + dynamics, NO html.
        var t2 = List(Row("1", "one", "open"), Row("2", "two", "open"), Row("3", "three", "done"));
        var p2 = Diff(t1, t2, ref nextId, tracker);
        var second = Assert.Single(p2);
        Assert.False(second.TryGetProperty("html", out _));
        Assert.Equal(tpl1.GetString(), second.GetProperty("tpl").GetString());
        Assert.True(second.TryGetProperty("bid", out _));

        // Dynamics arrive in the client's walk order:
        // li attrs (data-event=?no — li has data-key, class), then children.
        var dyn = second.GetProperty("d").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("3", dyn);       // data-key
        Assert.Contains("done", dyn);    // class
        Assert.Contains("three", dyn);   // title text
    }

    [Fact]
    public void DynamicsOrder_MatchesDefWalk()
    {
        var tracker = new HtmlDiff.TemplateTracker();
        int nextId = 0;
        var t0 = List(Row("1", "a", "x"));
        HtmlDiff.AssignIds(t0, ref nextId);
        var t1 = List(Row("1", "a", "x"), Row("2", "b", "y"));
        Diff(t0, t1, ref nextId, tracker); // registers template

        var t2 = List(Row("1", "a", "x"), Row("2", "b", "y"), Row("9", "TITLE", "CLS"));
        var patch = Assert.Single(Diff(t1, t2, ref nextId, tracker));
        var dyn = patch.GetProperty("d").EnumerateArray().Select(e => e.GetString()).ToList();

        // Walk: li[data-key, class], button[data-event, data-id], "✓",
        //       span[class], "TITLE", small(no attrs), "CLS"
        Assert.Equal(new[] { "9", "CLS", "toggle", "9", "✓", "title", "TITLE", "CLS" }, dyn);
    }

    [Fact]
    public void SmallSubtrees_SkipTemplating()
    {
        var tracker = new HtmlDiff.TemplateTracker();
        int nextId = 0;
        var t0 = H.Div(H.Span("a").Key("1"));
        HtmlDiff.AssignIds(t0, ref nextId);
        var t1 = H.Div(H.Span("a").Key("1"), H.Span("b").Key("2")); // 2 nodes < threshold
        var patch = Assert.Single(Diff(t0, t1, ref nextId, tracker));
        Assert.True(patch.TryGetProperty("html", out _));
        Assert.False(patch.TryGetProperty("tpl", out _));
    }

    [Fact]
    public void DifferentShapes_GetDifferentTemplates()
    {
        var tracker = new HtmlDiff.TemplateTracker();
        int nextId = 0;
        var t0 = List(Row("1", "one", "x"));
        HtmlDiff.AssignIds(t0, ref nextId);
        var t1 = List(Row("1", "one", "x"), Row("2", "two", "x"));
        var tplA = Assert.Single(Diff(t0, t1, ref nextId, tracker)).GetProperty("tpl").GetString();

        // A structurally different row (extra child).
        var special = H.Li(H.Button("✓"), H.Span("s"), H.Small("m"), H.B("extra")).Key("3");
        var ul1 = (HtmlElement)t1.Children[0];
        var t2 = List(ul1.Children.Cast<HtmlElement>().Append(special).ToArray());
        var tplB = Assert.Single(Diff(t1, t2, ref nextId, tracker)).GetProperty("tpl").GetString();

        Assert.NotEqual(tplA, tplB);
    }

    [Fact]
    public void WithoutTracker_AlwaysPlainHtml()
    {
        int nextId = 0;
        var t0 = List(Row("1", "one", "x"));
        HtmlDiff.AssignIds(t0, ref nextId);
        var t1 = List(Row("1", "one", "x"), Row("2", "two", "x"));
        var bytes = HtmlDiff.Diff(t0, t1, ref nextId)!;
        using var doc = JsonDocument.Parse(bytes);
        Assert.True(doc.RootElement[0].TryGetProperty("html", out _));
        Assert.False(doc.RootElement[0].TryGetProperty("tpl", out _));
    }
}
