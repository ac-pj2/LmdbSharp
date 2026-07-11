using MemoryPack;

namespace MissionControl;

[MemoryPackable]
public partial class FleetNode
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Region { get; set; } = "";
    public int Cpu { get; set; }        // 0..100
    public int Mem { get; set; }        // 0..100
    public int Reqs { get; set; }       // requests/sec
    public string Status { get; set; } = "ok";   // ok | warn | critical
}

/// <summary>Broadcast payload for a simulator tick. Delivered by reference to
/// every session (no serialization) — immutable by convention.</summary>
public sealed record TickDelta(IReadOnlyList<FleetNode> Nodes, IReadOnlyList<Incident> Incidents);

[MemoryPackable]
public partial class Incident
{
    public long Id { get; set; }
    public long NodeId { get; set; }
    public string NodeName { get; set; } = "";
    public string Message { get; set; } = "";
    public string State { get; set; } = "open";  // open | acked | resolved
    public string AckedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
