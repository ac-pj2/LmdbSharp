using Lmdb.LiveView;
using Lmdb.Objects;
using System.Collections.Concurrent;
using System.Text.Json;

namespace LiveTodo;

public class TodoState
{
    public List<Todo> Items { get; set; } = new();
    public long? EditingId { get; set; }
    public string? EditingTitle { get; set; }
    public string FilterTag { get; set; } = "";
}

/// <summary>Delta types for broadcasting changes between clients.</summary>
public record TodoDelta(string Type, Todo? Item = null, long? DeletedId = null);

/// <summary>Optimized LiveView: state in memory, DB only for persistence.</summary>
public class TodoLiveView : DeltaLiveView<TodoState>
{
    private readonly Collection<Todo> _todos;

    public TodoLiveView(Collection<Todo> todos, LiveViewHub hub)
    {
        _todos = todos;
        Hub = hub;
    }

    public override void Mount()
    {
        // Only DB read: load initial state on connect.
        using var txn = _todos.Database.BeginRead();
        State.Items = _todos.Scan(txn).OrderBy(t => t.Id).ToList();
    }

    public override string Render() => HtmlDiff.Render(RenderTree());


    /// <summary>Build the DOM tree directly — skips HTML string generation + re-parsing.
    /// For 1000 items this saves ~2ms of parsing and ~5000 allocations.</summary>
    public override HtmlElement RenderTree()
    {
        var root = new HtmlElement { Tag = "div" };

        var visible = State.FilterTag == ""
            ? State.Items
            : State.Items.Where(t => t.Tags.Contains(State.FilterTag)).ToList();

        // <h1>Todos <small>(N pending)</small></h1>
        var h1 = new HtmlElement { Tag = "h1" };
        h1.Children.Add(new HtmlText { Text = $"Todos " });
        var small = new HtmlElement { Tag = "small" };
        small.Children.Add(new HtmlText { Text = $"({visible.Count(t => !t.Completed)} pending)" });
        h1.Children.Add(small);
        root.Children.Add(h1);

        // Filter bar
        if (State.FilterTag != "")
        {
            var p = new HtmlElement { Tag = "p" };
            p.Children.Add(new HtmlText { Text = $"Filtering by: " });
            var b = new HtmlElement { Tag = "b" };
            b.Children.Add(new HtmlText { Text = State.FilterTag });
            p.Children.Add(b);
            p.Children.Add(new HtmlText { Text = " " });
            var clr = new HtmlElement { Tag = "button", Attributes = new() { ["data-event"] = "clearfilter" } };
            clr.Children.Add(new HtmlText { Text = "clear" });
            p.Children.Add(clr);
            root.Children.Add(p);
        }

        // Add form
        var form = new HtmlElement { Tag = "form", Attributes = new() { ["data-event"] = "add" } };
        var input = new HtmlElement { Tag = "input", Attributes = new() { ["name"] = "title", ["placeholder"] = "Add a todo..." } };
        form.Children.Add(input);
        var select = new HtmlElement { Tag = "select", Attributes = new() { ["name"] = "priority" } };
        select.Children.Add(MakeOption("1", "Low", false));
        select.Children.Add(MakeOption("2", "Medium", true));
        select.Children.Add(MakeOption("3", "High", false));
        form.Children.Add(select);
        var addBtn = new HtmlElement { Tag = "button" };
        addBtn.Children.Add(new HtmlText { Text = "+" });
        form.Children.Add(addBtn);
        root.Children.Add(form);

        // List
        var ul = new HtmlElement { Tag = "ul" };
        foreach (var item in visible)
        {
            var li = new HtmlElement { Tag = "li", Attributes = new() { ["data-key"] = item.Id.ToString(), ["class"] = item.Completed ? "done" : "" } };

            var toggleBtn = new HtmlElement { Tag = "button", Attributes = new() { ["data-key"] = "toggle", ["data-event"] = "toggle", ["data-id"] = item.Id.ToString() } };
            toggleBtn.Children.Add(new HtmlText { Text = item.Completed ? "☐" : "✓" });
            li.Children.Add(toggleBtn);
            li.Children.Add(new HtmlText { Text = " " });

            if (State.EditingId == item.Id)
            {
                var editForm = new HtmlElement { Tag = "form", Attributes = new() { ["data-key"] = "title", ["data-event"] = "save", ["style"] = "display:inline" } };
                var editInput = new HtmlElement { Tag = "input", Attributes = new() { ["name"] = "title", ["value"] = State.EditingTitle ?? item.Title } };
                editForm.Children.Add(editInput);
                var saveBtn = new HtmlElement { Tag = "button" };
                saveBtn.Children.Add(new HtmlText { Text = "💾" });
                editForm.Children.Add(saveBtn);
                li.Children.Add(editForm);
                li.Children.Add(new HtmlText { Text = " " });
                var cancelBtn = new HtmlElement { Tag = "button", Attributes = new() { ["data-key"] = "cancel", ["data-event"] = "cancel" } };
                cancelBtn.Children.Add(new HtmlText { Text = "✕" });
                li.Children.Add(cancelBtn);
            }
            else
            {
                var titleSpan = new HtmlElement { Tag = "span", Attributes = new() { ["data-key"] = "title", ["data-event"] = "edit", ["data-id"] = item.Id.ToString(), ["style"] = "cursor:text" } };
                titleSpan.Children.Add(new HtmlText { Text = item.Title });
                li.Children.Add(titleSpan);
            }

            li.Children.Add(new HtmlText { Text = " " });
            var priSpan = new HtmlElement { Tag = "span", Attributes = new() { ["data-key"] = "priority" } };
            priSpan.Children.Add(new HtmlText { Text = item.Priority switch { 3 => "🔴", 2 => "🟡", _ => "🟢" } });
            li.Children.Add(priSpan);

            foreach (var tag in item.Tags)
            {
                li.Children.Add(new HtmlText { Text = " " });
                var tagSpan = new HtmlElement { Tag = "span", Attributes = new() { ["class"] = "tag", ["data-event"] = "filtertag", ["data-tag"] = tag } };
                tagSpan.Children.Add(new HtmlText { Text = $"#{tag}" });
                li.Children.Add(tagSpan);
            }

            li.Children.Add(new HtmlText { Text = " " });
            var delBtn = new HtmlElement { Tag = "button", Attributes = new() { ["data-key"] = "delete", ["data-event"] = "delete", ["data-id"] = item.Id.ToString(), ["style"] = "color:red" } };
            delBtn.Children.Add(new HtmlText { Text = "×" });
            li.Children.Add(delBtn);

            ul.Children.Add(li);
        }
        root.Children.Add(ul);

        AssignTreeIds(root);
        return root;
    }

