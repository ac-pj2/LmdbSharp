// HtmlDiff: compares two DOM trees and produces a minimal list of patches.
// The client applies these patches to update the page without a full re-render.
//
// Patch types (sent as compact JSON):
//   {"t":"attr","id":3,"name":"class","val":"active"}      — set/change attribute
//   {"t":"delattr","id":3,"name":"disabled"}               — remove attribute
//   {"t":"text","id":5,"val":"New text"}                   — replace text content
//   {"t":"replace","id":4,"html":"<li>...</li>"}           — replace entire node
//   {"t":"insert","id":4,"pos":2,"html":"<li>new</li>"}    — insert child at position
//   {"t":"remove","id":4}                                   — remove node
//
// The diff algorithm walks both trees in parallel, comparing by node ID (assigned
// during parse). Elements with the same tag + same ID are considered "the same"
// and their attributes/children are diffed recursively. This is simpler than
// morphdom's key-based matching but works well for server-rendered templates where
// structure is mostly stable between renders.
using System.Text.Json;

namespace Lmdb.LiveView;

public static class HtmlDiff
{
    public static string Diff(HtmlNode? oldNode, HtmlNode? newNode)
    {
        var patches = new List<string>();
        var oldTree = oldNode as HtmlElement;
        var newTree = newNode as HtmlElement;
        if (oldTree != null && newTree != null)
            DiffElement(oldTree, newTree, patches);
        else if (oldTree == null && newTree != null)
            patches.Add(Patch("replace", newTree.Id, html: Render(newTree)));
        else if (oldTree != null && newNode == null)
            patches.Add(Patch("remove", oldTree.Id));

        return patches.Count == 0 ? "[]" : "[" + string.Join(",", patches) + "]";
    }

    private static void DiffElement(HtmlElement oldEl, HtmlElement newEl, List<string> patches)
    {
        // If tags differ, replace the whole element.
        if (oldEl.Tag != newEl.Tag)
        {
            patches.Add(Patch("replace", oldEl.Id, html: Render(newEl)));
            return;
        }

        // Diff attributes.
        foreach (var (name, newVal) in newEl.Attributes)
        {
            if (!oldEl.Attributes.TryGetValue(name, out string? oldVal) || oldVal != newVal)
                patches.Add(Patch("attr", newEl.Id, name: name, val: newVal));
        }
        foreach (var name in oldEl.Attributes.Keys)
        {
            if (!newEl.Attributes.ContainsKey(name))
                patches.Add(Patch("delattr", oldEl.Id, name: name));
        }

        // Diff children.
        DiffChildren(oldEl, newEl, patches);
    }

    private static void DiffChildren(HtmlElement oldEl, HtmlElement newEl, List<string> patches)
    {
        var oldChildren = oldEl.Children;
        var newChildren = newEl.Children;
        int oldCount = oldChildren.Count;
        int newCount = newChildren.Count;

        // Use a simple LCS-like approach: when tags don't match at a position,
        // look ahead in the new list to see if the old element appears later.
        // This handles insertions/removals without cascading false replacements.
        int oi = 0, ni = 0;
        while (oi < oldCount && ni < newCount)
        {
            var oldChild = oldChildren[oi];
            var newChild = newChildren[ni];

            // Same type (both text or both element with same tag)?
            if (NodeMatches(oldChild, newChild))
            {
                DiffNode(oldChild, newChild, patches);
                oi++; ni++;
                continue;
            }

            // Tags differ. Check if the old child matches a later new child (insertion happened).
            int newMatch = FindMatch(newChildren, ni + 1, oldChild);
            if (newMatch >= 0)
            {
                // New nodes were inserted before the old match. Insert them.
                for (int j = ni; j < newMatch; j++)
                    patches.Add(Patch("insert", newEl.Id, pos: j, html: Render(newChildren[j])));
                ni = newMatch;
                continue;
            }

            // Old child doesn't appear later in new. Check if new child matches a later old child (removal).
            int oldMatch = FindMatch(oldChildren, oi + 1, newChild);
            if (oldMatch >= 0)
            {
                // Old nodes were removed. Remove them.
                for (int j = oi; j < oldMatch; j++)
                    patches.Add(Patch("remove", oldChildren[j].Id));
                oi = oldMatch;
                continue;
            }

            // No match either way — replace in place.
            patches.Add(Patch("replace", oldChild.Id, html: Render(newChild)));
            oi++; ni++;
        }

        // Remaining old children → remove.
        while (oi < oldCount)
            patches.Add(Patch("remove", oldChildren[oi++].Id));

        // Remaining new children → insert.
        while (ni < newCount)
            patches.Add(Patch("insert", newEl.Id, pos: ni++, html: Render(newChildren[ni - 1])));
    }

