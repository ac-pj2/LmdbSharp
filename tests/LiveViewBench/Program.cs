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
        _lastTree = BuildTree(_state);
    }

    [GlobalCleanup]
    public void Cleanup() => _lastTree = null;

    // Rebuild baseline before each diff benchmark (BenchmarkDotNet calls this before each iteration)
    [IterationSetup]
    public void IterationSetup() => _lastTree = BuildTree(_state);

    /// <summary>Full render: build tree from scratch (initial load or full reload).</summary>
    [Benchmark]
    public HtmlElement FullRender()
    {
        return BuildTree();
    }

    /// <summary>Incremental change: toggle one item, re-render, diff.</summary>
    [Benchmark]
    public string ToggleOneItem()
    {
        // Clone state, toggle one item, build + diff
        var state = new List<TodoItem>(_state);
        state[ItemCount / 2] = new TodoItem { Id = state[ItemCount / 2].Id, Title = state[ItemCount / 2].Title, Completed = !state[ItemCount / 2].Completed, Priority = state[ItemCount / 2].Priority, Tags = state[ItemCount / 2].Tags };

        var newTree = BuildTree(state);
        return HtmlDiff.Diff(_lastTree, newTree);
    }

    /// <summary>Add an item, re-render, diff.</summary>
    [Benchmark]
    public string AddItem()
    {
        var state = new List<TodoItem>(_state)
        {
            new() { Id = ItemCount + 1000, Title = "New item", Priority = 2 }
        };

        var newTree = BuildTree(state);
        return HtmlDiff.Diff(_lastTree, newTree);
    }

    /// <summary>Delete an item, re-render, diff.</summary>
    [Benchmark]
    public string DeleteItem()
    {
        var state = new List<TodoItem>(_state);
        state.RemoveAt(ItemCount / 2);

        var newTree = BuildTree(state);
        return HtmlDiff.Diff(_lastTree, newTree);
    }

    private HtmlElement BuildTree() => BuildTree(_state);

    private static HtmlElement BuildTree(List<TodoItem> state)
    {
        var root = new HtmlElement { Tag = "div" };

        var h1 = new HtmlElement { Tag = "h1" };
        h1.Children.Add(new HtmlText { Text = $"Todos ({state.Count(t => !t.Completed)} pending)" });
        root.Children.Add(h1);

        var form = new HtmlElement { Tag = "form" };
        form.Attributes["data-event"] = "add";
        form.Children.Add(new HtmlElement { Tag = "input" });
        root.Children.Add(form);

        var ul = new HtmlElement { Tag = "ul" };
        foreach (var item in state)
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

            ul.Children.Add(li);
        }
        root.Children.Add(ul);

        // Assign IDs (needed for diffing)
        int id = 0;
        AssignIds(root, ref id);
        return root;
    }

    private static void AssignIds(HtmlNode node, ref int id)
    {
        node.Id = id++;
        if (node is HtmlElement el)
        {
            foreach (var child in el.Children)
            {
                child.Parent = el;
                AssignIds(child, ref id);
            }
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
