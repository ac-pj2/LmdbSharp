// HtmlDiff: compares two DOM trees and produces a minimal list of patches.
// The client applies these patches to update the page without a full re-render.
//
// Patch types (sent as compact UTF-8 JSON):
//   {"t":"attr","id":3,"name":"class","val":"active"}      — set/change attribute
//   {"t":"delattr","id":3,"name":"disabled"}               — remove attribute
//   {"t":"text","id":5,"val":"New text"}                   — replace text content
//   {"t":"replace","id":4,"html":"<li>...</li>"}           — replace entire node
//   {"t":"insert","id":4,"pos":2,"html":"<li>new</li>"}    — insert child at position
//   {"t":"remove","id":4}                                   — remove node
//
// Node identity: IDs are STABLE. A node matched between the old and new tree
// inherits its old ID; only freshly inserted/replaced subtrees get new IDs from
// the caller's monotonic counter. This means sibling insertions never renumber
// the rest of the document — the client's id→element map stays valid forever.
//
// Insert positions are indexes into the parent's ELEMENT children (matching the
// browser's el.children). Operations that would have to target a bare text node
// (which can't carry data-lvid) fall back to replacing the parent element.
//
// Subtrees that are reference-equal between renders (memoized via
// DeltaLiveView.Memo) are skipped entirely — diffing is O(changed), not O(tree).
using System.Buffers;
using System.Text.Json;

namespace Lmdb.LiveView;

public static class HtmlDiff
{
    private enum PatchType : byte { Attr, DelAttr, Text, Replace, Insert, Remove }

    private readonly record struct Patch(PatchType Type, int Id,
        string? Name = null, string? Val = null, int Pos = -1, string? Html = null);

    /// <summary>Diff two trees. Matched nodes in <paramref name="newTree"/> inherit the
    /// old tree's IDs; new subtrees are assigned fresh IDs from <paramref name="nextId"/>.
    /// Returns the UTF-8 JSON patch array, or null if nothing changed.</summary>
    public static byte[]? Diff(HtmlElement? oldTree, HtmlElement? newTree, ref int nextId)
    {
        var patches = new List<Patch>();
        if (oldTree != null && newTree != null)
            DiffElement(oldTree, newTree, patches, ref nextId);
        else if (oldTree != null && newTree == null)
            patches.Add(new Patch(PatchType.Remove, oldTree.Id));
        else if (newTree != null)
            throw new InvalidOperationException("Cannot diff against a null old tree — send an initial render instead.");

        return patches.Count == 0 ? null : Serialize(patches);
    }

    /// <summary>Assign fresh IDs to every node in a subtree and fix up Parent links.</summary>
    public static void AssignIds(HtmlNode node, ref int nextId)
    {
        node.Id = nextId++;
        if (node is HtmlElement el)
            foreach (var child in el.Children)
            {
                child.Parent = el;
                AssignIds(child, ref nextId);
            }
    }

    private static void DiffElement(HtmlElement oldEl, HtmlElement newEl, List<Patch> patches, ref int nextId)
    {
        // Memoized subtree — same instance in both trees, nothing can have changed.
        if (ReferenceEquals(oldEl, newEl)) return;

        // Stable identity: the new node takes over the old node's ID.
        newEl.Id = oldEl.Id;

        // If tags differ, replace the whole element.
        if (oldEl.Tag != newEl.Tag)
        {
            patches.Add(ReplacePatch(oldEl.Id, newEl, ref nextId));
            return;
        }

        // Diff attributes.
        foreach (var (name, newVal) in newEl.Attributes)
        {
            if (!oldEl.Attributes.TryGetValue(name, out string? oldVal) || oldVal != newVal)
                patches.Add(new Patch(PatchType.Attr, newEl.Id, Name: name, Val: newVal));
        }
        foreach (var name in oldEl.Attributes.Keys)
        {
            if (!newEl.Attributes.ContainsKey(name))
                patches.Add(new Patch(PatchType.DelAttr, oldEl.Id, Name: name));
        }

        // Diff children granularly; if that would require targeting a bare text
        // node, roll back this element's child patches and replace it wholesale.
        int checkpoint = patches.Count;
        if (!TryDiffChildren(oldEl, newEl, patches, ref nextId))
        {
            patches.RemoveRange(checkpoint, patches.Count - checkpoint);
            patches.Add(ReplacePatch(newEl.Id, newEl, ref nextId));
        }
    }