    private static HtmlElement MakeOption(string value, string label, bool selected)
    {
        var opt = new HtmlElement { Tag = "option", Attributes = new() { ["value"] = value } };
        if (selected) opt.Attributes["selected"] = "";
        opt.Children.Add(new HtmlText { Text = label });
        return opt;
    }

    public override void HandleEvent(string name, JsonElement? data)
    {
        switch (name)
        {
            case "add":
            {
                var title = data?.GetProperty("title").GetString() ?? "";
                var priority = int.TryParse(data?.GetProperty("priority").GetString() ?? "2", out var p) ? p : 2;
                if (!string.IsNullOrWhiteSpace(title))
                {
                    using var txn = _todos.Database.BeginWrite();
                    var todo = new Todo { Title = title, Priority = priority };
                    _todos.Insert(txn, todo);
                    txn.Commit();

                    State.Items.Add(todo);

                    // Incremental: insert just the new <li> at the end of <ul>
                    var li = BuildListItem(todo);
                    var ul = FindByPath(_lastTree, "ul");
                    if (ul != null)
                    {
                        int pos = ul.Children.Count;
                        ul.Children.Add(li);
                        li.Parent = ul;
                        ReindexSubtree(li);
                        EmitPatches(
                            PatchInsert(ul.Id, pos, HtmlDiff.Render(li)),
                            UpdateCounter()
                        );
                    }
                    else { PushUpdate(); }

                    BroadcastDelta("add", todo);
                }
                break;
            }

            case "toggle":
            {
                if (long.TryParse(data?.GetProperty("id").GetString(), out var id))
                {
                    using var txn = _todos.Database.BeginWrite();
                    var todo = _todos.Get(txn, id);
                    if (todo != null)
                    {
                        todo.Completed = !todo.Completed;
                        _todos.Update(txn, todo);
                        txn.Commit();

                        var local = State.Items.FirstOrDefault(t => t.Id == id);
                        if (local != null) local.Completed = todo.Completed;

                        // Incremental: replace just the <li> for this item
                        var oldLi = FindByKey(id.ToString());
                        if (oldLi != null)
                        {
                            var newLi = BuildListItem(local ?? todo);
                            newLi.Parent = oldLi.Parent;
                            var parent = (HtmlElement)oldLi.Parent!;
                            int idx = parent.Children.IndexOf(oldLi);
                            parent.Children[idx] = newLi;
                            ReindexSubtree(newLi);
                            EmitPatches(
                                PatchReplace(oldLi.Id, HtmlDiff.Render(newLi)),
                                UpdateCounter()
                            );
                        }
                        else { PushUpdate(); }

                        BroadcastDelta("update", todo);
                    }
                }
                break;
            }

            case "delete":
            {
                if (long.TryParse(data?.GetProperty("id").GetString(), out var id))
                {
                    using var txn = _todos.Database.BeginWrite();
                    _todos.Delete(txn, id);
                    txn.Commit();

                    State.Items.RemoveAll(t => t.Id == id);
                    if (State.EditingId == id) State.EditingId = null;

                    // Incremental: remove the <li>
                    var li = FindByKey(id.ToString());
                    if (li != null)
                    {
                        var parent = (HtmlElement)li.Parent!;
                        parent.Children.Remove(li);
                        _keyIndex?.TryRemove(id.ToString(), out _);
                        EmitPatches(
                            PatchRemove(li.Id),
                            UpdateCounter()
                        );
                    }
                    else { PushUpdate(); }

                    BroadcastDelta("delete", new TodoDelta("delete", DeletedId: id));
                }
                break;
            }

            case "edit":
            case "save":
            case "cancel":
            case "addtag":
            case "filtertag":
            case "clearfilter":
                // These change local-only or structural state — use full re-render
                HandleStructuralEvent(name, data);
                break;
        }
    }

