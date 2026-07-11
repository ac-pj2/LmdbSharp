// FleetSimulator: the "outside world". A background loop that mutates node
// metrics, persists every tick to LMDB in one batch write transaction, raises
// incidents when nodes go critical, and pushes the changes to every connected
// LiveView session via hub.Broadcast — no session re-reads the database.
using Lmdb.LiveView;
using Lmdb.Objects;

namespace MissionControl;

public sealed class FleetSimulator
{
    private static readonly string[] Regions = { "eu-west", "us-east", "us-west", "ap-south" };
    private static readonly string[] IncidentMessages =
    {
        "CPU saturation — request queue growing",
        "memory pressure — GC thrashing",
        "elevated p99 latency",
        "health checks failing",
    };

    private readonly Collection<FleetNode> _nodes;
    private readonly Collection<Incident> _incidents;
    private readonly Random _rng = new(1234);
    private readonly object _gate = new();
    private Timer? _timer;

    public LiveViewHub? Hub { get; set; }

    // Shared, read by every view when rendering.
    public volatile bool Paused;
    public long Ticks;
    public long DbWrites;
    public readonly DateTime StartedAt = DateTime.UtcNow;

    public FleetSimulator(Collection<FleetNode> nodes, Collection<Incident> incidents)
    {
        _nodes = nodes;
        _incidents = incidents;
        SeedIfEmpty();
    }

    public void Start(TimeSpan interval) => _timer = new Timer(_ => Tick(), null, interval, interval);

    private void SeedIfEmpty()
    {
        using (var read = _nodes.Database.BeginRead())
        {
            if (_nodes.Scan(read).Any()) return;
        }

        using var txn = _nodes.Database.BeginWrite();
        for (int i = 0; i < 200; i++)
        {
            var region = Regions[i % Regions.Length];
            _nodes.Insert(txn, new FleetNode
            {
                Name = $"{region}-node-{i / Regions.Length:d3}",
                Region = region,
                Cpu = _rng.Next(10, 60),
                Mem = _rng.Next(20, 70),
                Reqs = _rng.Next(50, 900),
            });
        }
        txn.Commit();
    }

    public (List<FleetNode> Nodes, List<Incident> Incidents) LoadAll()
    {
        using var txn = _nodes.Database.BeginRead();
        return (_nodes.Scan(txn).OrderBy(n => n.Id).ToList(),
                _incidents.Scan(txn).OrderByDescending(i => i.Id).Take(50).ToList());
    }

    private void Tick()
    {
        if (Paused || Hub == null) return;
        lock (_gate)
        {
            List<FleetNode> changed;
            var newIncidents = new List<Incident>();

            using (var txn = _nodes.Database.BeginWrite())
            {
                var all = _nodes.Scan(txn).ToList();

                // Drift 8–20 random nodes per tick.
                changed = all.OrderBy(_ => _rng.Next()).Take(_rng.Next(8, 21)).ToList();
                foreach (var n in changed)
                {
                    n.Cpu = Drift(n.Cpu, 12, 2, 100);
                    n.Mem = Drift(n.Mem, 6, 5, 100);
                    n.Reqs = Drift(n.Reqs, 120, 0, 2500);
                    var prev = n.Status;
                    n.Status = n.Cpu > 92 || n.Mem > 95 ? "critical" : n.Cpu > 75 ? "warn" : "ok";
                    _nodes.Update(txn, n);
                    DbWrites++;

                    if (n.Status == "critical" && prev != "critical")
                    {
                        var inc = new Incident
                        {
                            NodeId = n.Id,
                            NodeName = n.Name,
                            Message = IncidentMessages[_rng.Next(IncidentMessages.Length)],
                            CreatedAt = DateTime.UtcNow,
                        };
                        _incidents.Insert(txn, inc);
                        DbWrites++;
                        newIncidents.Add(inc);
                    }
                }
                txn.Commit();
            }

            Ticks++;

            // One delta for everyone: the changed nodes and any new incidents.
            // Sessions apply it to in-memory state — zero DB reads.
            Hub.Broadcast("tick", new
            {
                nodes = changed,
                incidents = newIncidents,
            });
        }
    }

    /// <summary>Spike a random node to critical and raise an incident — for
    /// demos (and tests) that don't want to wait for the random walk.</summary>
    public void TriggerChaos()
    {
        if (Hub == null) return;
        lock (_gate)
        {
            FleetNode node;
            Incident inc;
            using (var txn = _nodes.Database.BeginWrite())
            {
                var all = _nodes.Scan(txn).ToList();
                node = all[_rng.Next(all.Count)];
                node.Cpu = _rng.Next(93, 100);
                node.Status = "critical";
                _nodes.Update(txn, node);
                inc = new Incident
                {
                    NodeId = node.Id,
                    NodeName = node.Name,
                    Message = "manual chaos injection",
                    CreatedAt = DateTime.UtcNow,
                };
                _incidents.Insert(txn, inc);
                DbWrites += 2;
                txn.Commit();
            }
            Hub.Broadcast("tick", new { nodes = new[] { node }, incidents = new[] { inc } });
        }
    }

    private int Drift(int value, int step, int min, int max)
        => Math.Clamp(value + _rng.Next(-step, step + 1), min, max);
}
