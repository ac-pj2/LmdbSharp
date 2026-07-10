// HtmlNode: a lightweight DOM representation for server-side rendering + diffing.
// Only supports the subset needed for LiveView patches: elements, text, attributes.
namespace Lmdb.LiveView;

internal abstract class HtmlNode
{
    internal int Id;  // sequential ID assigned during tree build for diff stability
    internal HtmlNode? Parent;
}

internal sealed class HtmlElement : HtmlNode
{
    public string Tag { get; set; } = "";
    public Dictionary<string, string> Attributes { get; set; } = new();
    public List<HtmlNode> Children { get; } = new();
}

internal sealed class HtmlText : HtmlNode
{
    public string Text { get; set; } = "";
}

/// <summary>Minimal HTML parser. Builds a DOM tree from a string. Handles the common
/// HTML5 cases: void elements (br, img, input), attributes with/without quotes,
/// and text nodes. Not a spec-compliant parser — just enough for LiveView templates.</summary>
internal static class HtmlParser
{
    private static readonly HashSet<string> VoidElements = new()
    { "area", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr" };

    /// <summary>Check if a tag is a void element (no closing tag).</summary>
    public static bool IsVoid(string tag) => VoidElements.Contains(tag);

    public static HtmlElement Parse(string html)
    {
        _idCounter = 0;  // reset so identical structures get identical IDs across renders
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

                    if (!VoidElements.Contains(tag) && !selfClosing)
                        stack.Push(el);

                    i = end + 1;
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
                    var textNode = new HtmlText { Text = text.ToString(), Parent = stack.Peek() };
                    stack.Peek().Children.Add(textNode);
                }
                i = nextTag;
            }
        }

        AssignIds(root, ref _idCounter);
        return root;
    }

    private static int _idCounter;

    private static void AssignIds(HtmlNode node, ref int id)
    {
        node.Id = id++;
        if (node is HtmlElement el)
            foreach (var child in el.Children)
                AssignIds(child, ref id);
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
                attrs[name] = value;
        }
    }
}
