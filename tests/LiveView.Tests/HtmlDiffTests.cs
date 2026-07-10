// Tests for the LiveView diff engine: escaping, stable IDs, element-anchored
// inserts, text-node fallbacks, and memoization fast paths.
using System.Text;
using System.Text.Json;
using Lmdb.LiveView;

namespace LiveView.Tests;

public class HtmlDiffTests
{
    // ── helpers ──

    private static HtmlElement El(string tag, Dictionary<string, string>? attrs = null, params HtmlNode[] children)
    {
        var el = new HtmlElement { Tag = tag, Attributes = attrs ?? new() };
        foreach (var c in children) el.Children.Add(c);
        return el;
    }

    private static HtmlText Text(string s) => new() { Text = s };

    private static List<JsonElement> Diff(HtmlElement oldTree, HtmlElement newTree, ref int nextId)
    {
        var bytes = HtmlDiff.Diff(oldTree, newTree, ref nextId);
        if (bytes == null) return new();
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static (HtmlElement tree, int nextId) Assigned(HtmlElement tree)
    {
        int nextId = 0;
        HtmlDiff.AssignIds(tree, ref nextId);
        return (tree, nextId);
    }

    // ── escaping (XSS) ──

    [Fact]
    public void Render_EscapesTextNodes()
    {
        var (tree, _) = Assigned(El("span", null, Text("<img src=x onerror=alert(1)> & <b>")));
        var html = HtmlDiff.Render(tree);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt; &amp; &lt;b&gt;", html);
    }

    [Fact]
    public void Render_EscapesAttributeValues()
    {
        var (tree, _) = Assigned(El("div", new() { ["title"] = "a\"b <c> & d" }));
        var html = HtmlDiff.Render(tree);
        Assert.Contains("title=\"a&quot;b &lt;c&gt; &amp; d\"", html);
    }

    [Fact]
    public void Parser_RoundTripsEntities()
    {
        var tree = HtmlParser.Parse("<span title=\"a&quot;b\">x &lt;y&gt; &amp; z</span>");
        var span = Assert.IsType<HtmlElement>(tree.Children[0]);
        Assert.Equal("a\"b", span.Attributes["title"]);
        Assert.Equal("x <y> & z", Assert.IsType<HtmlText>(span.Children[0]).Text);

        // Re-render escapes again — no double escaping, no raw injection.
        int id = 0;
        HtmlDiff.AssignIds(tree, ref id);
        var html = HtmlDiff.Render(span);
        Assert.Contains("x &lt;y&gt; &amp; z", html);
    }

    // ── stable IDs ──

    [Fact]
    public void Diff_InsertInMiddle_DoesNotRenumberSiblings()
    {
        var (oldTree, nextId) = Assigned(El("ul", null,
            El("li", new() { ["data-key"] = "1" }, Text("one")),
            El("li", new() { ["data-key"] = "3" }, Text("three"))));
        int lastLiId = oldTree.Children[1].Id;

        // New tree: insert key=2 in the middle AND change the text of key=3.
        var newTree = El("ul", null,
            El("li", new() { ["data-key"] = "1" }, Text("one")),
            El("li", new() { ["data-key"] = "2" }, Text("two")),
            El("li", new() { ["data-key"] = "3" }, Text("THREE")));

        var patches = Diff(oldTree, newTree, ref nextId);

        var insert = Assert.Single(patches, p => p.GetProperty("t").GetString() == "insert");
        Assert.Equal(1, insert.GetProperty("pos").GetInt32());

        // The text patch must target the ORIGINAL id of key=3 — stable across the insert.
        var text = Assert.Single(patches, p => p.GetProperty("t").GetString() == "text");
        Assert.Equal(lastLiId, text.GetProperty("id").GetInt32());
        Assert.Equal("THREE", text.GetProperty("val").GetString());

        // Inserted subtree got fresh ids (present in its html).
        Assert.Contains("data-lvid", insert.GetProperty("html").GetString());
    }

    [Fact]
    public void Diff_FreshIdsNeverCollideWithExisting()
    {
        var (oldTree, nextId) = Assigned(El("ul", null,
            El("li", new() { ["data-key"] = "1" })));
        int maxOldId = nextId - 1;

        var newTree = El("ul", null,
            El("li", new() { ["data-key"] = "1" }),
            El("li", new() { ["data-key"] = "2" }));

        var patches = Diff(oldTree, newTree, ref nextId);
        var insert = Assert.Single(patches);
        var html = insert.GetProperty("html").GetString()!;
        // data-lvid in the inserted html must be > all previously assigned ids.
        var idStr = html.Split("data-lvid=\"")[1].Split('"')[0];
        Assert.True(int.Parse(idStr) > maxOldId);
    }

    // ── insert positions (element children, matching browser el.children) ──

    [Fact]
    public void Diff_InsertPosition_CountsElementChildrenOnly()
    {
        // Parent with text nodes between elements: <div>a<b/>c<i/></div>
        var (oldTree, nextId) = Assigned(El("div", null,
            Text("a"), El("b"), Text("c"), El("i")));

        // Insert <u/> before <i/> (element position 1, not raw position 3).
        var newTree = El("div", null,
            Text("a"), El("b"), Text("c"), El("u"), El("i"));

        var patches = Diff(oldTree, newTree, ref nextId);
        var insert = Assert.Single(patches);
        Assert.Equal("insert", insert.GetProperty("t").GetString());
        Assert.Equal(1, insert.GetProperty("pos").GetInt32());
    }

    // ── text node handling ──

    [Fact]
    public void Diff_SingleTextChild_EmitsTextPatch()
    {
        var (oldTree, nextId) = Assigned(El("h1", null, Text("Todos (3 pending)")));
        var newTree = El("h1", null, Text("Todos (2 pending)"));

        var patches = Diff(oldTree, newTree, ref nextId);
        var p = Assert.Single(patches);
        Assert.Equal("text", p.GetProperty("t").GetString());
        Assert.Equal(oldTree.Id, p.GetProperty("id").GetInt32());
        Assert.Equal("Todos (2 pending)", p.GetProperty("val").GetString());
    }

    [Fact]
    public void Diff_TextChangeInMixedContent_ReplacesParent()
    {
        // <li><b>x</b> old text</li> → text change with element sibling: the client
        // can't target a bare text node, so the whole parent is replaced.
        var (oldTree, nextId) = Assigned(El("div", null,
            El("li", null, El("b", null, Text("x")), Text("old text"))));
        var newTree = El("div", null,
            El("li", null, El("b", null, Text("x")), Text("new text")));

        var patches = Diff(oldTree, newTree, ref nextId);
        var p = Assert.Single(patches);
        Assert.Equal("replace", p.GetProperty("t").GetString());
        Assert.Equal(oldTree.Children[0].Id, p.GetProperty("id").GetInt32());
        Assert.Contains("new text", p.GetProperty("html").GetString());
    }

    [Fact]
    public void Diff_UnchangedMixedContent_EmitsGranularPatches()
    {
        // Static text separators shouldn't force parent replacement when only
        // an element child changes.
        var (oldTree, nextId) = Assigned(El("li", null,
            El("button", null, Text("✓")), Text(" "), El("span", null, Text("title"))));
        var newTree = El("li", null,
            El("button", null, Text("☐")), Text(" "), El("span", null, Text("title")));

        var patches = Diff(oldTree, newTree, ref nextId);
        var p = Assert.Single(patches);
        Assert.Equal("text", p.GetProperty("t").GetString());
        Assert.Equal("☐", p.GetProperty("val").GetString());
    }

    // ── attributes ──

    [Fact]
    public void Diff_AttributeChanges()
    {
        var (oldTree, nextId) = Assigned(El("div", new() { ["class"] = "a", ["gone"] = "1" }));
        var newTree = El("div", new() { ["class"] = "b", ["added"] = "2" });

        var patches = Diff(oldTree, newTree, ref nextId);
        Assert.Equal(3, patches.Count);
        Assert.Contains(patches, p => p.GetProperty("t").GetString() == "attr" && p.GetProperty("name").GetString() == "class" && p.GetProperty("val").GetString() == "b");
        Assert.Contains(patches, p => p.GetProperty("t").GetString() == "attr" && p.GetProperty("name").GetString() == "added");
        Assert.Contains(patches, p => p.GetProperty("t").GetString() == "delattr" && p.GetProperty("name").GetString() == "gone");
    }

    // ── keyed removal ──

    [Fact]
    public void Diff_KeyedRemoval_RemovesOnlyThatNode()
    {
        var (oldTree, nextId) = Assigned(El("ul", null,
            El("li", new() { ["data-key"] = "1" }, Text("one")),
            El("li", new() { ["data-key"] = "2" }, Text("two")),
            El("li", new() { ["data-key"] = "3" }, Text("three"))));
        int removedId = oldTree.Children[1].Id;

        var newTree = El("ul", null,
            El("li", new() { ["data-key"] = "1" }, Text("one")),
            El("li", new() { ["data-key"] = "3" }, Text("three")));

        var patches = Diff(oldTree, newTree, ref nextId);
        var p = Assert.Single(patches);
        Assert.Equal("remove", p.GetProperty("t").GetString());
        Assert.Equal(removedId, p.GetProperty("id").GetInt32());
    }

    // ── memoization ──

    [Fact]
    public void Diff_ReferenceEqualSubtrees_ProduceNoPatches()
    {
        var sharedRow = El("li", new() { ["data-key"] = "1" }, Text("unchanged"));
        var (oldTree, nextId) = Assigned(El("ul", null, sharedRow));

        // New render reuses the same instance (as Memo does).
        var newTree = El("ul", null, sharedRow);

        var bytes = HtmlDiff.Diff(oldTree, newTree, ref nextId);
        Assert.Null(bytes);
    }

    [Fact]
    public void Diff_MemoizedSiblings_OnlyChangedRowPatched()
    {
        var row1 = El("li", new() { ["data-key"] = "1" }, Text("one"));
        var row2 = El("li", new() { ["data-key"] = "2" }, Text("two"));
        var (oldTree, nextId) = Assigned(El("ul", null, row1, row2));

        var newTree = El("ul", null, row1,
            El("li", new() { ["data-key"] = "2" }, Text("TWO")));

        var patches = Diff(oldTree, newTree, ref nextId);
        var p = Assert.Single(patches);
        Assert.Equal("text", p.GetProperty("t").GetString());
        Assert.Equal(row2.Id, p.GetProperty("id").GetInt32());
    }

    [Fact]
    public void Diff_NoChanges_ReturnsNull()
    {
        var (oldTree, nextId) = Assigned(El("div", new() { ["class"] = "x" },
            El("span", null, Text("hello"))));
        var newTree = El("div", new() { ["class"] = "x" },
            El("span", null, Text("hello")));

        Assert.Null(HtmlDiff.Diff(oldTree, newTree, ref nextId));
    }

    // ── patch encoding ──

    [Fact]
    public void Patches_AreValidUtf8Json()
    {
        var (oldTree, nextId) = Assigned(El("div", null, El("span", null, Text("a"))));
        var newTree = El("div", null, El("span", null, Text("日本語 \"quoted\" <tag>")));

        var bytes = HtmlDiff.Diff(oldTree, newTree, ref nextId)!;
        using var doc = JsonDocument.Parse(bytes);
        var val = doc.RootElement[0].GetProperty("val").GetString();
        Assert.Equal("日本語 \"quoted\" <tag>", val);
    }
}