    private void HandleStructuralEvent(string name, JsonElement? data)
    {
        switch (name)
        {
            case "edit":
                if (long.TryParse(data?.GetProperty("id").GetString(), out var editId))
                {
                    var todo = State.Items.FirstOrDefault(t => t.Id == editId);
                    if (todo != null)
                    {
                        State.EditingId = editId;
                        State.EditingTitle = todo.Title;
                        PushUpdate();
                    }
                }
                break;

            case "save":
            {
                var newTitle = data?.GetProperty("title").GetString() ?? "";
                long? savedId = State.EditingId;
                if (State.EditingId.HasValue && !string.IsNullOrWhiteSpace(newTitle))
                {
                    var id = State.EditingId.Value;
                    using var txn = _todos.Database.BeginWrite();
                    var todo = _todos.Get(txn, id);
                    if (todo != null)
                    {
                        todo.Title = newTitle;
                        _todos.Update(txn, todo);
                        txn.Commit();
                        var local = State.Items.FirstOrDefault(t => t.Id == id);
                        if (local != null) local.Title = newTitle;
                    }
                }
                State.EditingId = null;
                PushUpdate();
                if (savedId.HasValue)
                    BroadcastDelta("update", State.Items.FirstOrDefault(t => t.Id == savedId));
                break;
            }

            case "cancel":
                State.EditingId = null;
                PushUpdate();
                break;

            case "addtag":
            {
                var tag = data?.GetProperty("tag").GetString() ?? "";
                var tagIdStr = data?.GetProperty("id").GetString() ?? "";
                if (long.TryParse(tagIdStr, out var tagId) && !string.IsNullOrWhiteSpace(tag))
                {
                    tag = tag.Trim().ToLowerInvariant();
                    using var txn = _todos.Database.BeginWrite();
                    var todo = _todos.Get(txn, tagId);
                    if (todo != null && !todo.Tags.Contains(tag))
                    {
                        todo.Tags.Add(tag);
                        _todos.Update(txn, todo);
                        txn.Commit();
                        var local = State.Items.FirstOrDefault(t => t.Id == tagId);
                        if (local != null && !local.Tags.Contains(tag))
                            local.Tags.Add(tag);
                        PushUpdate();
                        BroadcastDelta("update", todo);
                    }
                }
                break;
            }

            case "filtertag":
                State.FilterTag = data?.GetProperty("tag").GetString() ?? "";
                PushUpdate();
                break;

            case "clearfilter":
                State.FilterTag = "";
                PushUpdate();
                break;
        }
    }