    /// <summary>Check if two nodes match (same type, same tag, same data-key if present).</summary>
    private static bool NodeMatches(HtmlNode a, HtmlNode b)
    {
        if (a is HtmlText && b is HtmlText) return true;
        if (a is HtmlElement ea && b is HtmlElement eb)
        {
            if (ea.Tag != eb.Tag) return false;
            // If both have data-key, they must match.
            ea.Attributes.TryGetValue("data-key", out string? keyA);
            eb.Attributes.TryGetValue("data-key", out string? keyB);
            if (keyA != null || keyB != null)
                return keyA == keyB;
            return true;
        }
        return false;
    }

    /// <summary>Find the next node in the list that matches the given node by tag + data-key.</summary>
    private static int FindMatch(List<HtmlNode> list, int start, HtmlNode target)
    {
        string targetTag = target is HtmlElement te ? te.Tag : "";
        string? targetKey = target is HtmlElement tke ? tke.Attributes.GetValueOrDefault("data-key") : null;
        for (int i = start; i < list.Count; i++)
        {
            if (list[i] is HtmlElement el)
            {
                if (el.Tag != targetTag) continue;
                // If target has a data-key, the candidate must match it.
                if (targetKey != null)
                {
                    el.Attributes.TryGetValue("data-key", out string? elKey);
                    if (elKey != targetKey) continue;
                }
                return i;
            }
            if (target is HtmlText && list[i] is HtmlText)
                return i;
        }
        return -1;
    }

    private static void DiffNode(HtmlNode oldNode, HtmlNode newNode, List<string> patches)
    {
        if (oldNode is HtmlText oldText && newNode is HtmlText newText)
        {
            if (oldText.Text != newText.Text)
                // Text nodes can't have data-lvid in the DOM — target the parent element.
                patches.Add(Patch("text", newNode.Parent!.Id, val: newText.Text));
            return;
        }

        if (oldNode is HtmlElement oldEl && newNode is HtmlElement newEl)
        {
            DiffElement(oldEl, newEl, patches);
            return;
        }

        // Type mismatch (text vs element) → replace.
        patches.Add(Patch("replace", oldNode.Id, html: Render(newNode)));
    }

    // ── Patch serialization ──

    private static string Patch(string type, int id, string? name = null, string? val = null,
        int? pos = null, string? html = null)
    {
        var parts = new List<string> { $"\"t\":\"{type}\"", $"\"id\":{id}" };
        if (name != null) parts.Add($"\"name\":{JsonSerializer.Serialize(name)}");
        if (val != null) parts.Add($"\"val\":{JsonSerializer.Serialize(val)}");
        if (pos != null) parts.Add($"\"pos\":{pos}");
        if (html != null) parts.Add($"\"html\":{JsonSerializer.Serialize(html)}");
        return "{" + string.Join(",", parts) + "}";
    }

    // ── Render a node back to HTML string ──

    public static string Render(HtmlNode node)
    {
        var sb = new System.Text.StringBuilder();
        RenderTo(node, sb);
        return sb.ToString();
    }

    private static void RenderTo(HtmlNode node, System.Text.StringBuilder sb)
    {
        if (node is HtmlText text)
        {
            sb.Append(text.Text);
            return;
        }

        if (node is HtmlElement el)
        {
            sb.Append('<').Append(el.Tag);
            // Always emit data-lvid for diff targeting.
            sb.Append(" data-lvid=\"").Append(el.Id).Append('"');
            foreach (var (name, val) in el.Attributes)
            {
                sb.Append(' ').Append(name);
                if (!string.IsNullOrEmpty(val))
                    sb.Append("=\"").Append(val.Replace("\"", "&quot;")).Append('"');
            }
            sb.Append('>');

            // Don't close void elements
            if (!HtmlParser.IsVoid(el.Tag))
            {
                foreach (var child in el.Children)
                    RenderTo(child, sb);
                sb.Append("</").Append(el.Tag).Append('>');
            }
        }
    }
}
