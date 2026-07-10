// Incremental tree update API for DeltaLiveView.
//
// Instead of rebuilding the entire tree on every change (O(n)), views can
// directly mutate the cached tree and emit targeted patches (O(1)).
//
// The view maintains a dictionary of data-key → tree node, so it can find
// any item's <li> instantly. On a change:
//   - Update item: replace the <li> in the tree, emit a "replace" patch
//   - Add item: insert a new <li>, emit an "insert" patch
//   - Remove item: remove the <li>, emit a "remove" patch
//
// This makes toggle/add/delete O(1) regardless of list size.
using System.Collections.Concurrent;
using System.Text.Json;

namespace Lmdb.LiveView;

public abstract partial class DeltaLiveView
{
    /// <summary>Map from data-key to the tree node for that key. Lets the view
    /// find any item's DOM node in O(1) for incremental updates.</summary>
    // _keyIndex is declared in DeltaLiveView.cs (protected)

    /// <summary>Build the key index after initial render or full re-render.</summary>
    protected void RebuildKeyIndex()
    {
        _keyIndex = new ConcurrentDictionary<string, HtmlElement>();
        if (_lastTree != null)
            IndexNode(_lastTree);
    }

    private void IndexNode(HtmlNode node)
    {
        if (node is HtmlElement el)
        {
            if (el.Attributes.TryGetValue("data-key", out string? key) && !string.IsNullOrEmpty(key))
                _keyIndex![key] = el;
            foreach (var child in el.Children)
                IndexNode(child);
        }
    }

    /// <summary>Find a tree node by its data-key value. Returns null if not found.</summary>
    protected HtmlElement? FindByKey(string key)
        => _keyIndex?.TryGetValue(key, out var node) == true ? node : null;

    // ── Incremental patch emission (bypasses full rebuild + diff) ──

    /// <summary>Emit a patch directly without rebuilding the tree. Use this for
    /// targeted updates (attribute changes, text changes) on known nodes.</summary>
    protected void EmitPatch(string patchJson)
    {
        if (!string.IsNullOrEmpty(patchJson) && patchJson != "[]")
            Outbound.Writer.TryWrite("[" + patchJson + "]");
    }

    /// <summary>Emit multiple patches at once.</summary>
    protected void EmitPatches(params string[] patchJsons)
    {
        var valid = patchJsons.Where(p => !string.IsNullOrEmpty(p)).ToArray();
        if (valid.Length > 0)
            Outbound.Writer.TryWrite("[" + string.Join(",", valid) + "]");
    }

    // ── Convenience patch builders ──

    protected string PatchAttr(int nodeId, string name, string val)
        => $"{{\"t\":\"attr\",\"id\":{nodeId},\"name\":{JsonSerializer.Serialize(name)},\"val\":{JsonSerializer.Serialize(val)}}}";

    protected string PatchText(int nodeId, string val)
        => $"{{\"t\":\"text\",\"id\":{nodeId},\"val\":{JsonSerializer.Serialize(val)}}}";

    protected string PatchRemove(int nodeId)
        => $"{{\"t\":\"remove\",\"id\":{nodeId}}}";

    protected string PatchInsert(int parentId, int pos, string html)
        => $"{{\"t\":\"insert\",\"id\":{parentId},\"pos\":{pos},\"html\":{JsonSerializer.Serialize(html)}}}";

    protected string PatchReplace(int nodeId, string html)
        => $"{{\"t\":\"replace\",\"id\":{nodeId},\"html\":{JsonSerializer.Serialize(html)}}}";
}
