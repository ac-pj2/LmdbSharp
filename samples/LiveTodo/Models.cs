using MemoryPack;

namespace LiveTodo;

[MemoryPackable]
public partial class Todo
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public bool Completed { get; set; }
}
