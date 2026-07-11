// H: a compact builder for HtmlElement trees. Strings become text nodes
// (implicit conversion on HtmlNode), attributes chain fluently:
//
//   H.Div(
//       H.Span().Cls("dot " + n.Status),
//       H.B(n.Name),
//       H.Span(n.Region).Cls("region")
//   ).Cls("card " + n.Status).Key(n.Id)
//
//   H.Button("ack").On("ack", incident.Id)          // data-event + data-id
//   H.Button("⚙ dev").Client("toggle #dev with slide")
//   H.Ul().Cls("incidents").AddRange(items.Select(BuildRow))
//
// Text is escaped at render time — pass user input straight in.
namespace Lmdb.LiveView;

public static class H
{
    public static HtmlText Text(string s) => new() { Text = s };

    public static HtmlElement El(string tag, params HtmlNode[] children)
    {
        var el = new HtmlElement { Tag = tag };
        foreach (var c in children) el.Children.Add(c);
        return el;
    }

    public static HtmlElement Div(params HtmlNode[] c) => El("div", c);
    public static HtmlElement Span(params HtmlNode[] c) => El("span", c);
    public static HtmlElement P(params HtmlNode[] c) => El("p", c);
    public static HtmlElement B(params HtmlNode[] c) => El("b", c);
    public static HtmlElement Small(params HtmlNode[] c) => El("small", c);
    public static HtmlElement H1(params HtmlNode[] c) => El("h1", c);
    public static HtmlElement H2(params HtmlNode[] c) => El("h2", c);
    public static HtmlElement H3(params HtmlNode[] c) => El("h3", c);
    public static HtmlElement Ul(params HtmlNode[] c) => El("ul", c);
    public static HtmlElement Li(params HtmlNode[] c) => El("li", c);
    public static HtmlElement Button(params HtmlNode[] c) => El("button", c);
    public static HtmlElement Form(params HtmlNode[] c) => El("form", c);
    public static HtmlElement Input() => El("input");
    public static HtmlElement Select(params HtmlNode[] c) => El("select", c);
    public static HtmlElement Option(params HtmlNode[] c) => El("option", c);
    public static HtmlElement Label(params HtmlNode[] c) => El("label", c);
    public static HtmlElement A(params HtmlNode[] c) => El("a", c);
    public static HtmlElement Header(params HtmlNode[] c) => El("header", c);
    public static HtmlElement Aside(params HtmlNode[] c) => El("aside", c);
    public static HtmlElement Section(params HtmlNode[] c) => El("section", c);
    public static HtmlElement Canvas() => El("canvas");

    /// <summary>A style element with raw (unescaped) CSS. Never interpolate
    /// untrusted input into the CSS.</summary>
    public static HtmlElement Style(string css) => El("style", Text(css));
}

/// <summary>Fluent attribute helpers for builder-constructed elements.</summary>
public static class HtmlBuilderExtensions
{
    public static HtmlElement Attr(this HtmlElement el, string name, string value)
    {
        el.Attributes[name] = value;
        return el;
    }

    /// <summary>Set the class attribute.</summary>
    public static HtmlElement Cls(this HtmlElement el, string cls) => el.Attr("class", cls);

    /// <summary>Set the id attribute.</summary>
    public static HtmlElement Id(this HtmlElement el, string id) => el.Attr("id", id);

    /// <summary>Set data-key — the differ's identity for keyed siblings.</summary>
    public static HtmlElement Key(this HtmlElement el, object key) => el.Attr("data-key", key.ToString()!);

    /// <summary>Wire a server event: data-event (+ data-id when given).</summary>
    public static HtmlElement On(this HtmlElement el, string @event, object? id = null)
    {
        el.Attributes["data-event"] = @event;
        if (id != null) el.Attributes["data-id"] = id.ToString()!;
        return el;
    }

    /// <summary>Wire client-side commands (data-client) — run instantly, no server.</summary>
    public static HtmlElement Client(this HtmlElement el, string commands) => el.Attr("data-client", commands);

    /// <summary>Debounce this input's data-event while typing (data-debounce).</summary>
    public static HtmlElement Debounce(this HtmlElement el, int ms) => el.Attr("data-debounce", ms.ToString());

    /// <summary>Mark a client-owned subtree that patches must not touch (data-lv-ignore).</summary>
    public static HtmlElement Ignore(this HtmlElement el) => el.Attr("data-lv-ignore", "");

    public static HtmlElement Hidden(this HtmlElement el, bool hidden = true)
    {
        if (hidden) el.Attributes["hidden"] = "";
        else el.Attributes.Remove("hidden");
        return el;
    }

    /// <summary>Append a child.</summary>
    public static HtmlElement Add(this HtmlElement el, HtmlNode child)
    {
        el.Children.Add(child);
        return el;
    }

    /// <summary>Append children — for list comprehensions:
    /// H.Ul().AddRange(items.Select(BuildRow)).</summary>
    public static HtmlElement AddRange(this HtmlElement el, IEnumerable<HtmlNode> children)
    {
        foreach (var c in children) el.Children.Add(c);
        return el;
    }

    /// <summary>Append a child only when <paramref name="condition"/> holds.</summary>
    public static HtmlElement AddIf(this HtmlElement el, bool condition, Func<HtmlNode> child)
    {
        if (condition) el.Children.Add(child());
        return el;
    }
}
