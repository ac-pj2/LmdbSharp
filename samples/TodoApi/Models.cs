using MemoryPack;

namespace TodoApi;

[MemoryPackable]
public partial class Todo
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public bool Completed { get; set; }
    public int Priority { get; set; } // 1=low, 2=medium, 3=high
    public DateTime DueDate { get; set; }
    public string Category { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