    /// <summary>Apply a delta from another client. Updates in-memory state — NO DB READ.</summary>
    public override void ApplyDelta(LiveDelta delta)
    {
        if (delta.Type == "reload")
        {
            // Fallback: full reload from DB.
            using var txn = _todos.Database.BeginRead();
            State.Items = _todos.Scan(txn).OrderBy(t => t.Id).ToList();
            return;
        }

        if (delta.Type == "add" && delta.Data.HasValue)
        {
            var todo = delta.Data.Value.Deserialize<Todo>();
            if (todo != null) State.Items.Add(todo);
        }
        else if (delta.Type == "update" && delta.Data.HasValue)
        {
            var todo = delta.Data.Value.Deserialize<Todo>();
            if (todo != null)
            {
                var idx = State.Items.FindIndex(t => t.Id == todo.Id);
                if (idx >= 0) State.Items[idx] = todo;
            }
        }
        else if (delta.Type == "delete" && delta.Data.HasValue)
        {
            var d = delta.Data.Value.Deserialize<TodoDelta>();
            if (d?.DeletedId != null)
                State.Items.RemoveAll(t => t.Id == d.DeletedId);
        }
    }

    /// <summary>Override ReceiveDelta for incremental updates on broadcast.</summary>
    protected internal override void ReceiveDelta(LiveDelta delta)
    {
        ApplyDelta(delta);

        if (delta.Type == "add" && delta.Data.HasValue)
        {
            var todo = delta.Data.Value.Deserialize<Todo>();
            if (todo != null)
            {
                var li = BuildListItem(todo);
                var ul = FindByPath(_lastTree, "ul");
                if (ul != null)
                {
                    int pos = ul.Children.Count;
                    ul.Children.Add(li);
                    li.Parent = ul;
                    ReindexSubtree(li);
                    EmitPatches(PatchInsert(ul.Id, pos, HtmlDiff.Render(li)), UpdateCounter());
                    return;
                }
            }
            PushUpdate();
        }
        else if (delta.Type == "delete" && delta.Data.HasValue)
        {
            var d = delta.Data.Value.Deserialize<TodoDelta>();
            if (d?.DeletedId != null)
            {
                var li = FindByKey(d.DeletedId.ToString());
                if (li != null)
                {
                    var parent = (HtmlElement)li.Parent!;
                    parent.Children.Remove(li);
                    _keyIndex?.TryRemove(d.DeletedId.ToString(), out _);
                    EmitPatches(PatchRemove(li.Id), UpdateCounter());
                    return;
                }
            }
            PushUpdate();
        }
        else if (delta.Type == "update" && delta.Data.HasValue)
        {
            var todo = delta.Data.Value.Deserialize<Todo>();
            if (todo != null)
            {
                var oldLi = FindByKey(todo.Id.ToString());
                if (oldLi != null)
                {
                    var newLi = BuildListItem(todo);
                    newLi.Parent = oldLi.Parent;
                    var parent = (HtmlElement)oldLi.Parent!;
                    int idx = parent.Children.IndexOf(oldLi);
                    parent.Children[idx] = newLi;
                    ReindexSubtree(newLi);
                    EmitPatches(PatchReplace(oldLi.Id, HtmlDiff.Render(newLi)));
                    return;
                }
            }
            PushUpdate();
        }
        else
        {
            PushUpdate();
        }
    }

