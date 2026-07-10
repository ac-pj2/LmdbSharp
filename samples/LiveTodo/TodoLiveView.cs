using Lmdb.LiveView;
using Lmdb.Objects;
using System.Text.Json;

namespace LiveTodo;

public class TodoState
{
    public List<Todo> Items { get; set; } = new();
    public long? EditingId { get; set; } // null = not editing
    public string? EditingTitle { get; set; }
    public string? TagInput { get; set; }
    public string FilterTag { get; set; } = ""; // empty = show all
}

public class TodoLiveView : LiveView<TodoState>
{
    private readonly Collection<Todo> _todos;
    private readonly LiveViewHub _hub;

    public TodoLiveView(Collection<Todo> todos, LiveViewHub hub)
    {
        _todos = todos;
        _hub = hub;
    }

    public override string Mount()
    {
        ReloadState();
        return "";
    }

    public override string Render()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<div>");

        // Header with stats
        var visible = State.FilterTag == "" 
            ? State.Items 
            : State.Items.Where(t => t.Tags.Contains(State.FilterTag)).ToList();
        sb.Append($"<h1>Todos <small>({visible.Count(t => !t.Completed)} pending)</small></h1>");

        // Tag filter bar
        if (State.FilterTag != "")
            sb.Append($"<p>Filtering by: <b>{Esc(State.FilterTag)}</b> <button data-event=\"clearfilter\">clear</button></p>");

        // Add form
        sb.Append("""<form data-event="add"><input name="title" placeholder="Add a todo..." autofocus><select name="priority"><option value="1">Low</option><option value="2" selected>Medium</option><option value="3">High</option></select><button>+</button></form>""");

        // Todo list
        sb.Append("<ul>");
        foreach (var item in visible)
        {
            var cls = item.Completed ? "done" : "";
            var priorityLabel = item.Priority switch { 3 => "🔴", 2 => "🟡", _ => "🟢" };
            sb.Append($"<li data-key=\"{item.Id}\" class=\"{cls}\">");
            sb.Append($"<button data-key=\"toggle\" data-event=\"toggle\" data-id=\"{item.Id}\">{(item.Completed ? "☐" : "✓")}</button> ");

            // Title: either text or edit input
            if (State.EditingId == item.Id)
            {
                sb.Append($"<form data-key=\"title\" data-event=\"save\" style=\"display:inline\"><input name=\"title\" value=\"{Esc(State.EditingTitle ?? item.Title)}\" autofocus><button>💾</button></form>");
                sb.Append($" <button data-key=\"cancel\" data-event=\"cancel\">✕</button>");
            }
            else
            {
                sb.Append($"<span data-key=\"title\" data-event=\"edit\" data-id=\"{item.Id}\" style=\"cursor:text\">{Esc(item.Title)}</span>");
            }

            // Priority badge
            sb.Append($" <span data-key=\"priority\">{priorityLabel}</span>");

            // Tags
            foreach (var tag in item.Tags)
                sb.Append($" <span class=\"tag\" data-event=\"filtertag\" data-tag=\"{Esc(tag)}\">#{Esc(tag)}</span>");

            // Delete button
            sb.Append($" <button data-key=\"delete\" data-event=\"delete\" data-id=\"{item.Id}\" style=\"color:red\">×</button>");
            sb.Append("</li>");
        }
        sb.Append("</ul>");

        // Tag input for selected item (if editing)
        if (State.EditingId.HasValue)
        {
            var editing = State.Items.FirstOrDefault(t => t.Id == State.EditingId);
            if (editing != null)
            {
                sb.Append("<div style=\"margin-top:16px;padding:8px;background:#f5f5f5\">");
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
                var title = data?.GetProperty("title").GetString() ?? "";
                var priority = int.TryParse(data?.GetProperty("priority").GetString() ?? "2", out var p) ? p : 2;
                if (!string.IsNullOrWhiteSpace(title))
                {
                    using var txn = _todos.Database.BeginWrite();
                    _todos.Insert(txn, new Todo { Title = title, Priority = priority });
                    txn.Commit();
                    ReloadAndBroadcast();
                }
                break;

            case "toggle":
                if (long.TryParse(data?.GetProperty("id").GetString(), out var toggleId))
                {
                    using var txn = _todos.Database.BeginWrite();
                    var todo = _todos.Get(txn, toggleId);
                    if (todo != null)
                    {
                        todo.Completed = !todo.Completed;
                        _todos.Update(txn, todo);
                        txn.Commit();
                    }
                    ReloadAndBroadcast();
                }
                break;

            case "delete":
                if (long.TryParse(data?.GetProperty("id").GetString(), out var delId))
                {
                    using var txn = _todos.Database.BeginWrite();
                    _todos.Delete(txn, delId);
                    txn.Commit();
                    State.EditingId = null;
                    ReloadAndBroadcast();
                }
                break;

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
                var newTitle = data?.GetProperty("title").GetString() ?? "";
                if (State.EditingId.HasValue && !string.IsNullOrWhiteSpace(newTitle))
                {
                    using var txn = _todos.Database.BeginWrite();
                    var todo = _todos.Get(txn, State.EditingId.Value);
                    if (todo != null)
                    {
                        todo.Title = newTitle;
                        _todos.Update(txn, todo);
                        txn.Commit();
                    }
                }
                State.EditingId = null;
                ReloadAndBroadcast();
                break;

            case "cancel":
                State.EditingId = null;
                PushUpdate();
                break;

            case "addtag":
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
                    }
                    ReloadAndBroadcast();
                }
                break;

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

    private void ReloadState()
    {
        using var txn = _todos.Database.BeginRead();
        State.Items = _todos.Scan(txn).OrderBy(t => t.Id).ToList();
    }

    private void ReloadAndBroadcast()
    {
        ReloadState();
        _hub.BroadcastUpdate();
    }

    private static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);
}