    /// <summary>Granular child diff. Returns false if the change can only be
    /// expressed by targeting a text node (client can't address those).</summary>
    private static bool TryDiffChildren(HtmlElement oldEl, HtmlElement newEl, List<Patch> patches, ref int nextId)
    {
        var oldChildren = oldEl.Children;
        var newChildren = newEl.Children;
        int oldCount = oldChildren.Count;
        int newCount = newChildren.Count;

        // Common case: element wraps a single text node → "text" patch on the parent.
        if (oldCount == 1 && newCount == 1 && oldChildren[0] is HtmlText ot && newChildren[0] is HtmlText nt)
        {
            nt.Id = ot.Id;
            if (ot.Text != nt.Text)
                patches.Add(new Patch(PatchType.Text, newEl.Id, Val: nt.Text));
            return true;
        }

        int oi = 0, ni = 0;
        int elemPos = 0; // number of element children in newChildren[0..ni)

        while (oi < oldCount && ni < newCount)
        {
            var oldChild = oldChildren[oi];
            var newChild = newChildren[ni];

            if (ReferenceEquals(oldChild, newChild))
            {
                oi++; ni++;
                if (newChild is HtmlElement) elemPos++;
                continue;
            }

            if (NodeMatches(oldChild, newChild))
            {
                if (oldChild is HtmlText oldText && newChild is HtmlText newText)
                {
                    newText.Id = oldText.Id;
                    if (oldText.Text != newText.Text)
                        return false; // text change in mixed content → replace parent
                    oi++; ni++;
                    continue;
                }

                DiffElement((HtmlElement)oldChild, (HtmlElement)newChild, patches, ref nextId);
                oi++; ni++; elemPos++;
                continue;
            }

            // No direct match. Check if the old child appears later in the new list
            // (nodes were inserted before it).
            int newMatch = FindMatch(newChildren, ni + 1, oldChild);
            if (newMatch >= 0)
            {
                for (int j = ni; j < newMatch; j++)
                {
                    patches.Add(InsertPatch(newEl.Id, elemPos, newChildren[j], ref nextId));
                    if (newChildren[j] is HtmlElement) elemPos++;
                }
                ni = newMatch;
                continue;
            }

            // Check if the new child appears later in the old list (nodes were removed).
            int oldMatch = FindMatch(oldChildren, oi + 1, newChild);
            if (oldMatch >= 0)
            {
                for (int j = oi; j < oldMatch; j++)
                {
                    if (oldChildren[j] is not HtmlElement removed)
                        return false; // can't remove a bare text node → replace parent
                    patches.Add(new Patch(PatchType.Remove, removed.Id));
                }
                oi = oldMatch;
                continue;
            }

            // No match either way — replace in place (must target an element).
            if (oldChild is not HtmlElement)
                return false;
            patches.Add(ReplacePatch(oldChild.Id, newChild, ref nextId));
            oi++; ni++;
            if (newChild is HtmlElement) elemPos++;
        }

        // Remaining old children → remove (elements only).
        while (oi < oldCount)
        {
            if (oldChildren[oi] is not HtmlElement removed)
                return false;
            patches.Add(new Patch(PatchType.Remove, removed.Id));
            oi++;
        }

        // Remaining new children → insert at the end.
        while (ni < newCount)
        {
            patches.Add(InsertPatch(newEl.Id, elemPos, newChildren[ni], ref nextId));
            if (newChildren[ni] is HtmlElement) elemPos++;
            ni++;
        }

        return true;
    }

    private static Patch ReplacePatch(int targetId, HtmlNode replacement, ref int nextId)
    {
        AssignIds(replacement, ref nextId);
        return new Patch(PatchType.Replace, targetId, Html: Render(replacement));
    }

    private static Patch InsertPatch(int parentId, int elemPos, HtmlNode node, ref int nextId)
    {
        AssignIds(node, ref nextId);
        return new Patch(PatchType.Insert, parentId, Pos: elemPos, Html: Render(node));
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
            if (target is HtmlText)
            {
                if (list[i] is HtmlText) return i;
                continue;
            }
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
        }
        return -1;
    }

    // ── Patch serialization (UTF-8 JSON, single buffer) ──

    private static readonly JsonEncodedText TProp = JsonEncodedText.Encode("t");
    private static readonly JsonEncodedText IdProp = JsonEncodedText.Encode("id");
    private static readonly JsonEncodedText NameProp = JsonEncodedText.Encode("name");
    private static readonly JsonEncodedText ValProp = JsonEncodedText.Encode("val");
    private static readonly JsonEncodedText PosProp = JsonEncodedText.Encode("pos");
    private static readonly JsonEncodedText HtmlProp = JsonEncodedText.Encode("html");

    private static readonly JsonEncodedText[] TypeNames =
    {
        JsonEncodedText.Encode("attr"), JsonEncodedText.Encode("delattr"),
        JsonEncodedText.Encode("text"), JsonEncodedText.Encode("replace"),
        JsonEncodedText.Encode("insert"), JsonEncodedText.Encode("remove"),
    };

    private static byte[] Serialize(List<Patch> patches)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartArray();
        foreach (var p in patches)
        {
            writer.WriteStartObject();
            writer.WriteString(TProp, TypeNames[(int)p.Type]);
            writer.WriteNumber(IdProp, p.Id);
            if (p.Name != null) writer.WriteString(NameProp, p.Name);
            if (p.Val != null) writer.WriteString(ValProp, p.Val);
            if (p.Pos >= 0) writer.WriteNumber(PosProp, p.Pos);
            if (p.Html != null) writer.WriteString(HtmlProp, p.Html);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    // ── Render a node back to HTML (escaped) ──

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
            AppendEscaped(sb, text.Text, forAttribute: false);
            return;
        }

        if (node is HtmlElement el)
        {
            sb.Append('<').Append(el.Tag);
            // Always emit data-lvid for diff targeting.
            if (el.Id >= 0)
                sb.Append(" data-lvid=\"").Append(el.Id).Append('"');
            foreach (var (name, val) in el.Attributes)
            {
                sb.Append(' ').Append(name);
                if (!string.IsNullOrEmpty(val))
                {
                    sb.Append("=\"");
                    AppendEscaped(sb, val, forAttribute: true);
                    sb.Append('"');
                }
            }
            sb.Append('>');

            // Don't close void elements
            if (!HtmlParser.IsVoid(el.Tag))
            {
                // style/script content is raw text — HTML-escaping would corrupt
                // CSS/JS (e.g. child selectors). Never place untrusted input here.
                bool raw = el.Tag is "style" or "script";
                foreach (var child in el.Children)
                {
                    if (raw && child is HtmlText rawText) sb.Append(rawText.Text);
                    else RenderTo(child, sb);
                }
                sb.Append("</").Append(el.Tag).Append('>');
            }
        }
    }

    private static void AppendEscaped(System.Text.StringBuilder sb, string s, bool forAttribute)
    {
        foreach (char c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"' when forAttribute: sb.Append("&quot;"); break;
                default: sb.Append(c); break;
            }
        }
    }
}
