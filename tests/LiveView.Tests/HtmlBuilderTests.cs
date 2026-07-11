// Tests for the H builder DSL, raw-text style/script handling, and DevPanel.
using Lmdb.LiveView;

namespace LiveView.Tests;

public class HtmlBuilderTests
{
    [Fact]
    public void Builder_StringsBecomeTextChildren()
    {
        var el = H.Div(H.B("bold"), " between ", H.Span("span"));
        Assert.Equal(3, el.Children.Count);
        Assert.Equal(" between ", Assert.IsType<HtmlText>(el.Children[1]).Text);
        Assert.Equal("bold", Assert.IsType<HtmlText>(((HtmlElement)el.Children[0]).Children[0]).Text);
    }

    [Fact]
    public void Builder_FluentAttributes()
    {
        var el = H.Button("ack").On("ack", 42).Cls("small").Key("i42").Debounce(250);
        Assert.Equal("ack", el.Attributes["data-event"]);
        Assert.Equal("42", el.Attributes["data-id"]);
        Assert.Equal("small", el.Attributes["class"]);
        Assert.Equal("i42", el.Attributes["data-key"]);
        Assert.Equal("250", el.Attributes["data-debounce"]);
    }

    [Fact]
    public void Builder_AddRangeAndAddIf()
    {
        var ul = H.Ul()
            .AddRange(Enumerable.Range(0, 3).Select(i => (HtmlNode)H.Li(i.ToString())))
            .AddIf(true, () => H.Li("yes"))
            .AddIf(false, () => H.Li("no"));
        Assert.Equal(4, ul.Children.Count);
    }

    [Fact]
    public void Builder_TextIsEscapedAtRender()
    {
        var el = H.Span("<script>alert(1)</script>");
        int id = 0;
        HtmlDiff.AssignIds(el, ref id);
        var html = HtmlDiff.Render(el);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void StyleContent_RendersRaw_NotEscaped()
    {
        var el = H.Style(".a { color: red; } .b .c { margin: 0; }");
        int id = 0;
        HtmlDiff.AssignIds(el, ref id);
        var html = HtmlDiff.Render(el);
        Assert.Contains(".a { color: red; }", html);
        Assert.DoesNotContain("&", html);
    }

    [Fact]
    public void StyleContent_ParserRoundTripsRaw()
    {
        var tree = HtmlParser.Parse("<div><style>.x { top: 0; }</style><p>a &amp; b</p></div>");
        var div = Assert.IsType<HtmlElement>(tree.Children[0]);
        var style = Assert.IsType<HtmlElement>(div.Children[0]);
        Assert.Equal(".x { top: 0; }", Assert.IsType<HtmlText>(style.Children[0]).Text);
        // Regular text still entity-decoded.
        var p = Assert.IsType<HtmlElement>(div.Children[1]);
        Assert.Equal("a & b", Assert.IsType<HtmlText>(p.Children[0]).Text);
    }

    private sealed class NullView : DeltaLiveView
    {
        public override void Mount() { }
        public override HtmlElement RenderTree() => H.Div();
        public override void HandleEvent(string name, System.Text.Json.JsonElement? data) { }
        public override void ApplyDelta(LiveDelta delta) { }
    }

    [Fact]
    public void DevPanel_RendersDrawerWithClientZones()
    {
        var panel = DevPanel.Render(new NullView(), ("extra", "42"));
        int id = 0;
        HtmlDiff.AssignIds(panel, ref id);
        var html = HtmlDiff.Render(panel);

        Assert.Contains("id=\"lv-dev\"", html);
        Assert.Contains("hidden", html);                         // starts closed
        Assert.Contains("toggle #lv-dev with lv-slide", html);   // client-side toggle
        Assert.Contains("id=\"lv-dev-client\"", html);
        Assert.Contains("data-lv-ignore", html);                 // client-owned zones
        Assert.Contains("memo hit rate", html);
        Assert.Contains("extra", html);
        Assert.Contains(".lv-dev-toggle", html);                 // CSS shipped raw
        Assert.DoesNotContain("&gt;", html);
    }
}
