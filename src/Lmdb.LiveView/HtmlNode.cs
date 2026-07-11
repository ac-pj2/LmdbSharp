// HtmlNode: a lightweight DOM representation for server-side rendering + diffing.
// Only supports the subset needed for LiveView patches: elements, text, attributes.
using System.Net;

namespace Lmdb.LiveView;

public abstract class HtmlNode
{
    /// <summary>Stable node ID (data-lvid). Assigned by the differ: matched nodes
    /// inherit their previous ID, new subtrees get fresh IDs from the view's counter.
    /// IDs never shift when siblings are inserted/removed.</summary>
    public int Id = -1;
    public HtmlNode? Parent;

    /// <summary>Strings used where a node is expected become text nodes —
    /// lets builders write H.B(item.Title) instead of wrapping in HtmlText.</summary>
    public static implicit operator HtmlNode(string text) => new HtmlText { Text = text };
}

public sealed class HtmlElement : HtmlNode
{
    public string Tag { get; set; } = "";
    public Dictionary<string, string> Attributes { get; set; } = new();
    public List<HtmlNode> Children { get; } = new();
}

public sealed class HtmlText : HtmlNode
{
    /// <summary>Raw (unescaped) text. Escaped at serialization time.</summary>
    public string Text { get; set; } = "";
}

/// <summary>Minimal HTML parser. Builds a DOM tree from a string. Handles the common
/// HTML5 cases: void elements (br, img, input), attributes with/without quotes,
/// and text nodes. Not a spec-compliant parser — just enough for LiveView templates.</summary>
public static class HtmlParser
{
    private static readonly HashSet<string> VoidElements = new()
    { "area", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr" };

    /// <summary>Check if a tag is a void element (no closing tag).</summary>
    public static bool IsVoid(string tag) => VoidElements.Contains(tag);

    public static HtmlElement Parse(string html)
    {
        var root = new HtmlElement { Tag = "div" };
        var stack = new Stack<HtmlElement>();
        stack.Push(root);
        int i = 0;

        while (i < html.Length)
        {
            if (html[i] == '<')
            {
                if (i + 1 < html.Length && html[i + 1] == '/')
                {
                    // Closing tag
                    int end = html.IndexOf('>', i);
                    if (end < 0) break;
                    if (stack.Count > 1) stack.Pop();
                    i = end + 1;
                }
                else if (i + 3 < html.Length && html.AsSpan(i, 4).SequenceEqual("<!--"))
                {
                    // Comment
                    int end = html.IndexOf("-->", i);
                    i = end < 0 ? html.Length : end + 3;
                }
                else
                {
                    // Opening tag
                    int end = html.IndexOf('>', i);
                    if (end < 0) break;
                    var tagContent = html.AsSpan(i + 1, end - i - 1).Trim();
                    bool selfClosing = tagContent.EndsWith("/>");
                    if (selfClosing) tagContent = tagContent[..^1].Trim();

                    var (tag, attrs) = ParseTag(tagContent);
                    var el = new HtmlElement { Tag = tag, Attributes = attrs };
                    el.Parent = stack.Peek();
                    stack.Peek().Children.Add(el);

                    i = end + 1;

                    // style/script hold raw text (no entity decoding, no nested tags).
                    if (tag is "style" or "script")
                    {
                        string close = "</" + tag;
                        int rawEnd = html.IndexOf(close, i, StringComparison.OrdinalIgnoreCase);
                        if (rawEnd < 0) rawEnd = html.Length;
                        var raw = html.AsSpan(i, rawEnd - i).Trim();
                        if (!raw.IsEmpty)
                            el.Children.Add(new HtmlText { Text = raw.ToString(), Parent = el });
                        int closeEnd = html.IndexOf('>', rawEnd);
                        i = closeEnd < 0 ? html.Length : closeEnd + 1;
                    }
                    else if (!VoidElements.Contains(tag) && !selfClosing)
                        stack.Push(el);
                }
            }
            else
            {
                // Text node
                int nextTag = html.IndexOf('<', i);
                if (nextTag < 0) nextTag = html.Length;
                var text = html.AsSpan(i, nextTag - i).Trim();
                if (!text.IsEmpty)
                {
                    // Decode entities so the tree holds raw text (re-escaped on render).
                    var textNode = new HtmlText { Text = WebUtility.HtmlDecode(text.ToString()), Parent = stack.Peek() };
                    stack.Peek().Children.Add(textNode);
                }
                i = nextTag;
            }
        }

        return root;
    }

    private static (string tag, Dictionary<string, string> attrs) ParseTag(ReadOnlySpan<char> content)
    {
        int spaceIdx = content.IndexOf(' ');
        string tag = spaceIdx < 0 ? content.ToString() : content[..spaceIdx].ToString();
        var attrs = new Dictionary<string, string>();

        if (spaceIdx > 0)
        {
            var rest = content[spaceIdx..].Trim();
            ParseAttributes(rest, attrs);
        }

        return (tag, attrs);
    }

    private static void ParseAttributes(ReadOnlySpan<char> span, Dictionary<string, string> attrs)
    {
        int i = 0;
        while (i < span.Length)
        {
            // Skip whitespace
            while (i < span.Length && char.IsWhiteSpace(span[i])) i++;
            if (i >= span.Length) break;

            // Read attribute name
            int nameStart = i;
            while (i < span.Length && span[i] != '=' && !char.IsWhiteSpace(span[i])) i++;
            string name = span[nameStart..i].ToString();

            // Skip whitespace
            while (i < span.Length && char.IsWhiteSpace(span[i])) i++;

            string value = "";
            if (i < span.Length && span[i] == '=')
            {
                i++; // skip =
                while (i < span.Length && char.IsWhiteSpace(span[i])) i++;

                if (i < span.Length && (span[i] == '"' || span[i] == '\''))
                {
                    char quote = span[i++];
                    int valEnd = span.Slice(i).IndexOf(quote);
                    if (valEnd < 0) valEnd = span.Length - i;
                    value = span.Slice(i, valEnd).ToString();
                    i += valEnd + 1;
                }
                else
                {
                    int valStart = i;
                    while (i < span.Length && !char.IsWhiteSpace(span[i])) i++;
                    value = span[valStart..i].ToString();
                }
            }

            if (!string.IsNullOrEmpty(name))
                attrs[name] = WebUtility.HtmlDecode(value);
        }
    }
}
