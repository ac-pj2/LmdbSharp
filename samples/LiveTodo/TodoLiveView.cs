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

    public override string Render()
    {
        // NO DB reads here. Pure in-memory state → HTML.
        var sb = new System.Text.StringBuilder();
        sb.Append("<div>");

        var visible = State.FilterTag == ""
            ? State.Items
            : State.Items.Where(t => t.Tags.Contains(State.FilterTag)).ToList();
        sb.Append($"<h1>Todos <small>({visible.Count(t => !t.Completed)} pending)</small></h1>");

        if (State.FilterTag != "")
            sb.Append($"<p>Filtering by: <b>{Esc(State.FilterTag)}</b> <button data-event=\"clearfilter\">clear</button></p>");

        sb.Append("""<form data-event="add"><input name="title" placeholder="Add a todo..." autofocus><select name="priority"><option value="1">Low</option><option value="2" selected>Medium</option><option value="3">High</option></select><button>+</button></form>""");

        sb.Append("<ul>");
        foreach (var item in visible)
        {
            var cls = item.Completed ? "done" : "";
            var priorityLabel = item.Priority switch { 3 => "🔴", 2 => "🟡", _ => "🟢" };
            sb.Append($"<li data-key=\"{item.Id}\" class=\"{cls}\">");
            sb.Append($"<button data-key=\"toggle\" data-event=\"toggle\" data-id=\"{item.Id}\">{(item.Completed ? "☐" : "✓")}</button> ");

            if (State.EditingId == item.Id)
            {
                sb.Append($"<form data-key=\"title\" data-event=\"save\" style=\"display:inline\"><input name=\"title\" value=\"{Esc(State.EditingTitle ?? item.Title)}\" autofocus><button>💾</button></form>");
                sb.Append($" <button data-key=\"cancel\" data-event=\"cancel\">✕</button>");
            }
            else
            {
                sb.Append($"<span data-key=\"title\" data-event=\"edit\" data-id=\"{item.Id}\" style=\"cursor:text\">{Esc(item.Title)}</span>");
            }

            sb.Append($" <span data-key=\"priority\">{priorityLabel}</span>");

            foreach (var tag in item.Tags)
                sb.Append($" <span class=\"tag\" data-event=\"filtertag\" data-tag=\"{Esc(tag)}\">#{Esc(tag)}</span>");

            sb.Append($" <button data-key=\"delete\" data-event=\"delete\" data-id=\"{item.Id}\" style=\"color:red\">×</button>");
            sb.Append("</li>");
        }
        sb.Append("</ul>");

        if (State.EditingId.HasValue)
        {
            var editing = State.Items.FirstOrDefault(t => t.Id == State.EditingId);
            if (editing != null)
            {
                sb.Append("<div data-key=\"tagpanel\" style=\"margin-top:16px;padding:8px;background:#f5f5f5\">");
                sb.Append($"<p>Add tag to: <b>{Esc(editing.Title)}</b></p>");
                sb.Append($"<form data-event=\"addtag\" data-id=\"{editing.Id}\"><input name=\"tag\" placeholder=\"tag name\"><button>Add Tag</button></form>");
                sb.Append("</div>");
            }
        }

        sb.Append("</div>");
        return sb.ToString();
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
                    // 1. Persist to DB
                    using var txn = _todos.Database.BeginWrite();
                    var todo = new Todo { Title = title, Priority = priority };
                    _todos.Insert(txn, todo);
                    txn.Commit();

                    // 2. Update in-memory state (no DB read!)
                    State.Items.Add(todo);

                    // 3. Push our own update (renders from memory)
                    PushUpdate();

                    // 4. Broadcast delta to other clients (they update memory, no DB read)
                    BroadcastDelta("add", todo);
                }
                break;
            }

            case "toggle":
            {
                if (long.TryParse(data?.GetProperty("id").GetString(), out var id))
                {
                    // 1. Persist
                    using var txn = _todos.Database.BeginWrite();
                    var todo = _todos.Get(txn, id);
                    if (todo != null)
                    {
                        todo.Completed = !todo.Completed;
                        _todos.Update(txn, todo);
                        txn.Commit();

                        // 2. Update memory
                        var local = State.Items.FirstOrDefault(t => t.Id == id);
                        if (local != null) local.Completed = todo.Completed;

                        // 3. Push + broadcast
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
                    // 1. Persist
                    using var txn = _todos.Database.BeginWrite();
                    _todos.Delete(txn, id);
                    txn.Commit();

                    // 2. Update memory
                    State.Items.RemoveAll(t => t.Id == id);
                    if (State.EditingId == id) State.EditingId = null;

                    // 3. Push + broadcast
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

                        // Update memory
                        var local = State.Items.FirstOrDefault(t => t.Id == id);
                        if (local != null) local.Title = newTitle;
                    }
                }
                State.EditingId = null;
                PushUpdate();
                BroadcastDelta("update", State.Items.FirstOrDefault(t => t.Id == State.EditingId));
                break;
            }

            case "cancel":
            {
                State.EditingId = null;
                PushUpdate(); // local only
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

                        // Update memory
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
                PushUpdate(); // local only — filter is per-client
                break;
            }

            case "clearfilter":
            {
                State.FilterTag = "";
                PushUpdate(); // local only
                break;
            }
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

    private static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);
}
