// LiveView render + diff benchmark. Measures the full pipeline:
// state → render tree → diff → patches, with varying list sizes.
//
// Run: dotnet run -c Release --project tests/LiveViewBench
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Lmdb.LiveView;
using System.Text;

BenchmarkRunner.Run<LiveViewBench>();

[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median")]
public class LiveViewBench
{
    private List<TodoItem> _state = null!;
    private HtmlElement? _lastTree;
    private List<HtmlElement> _lastListItems = null!;
    private int _nextId;

    [Params(10, 100, 1000, 5000)]
    public int ItemCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _state = new List<TodoItem>(ItemCount);
        for (int i = 0; i < ItemCount; i++)
        {
            _state.Add(new TodoItem
            {
                Id = i + 1,
                Title = $"Task number {i}",
                Completed = i % 3 == 0,
                Priority = (i % 3) + 1,
                Tags = i % 5 == 0 ? new List<string> { "work", "urgent" } : new List<string>(),
            });
        }
        IterationSetup();
    }

    [GlobalCleanup]
    public void Cleanup() => _lastTree = null;

    // Rebuild baseline before each diff benchmark (BenchmarkDotNet calls this before each iteration)
    [IterationSetup]
    public void IterationSetup()
    {
        _nextId = 0;
        _lastTree = BuildTree(_state);
        HtmlDiff.AssignIds(_lastTree, ref _nextId);
        _lastListItems = ((HtmlElement)_lastTree.Children[^1]).Children.Cast<HtmlElement>().ToList();
    }

    /// <summary>Full render: build tree from scratch (initial load or full reload).</summary>
    [Benchmark]
    public HtmlElement FullRender()
    {
        return BuildTree(_state);
    }

    /// <summary>Incremental change: toggle one item, re-render, diff.</summary>
    [Benchmark]
    public byte[]? ToggleOneItem()
    {
        var state = new List<TodoItem>(_state);
        state[ItemCount / 2] = new TodoItem { Id = state[ItemCount / 2].Id, Title = state[ItemCount / 2].Title, Completed = !state[ItemCount / 2].Completed, Priority = state[ItemCount / 2].Priority, Tags = state[ItemCount / 2].Tags };

        var newTree = BuildTree(state);
        int nextId = _nextId;
        return HtmlDiff.Diff(_lastTree, newTree, ref nextId);
    }

    /// <summary>Toggle one item with memoized rows: only the changed row is rebuilt;
    /// all other rows are the same instance and skipped by reference in the diff.</summary>
    [Benchmark]
    public byte[]? ToggleOneItemMemoized()
    {
        int toggleIdx = ItemCount / 2;
        var item = _state[toggleIdx];
        var changed = new TodoItem { Id = item.Id, Title = item.Title, Completed = !item.Completed, Priority = item.Priority, Tags = item.Tags };

        // Rebuild the page chrome + reuse unchanged row instances (what Memo does).
        var newTree = BuildShell(_state.Count(t => !t.Completed) + (changed.Completed ? -1 : 1));
        var ul = new HtmlElement { Tag = "ul" };
        for (int i = 0; i < _lastListItems.Count; i++)
            ul.Children.Add(i == toggleIdx ? BuildListItem(changed) : _lastListItems[i]);
        newTree.Children.Add(ul);

        int nextId = _nextId;
        return HtmlDiff.Diff(_lastTree, newTree, ref nextId);
    }

    /// <summary>Add an item, re-render, diff.</summary>
    [Benchmark]
    public byte[]? AddItem()
    {
        var state = new List<TodoItem>(_state)
        {
            new() { Id = ItemCount + 1000, Title = "New item", Priority = 2 }
        };

        var newTree = BuildTree(state);
        int nextId = _nextId;
        return HtmlDiff.Diff(_lastTree, newTree, ref nextId);
    }

    /// <summary>Delete an item, re-render, diff.</summary>
    [Benchmark]
    public byte[]? DeleteItem()
    {
        var state = new List<TodoItem>(_state);
        state.RemoveAt(ItemCount / 2);

        var newTree = BuildTree(state);
        int nextId = _nextId;
        return HtmlDiff.Diff(_lastTree, newTree, ref nextId);
    }

    private static HtmlElement BuildTree(List<TodoItem> state)
    {
        var root = BuildShell(state.Count(t => !t.Completed));

        var ul = new HtmlElement { Tag = "ul" };
        foreach (var item in state)
            ul.Children.Add(BuildListItem(item));
        root.Children.Add(ul);
        return root;
    }

    private static HtmlElement BuildShell(int pendingCount)
    {
        var root = new HtmlElement { Tag = "div" };

        var h1 = new HtmlElement { Tag = "h1" };
        h1.Children.Add(new HtmlText { Text = $"Todos ({pendingCount} pending)" });
        root.Children.Add(h1);

        var form = new HtmlElement { Tag = "form" };
        form.Attributes["data-event"] = "add";
        form.Children.Add(new HtmlElement { Tag = "input" });
        root.Children.Add(form);
        return root;
    }

    private static HtmlElement BuildListItem(TodoItem item)
    {
        {
            var li = new HtmlElement { Tag = "li" };
            li.Attributes["data-key"] = item.Id.ToString();
            li.Attributes["class"] = item.Completed ? "done" : "";

            var toggle = new HtmlElement { Tag = "button" };
            toggle.Attributes["data-key"] = "toggle";
            toggle.Attributes["data-event"] = "toggle";
            toggle.Attributes["data-id"] = item.Id.ToString();
            toggle.Children.Add(new HtmlText { Text = item.Completed ? "☐" : "✓" });
            li.Children.Add(toggle);

            var title = new HtmlElement { Tag = "span" };
            title.Attributes["data-key"] = "title";
            title.Children.Add(new HtmlText { Text = item.Title });
            li.Children.Add(title);

            var pri = new HtmlElement { Tag = "span" };
            pri.Attributes["data-key"] = "priority";
            pri.Children.Add(new HtmlText { Text = item.Priority switch { 3 => "🔴", 2 => "🟡", _ => "🟢" } });
            li.Children.Add(pri);

            foreach (var tag in item.Tags)
            {
                var tagSpan = new HtmlElement { Tag = "span" };
                tagSpan.Attributes["class"] = "tag";
                tagSpan.Attributes["data-tag"] = tag;
                tagSpan.Children.Add(new HtmlText { Text = $"#{tag}" });
                li.Children.Add(tagSpan);
            }

            var del = new HtmlElement { Tag = "button" };
            del.Attributes["data-key"] = "delete";
            del.Attributes["data-event"] = "delete";
            del.Attributes["data-id"] = item.Id.ToString();
            del.Children.Add(new HtmlText { Text = "×" });
            li.Children.Add(del);

            return li;
        }
    }
}

public class TodoItem
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public bool Completed { get; set; }
    public int Priority { get; set; }
    public List<string> Tags { get; set; } = new();
}

public record TodoItemRecord(long Id, string Title, bool Completed, int Priority, List<string> Tags);
