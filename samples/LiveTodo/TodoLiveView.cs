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

public record TodoDelta(string Type, Todo? Item = null, long? DeletedId = null);

/// <summary>
/// Robust LiveView design: always re-render + diff. No manual tree manipulation.
///
/// Data flow:
///   User event → update in-memory State → persist to DB → PushUpdate (re-render + diff)
///   Broadcast delta → other clients ApplyDelta to State → PushUpdate (re-render + diff)
///
/// PushUpdate builds a fresh tree from State via RenderTree(), diffs it against
/// the last tree, and sends only the changed patches. List items are memoized
/// (Memo below): unchanged rows return the same tree instance, which the differ
/// skips by reference — so a change to one row costs O(1), not O(list).
///
/// The diff engine is the single source of truth for what changed — just
/// re-render and let the diff find the changes.
/// </summary>
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
        using var txn = _todos.Database.BeginRead();
        State.Items = _todos.Scan(txn).OrderBy(t => t.Id).ToList();
    }

    /// <summary>Build the DOM tree directly — skips HTML string generation + re-parsing.</summary>
    public override HtmlElement RenderTree()
    {
        var root = new HtmlElement { Tag = "div" };

        var visible = State.FilterTag == ""
            ? State.Items
            : State.Items.Where(t => t.Tags.Contains(State.FilterTag)).ToList();

        // Header, with a help toggle handled entirely client-side (data-client:
        // no WebSocket message, no server render — instant show/hide).
        var header = new HtmlElement { Tag = "div", Attributes = new() { ["class"] = "header" } };
        var h1 = new HtmlElement { Tag = "h1" };
        h1.Children.Add(new HtmlText { Text = $"Todos ({visible.Count(t => !t.Completed)} pending)" });
        header.Children.Add(h1);
        var helpBtn = new HtmlElement { Tag = "button", Attributes = new()
        { ["type"] = "button", ["class"] = "help-btn", ["data-client"] = "toggle #help with fade", ["aria-label"] = "Help" } };
        helpBtn.Children.Add(new HtmlText { Text = "?" });
        header.Children.Add(helpBtn);
        root.Children.Add(header);

        var help = new HtmlElement { Tag = "div", Attributes = new() { ["id"] = "help", ["hidden"] = "" } };
        var helpText = new HtmlElement { Tag = "p" };
        helpText.Children.Add(new HtmlText
        { Text = "Click ✓ to toggle, the title to edit, × to delete, a #tag to filter. Changes sync live to everyone connected." });
        help.Children.Add(helpText);
        root.Children.Add(help);

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
        form.Children.Add(new HtmlElement { Tag = "input", Attributes = new() { ["name"] = "title", ["placeholder"] = "Add a todo..." } });
        var select = new HtmlElement { Tag = "select", Attributes = new() { ["name"] = "priority" } };
        select.Children.Add(MakeOption("1", "Low", false));
        select.Children.Add(MakeOption("2", "Medium", true));
        select.Children.Add(MakeOption("3", "High", false));
        form.Children.Add(select);
        var addBtn = new HtmlElement { Tag = "button" };
        addBtn.Children.Add(new HtmlText { Text = "+" });
        form.Children.Add(addBtn);
        root.Children.Add(form);

        // List. Rows are memoized: if a row's version tuple is unchanged since the
        // last render, the same node instance is reused and the differ skips it.
        var ul = new HtmlElement { Tag = "ul" };
        foreach (var item in visible)
        {
            bool editing = State.EditingId == item.Id;
            var version = (item.Title, item.Completed, item.Priority,
                string.Join(",", item.Tags), editing, editing ? State.EditingTitle : null);
            ul.Children.Add(Memo(item.Id, version, () => BuildListItem(item)));
        }
        root.Children.Add(ul);

        return root;
    }

    private HtmlElement BuildListItem(Todo item)
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

        return li;
    }

    private static HtmlElement MakeOption(string value, string label, bool selected)
    {
        var opt = new HtmlElement { Tag = "option", Attributes = new() { ["value"] = value } };
        if (selected) opt.Attributes["selected"] = "";
        opt.Children.Add(new HtmlText { Text = label });
        return opt;
    }

    // ── Event handling ──

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
                    PushUpdate();
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

                        PushUpdate();
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

                    PushUpdate();
                    BroadcastDelta("delete", new TodoDelta("delete", DeletedId: id));
                }
                break;
            }

            case "edit":
            {
                if (long.TryParse(data?.GetProperty("id").GetString(), out var id))
                {
                    var todo = State.Items.FirstOrDefault(t => t.Id == id);
                    if (todo != null)
                    {
                        State.EditingId = id;
                        State.EditingTitle = todo.Title;
                        PushUpdate(); // local only — editing state is per-client
                    }
                }
                break;
            }

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
                {
                    var updated = State.Items.FirstOrDefault(t => t.Id == savedId.Value);
                    if (updated != null) BroadcastDelta("update", updated);
                }
                break;
            }

            case "cancel":
            {
                State.EditingId = null;
                PushUpdate();
                break;
            }

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
            {
                State.FilterTag = data?.GetProperty("tag").GetString() ?? "";
                PushUpdate(); // local only
                break;
            }

            case "clearfilter":
            {
                State.FilterTag = "";
                PushUpdate();
                break;
            }
        }
    }

    /// <summary>Apply a delta from another client. Updates in-memory state — NO DB READ.</summary>
    public override void ApplyDelta(LiveDelta delta)
    {
        if (delta.Type == "reload")
        {
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

    /// <summary>Receive a delta from another client: apply to state, then re-render + diff.</summary>
    protected internal override void ReceiveDelta(LiveDelta delta)
    {
        ApplyDelta(delta);
        PushUpdate(); // always re-render + diff — the diff engine finds what changed
    }
}
