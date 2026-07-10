using Lmdb.LiveView;
using Lmdb.Objects;
using System.Text.Json;

namespace LiveTodo;

/// <summary>A collaborative real-time todo list. All clients see updates instantly
/// because every change triggers a re-render + diff broadcast.</summary>
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
        // Load initial state.
        using var txn = _todos.Database.BeginRead();
        State.Items = _todos.Scan(txn).OrderBy(t => t.Id).ToList();
        return "";
    }

    public override string Render()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<div>");
        sb.Append("<h1>Collaborative Todos</h1>");
        sb.Append($"<p>{State.Items.Count(i => !i.Completed)} pending, {State.Items.Count(i => i.Completed)} done</p>");

        // Add form.
        sb.Append("""<form data-event="add"><input name="title" placeholder="What needs doing?" autofocus><button>Add</button></form>""");

        // Todo list.
        sb.Append("<ul>");
        foreach (var item in State.Items)
        {
            var cls = item.Completed ? "done" : "";
            var label = item.Completed ? "☐" : "✓";
            sb.Append($"<li class=\"{cls}\">");
            sb.Append($"<button data-event=\"toggle\" data-id=\"{item.Id}\">{label}</button> ");
            sb.Append($"<span>{System.Net.WebUtility.HtmlEncode(item.Title)}</span> ");
            sb.Append($"<button data-event=\"delete\" data-id=\"{item.Id}\" style=\"color:red\">×</button>");
            sb.Append("</li>");
        }
        sb.Append("</ul>");
        sb.Append("</div>");
        return sb.ToString();
    }

    public override void HandleEvent(string name, JsonElement? data)
    {
        switch (name)
        {
            case "add":
                var title = data?.GetProperty("title").GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(title))
                {
                    using var txn = _todos.Database.BeginWrite();
                    _todos.Insert(txn, new Todo { Title = title });
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
                    ReloadAndBroadcast();
                }
                break;
        }
    }

    private void ReloadAndBroadcast()
    {
        // Update this view's state.
        using var txn = _todos.Database.BeginRead();
        State.Items = _todos.Scan(txn).OrderBy(t => t.Id).ToList();
        // Broadcast to ALL connected clients (collaborative updates).
        _hub.BroadcastUpdate();
    }
}

public class TodoState
{
    public List<Todo> Items { get; set; } = new();
}