    private static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);

    // ── Incremental rendering helpers ──

    /// <summary>Build a single <li> for a todo item (same structure as RenderTree).</summary>
    private HtmlElement BuildListItem(Todo item)
    {
        var li = new HtmlElement { Tag = "li", Attributes = new() { ["data-key"] = item.Id.ToString(), ["class"] = item.Completed ? "done" : "" } };

        var toggleBtn = new HtmlElement { Tag = "button", Attributes = new() { ["data-key"] = "toggle", ["data-event"] = "toggle", ["data-id"] = item.Id.ToString() } };
        toggleBtn.Children.Add(new HtmlText { Text = item.Completed ? "☐" : "✓" });
        li.Children.Add(toggleBtn);
        li.Children.Add(new HtmlText { Text = " " });

        var titleSpan = new HtmlElement { Tag = "span", Attributes = new() { ["data-key"] = "title", ["data-event"] = "edit", ["data-id"] = item.Id.ToString(), ["style"] = "cursor:text" } };
        titleSpan.Children.Add(new HtmlText { Text = item.Title });
        li.Children.Add(titleSpan);

        li.Children.Add(new HtmlText { Text = " " });
        var priSpan = new HtmlElement { Tag = "span", Attributes = new() { ["data-key"] = "priority" } };
        priSpan.Children.Add(new HtmlText { Text = item.Priority switch { 3 => "🔴", 2 => "🟡", _ => "🟢" } });
        li.Children.Add(priSpan);

        foreach (var tag in item.Tags)
        {
            li.Children.Add(new HtmlText { Text = " " });
            var tagSpan = new HtmlElement { Tag = "span", Attributes = new() { ["class"] = "tag", ["data-event"] = "filtertag", ["data-tag"] = tag } };
            tagSpan.Children.Add(new HtmlText { Text = $"#{tag}" });
            li.Children.Add(tagSpan);
        }

        li.Children.Add(new HtmlText { Text = " " });
        var delBtn = new HtmlElement { Tag = "button", Attributes = new() { ["data-key"] = "delete", ["data-event"] = "delete", ["data-id"] = item.Id.ToString(), ["style"] = "color:red" } };
        delBtn.Children.Add(new HtmlText { Text = "×" });
        li.Children.Add(delBtn);

        // Assign IDs using a high offset to avoid collision with tree IDs
        int id = 1000000 + (int)(item.Id * 100);
        AssignIdsRecursive(li, ref id);
        return li;
    }

    private static void AssignIdsRecursive(HtmlNode node, ref int id)
    {
        node.Id = id++;
        if (node is HtmlElement el)
            foreach (var child in el.Children)
            { child.Parent = el; AssignIdsRecursive(child, ref id); }
    }

    /// <summary>Find a descendant element by traversing tag names (e.g., "div" → "ul").</summary>
    private static HtmlElement? FindByPath(HtmlElement? root, params string[] tags)
    {
        var current = root;
        foreach (var tag in tags)
        {
            current = current?.Children.OfType<HtmlElement>().FirstOrDefault(e => e.Tag == tag);
            if (current == null) return null;
        }
        return current;
    }

    /// <summary>Re-index data-key entries for a subtree (after replacing a node).</summary>
    private void ReindexSubtree(HtmlElement node)
    {
        if (node.Attributes.TryGetValue("data-key", out string? key) && !string.IsNullOrEmpty(key))
        {
            _keyIndex ??= new ConcurrentDictionary<string, HtmlElement>();
            _keyIndex[key] = node;
        }
        foreach (var child in node.Children.OfType<HtmlElement>())
            ReindexSubtree(child);
    }

    /// <summary>Generate the counter patch for the header (pending count).</summary>
    private string UpdateCounter()
    {
        var h1 = FindByPath(_lastTree, "h1");
        if (h1 == null) return "";
        var pending = State.Items.Count(t => !t.Completed);
        return PatchText(h1.Id, $"Todos ({pending} pending)");
    }
}
