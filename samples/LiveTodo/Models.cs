using MemoryPack;
using System.Collections.Generic;

namespace LiveTodo;

[MemoryPackable]
public partial class Todo
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public bool Completed { get; set; }
    public int Priority { get; set; } = 2; // 1=low, 2=medium, 3=high
    public List<string> Tags { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
