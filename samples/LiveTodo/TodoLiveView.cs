using Lmdb.LiveView;
using Lmdb.Objects;
using System.Text.Json;

namespace LiveTodo;

public class TodoState
{
    public List<Todo> Items { get; set; } = new();
    public long? EditingId { get; set; }
    public string? EditingTitle { get; set; }
    public string FilterTag { get; set; } = "";
}

/// <summary>
/// Robust LiveView design: always re-render + diff. No manual tree manipulation.
///
/// Data flow:
///   User event → update in-memory State → persist to DB → PushUpdate (re-render + diff)
///   Broadcast delta → other clients ApplyDelta to State → framework re-renders
///
/// PushUpdate builds a fresh tree from State via RenderTree(), diffs it against
/// the last tree, and sends only the changed patches. List items are memoized
/// (Memo below): unchanged rows return the same tree instance, which the differ
/// skips by reference — so a change to one row costs O(1), not O(list).
/// </summary>
public class TodoLiveView : DeltaLiveView<TodoState>
{
    private readonly Collection<Todo> _todos;

    public TodoLiveView(Collection<Todo> todos) => _todos = todos;

    public override void Mount()
    {
        using var txn = _todos.Database.BeginRead();
        State.Items = _todos.Scan(txn).OrderBy(t => t.Id).ToList();
    }

    /// <summary>Build the DOM tree directly — skips HTML string generation + re-parsing.</summary>
    public override HtmlElement RenderTree()
    {
        var visible = State.FilterTag == ""
            ? State.Items
            : State.Items.Where(t => t.Tags.Contains(State.FilterTag)).ToList();

        var root = H.Div(
            // Header, with a help toggle handled entirely client-side (data-client:
            // no WebSocket message, no server render — instant show/hide).
            H.Div(
                H.H1($"Todos ({visible.Count(t => !t.Completed)} pending)"),
                H.Button("?").Cls("help-btn").Client("toggle #help with fade")
                    .Attr("type", "button").Attr("aria-label", "Help")
            ).Cls("header"),
            H.Div(H.P("Click ✓ to toggle, the title to edit, × to delete, a #tag to filter. " +
                      "Changes sync live to everyone connected."))
                .Id("help").Hidden()
        );

        if (State.FilterTag != "")
            root.Add(H.P(
                "Filtering by: ", H.B(State.FilterTag), " ",
                H.Button("clear").On("clearfilter")));

        root.Add(H.Form(
            H.Input().Attr("name", "title").Attr("placeholder", "Add a todo..."),
            H.Select(
                H.Option("Low").Attr("value", "1"),
                H.Option("Medium").Attr("value", "2").Attr("selected", ""),
                H.Option("High").Attr("value", "3")
            ).Attr("name", "priority"),
            H.Button("+")
        ).On("add"));

        // Rows are memoized: if a row's version tuple is unchanged since the
        // last render, the same node instance is reused and the differ skips it.
        root.Add(H.Ul().AddRange(visible.Select(item =>
        {
            bool editing = State.EditingId == item.Id;
            var version = (item.Title, item.Completed, item.Priority,
                string.Join(",", item.Tags), editing, editing ? State.EditingTitle : null);
            return (HtmlNode)Memo(item.Id, version, () => BuildListItem(item));
        })));

        // Built-in observability drawer (server stats + client wire stats).
        root.Add(DevPanel.Render(this));
        return root;
    }

    private HtmlElement BuildListItem(Todo item)
    {
        var li = H.Li(
            H.Button(item.Completed ? "☐" : "✓").On("toggle", item.Id).Key("toggle"),
            " "
        ).Cls(item.Completed ? "done" : "").Key(item.Id);

        if (State.EditingId == item.Id)
        {
            li.Add(H.Form(
                    H.Input().Attr("name", "title").Attr("value", State.EditingTitle ?? item.Title),
                    H.Button("💾")
                ).On("save").Key("title").Attr("style", "display:inline"));
            li.Add(" ");
            li.Add(H.Button("✕").On("cancel").Key("cancel"));
        }
        else
        {
            li.Add(H.Span(item.Title).On("edit", item.Id).Key("title").Attr("style", "cursor:text"));
        }

        li.Add(" ");
        li.Add(H.Span(item.Priority switch { 3 => "🔴", 2 => "🟡", _ => "🟢" }).Key("priority"));

        foreach (var tag in item.Tags)
        {
            li.Add(" ");
            li.Add(H.Span($"#{tag}").Cls("tag").On("filtertag").Attr("data-tag", tag));
        }

        li.Add(" ");
        li.Add(H.Button("×").On("delete", item.Id).Key("delete").Attr("style", "color:red"));
        return li;
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

                        // Replace, don't mutate — delta payloads are shared instances.
                        var idx = State.Items.FindIndex(t => t.Id == id);
                        if (idx >= 0) State.Items[idx] = todo;

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
                    BroadcastDelta("delete", id);
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
                        var idx = State.Items.FindIndex(t => t.Id == id);
                        if (idx >= 0) State.Items[idx] = todo;
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
                        var idx = State.Items.FindIndex(t => t.Id == tagId);
                        if (idx >= 0) State.Items[idx] = todo;
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

    /// <summary>Apply a delta from another client. Updates in-memory state — NO DB
    /// READ, no serialization: payloads are the actual objects (shared and
    /// immutable). Idempotent: deltas can race the mount's DB read.</summary>
    public override void ApplyDelta(LiveDelta delta)
    {
        switch (delta.Type)
        {
            case "reload":
            {
                using var txn = _todos.Database.BeginRead();
                State.Items = _todos.Scan(txn).OrderBy(t => t.Id).ToList();
                break;
            }
            case "add" when delta.Data is Todo added:
                if (State.Items.All(t => t.Id != added.Id))
                    State.Items.Add(added);
                break;
            case "update" when delta.Data is Todo updated:
            {
                var idx = State.Items.FindIndex(t => t.Id == updated.Id);
                if (idx >= 0) State.Items[idx] = updated;
                break;
            }
            case "delete" when delta.Data is long deletedId:
                State.Items.RemoveAll(t => t.Id == deletedId);
                break;
        }
    }
}
