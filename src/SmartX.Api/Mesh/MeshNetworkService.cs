using SmartX.Api.Services;
using SmartX.Shared.Mesh;
using SmartX.Shared.Models;

namespace SmartX.Api.Mesh;

/// <summary>
/// Part 3 — the live mesh: graph, down-node tracking, spanning tree and self-healing.
/// <para>
/// A node is <b>down</b> when an operator takes it offline (simulated hardware failure) or when the Part 1 heartbeat
/// sees it miss three publish intervals. Whenever the set of down nodes changes, the minimum spanning tree is rebuilt
/// over the nodes still powered. Every sensor whose uplink changed is recorded in a <see cref="HealingEvent"/>, which
/// is the "network heals itself by finding a new path" behaviour from the brief. Sensors with no powered path left
/// are reported as isolated, with advice on where they could hop.
/// </para>
/// All public members are thread-safe (one lock; each rebuild takes well under a millisecond for a few hundred nodes).
/// </summary>
public sealed partial class MeshNetworkService(GatewayState gateway, ILogger<MeshNetworkService> logger)
{
    public const int MaxHealingEvents = 30;

    /// <summary>Everything derived from one spanning-tree build.</summary>
    private sealed class MeshState
    {
        public required MeshGraph Graph { get; init; }
        public required bool[] Down { get; init; }
        public required MeshNodeStatus[] Status { get; init; }
        public required SpanningTree Tree { get; init; }
        public required int[] Hops { get; init; }
        public required int[] Downstream { get; init; }
        public required ShortestPaths Shortest { get; init; }
        public required bool[] Critical { get; init; }
        public required int Components { get; init; }
        public required long Version { get; init; }
        public required double ShortestTreeWeight { get; init; }
        public required double TreeRoutesSum { get; init; }
        public required double ShortestRoutesSum { get; init; }

        public bool IsUp(MeshVertex v) => !Down[v.Index];
        public MeshVertex? Uplink(MeshVertex v) => Tree.ParentEdge[v.Index]?.Other(v);
    }

    private readonly object _lock = new();
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _silent = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<HealingEvent> _healing = new();
    private MeshGraph? _graph;
    private uint _graphSignature;
    private MeshState? _state;
    private MeshState? _previous;
    private long _version;

    /// <summary>Treat nodes the Part 1 heartbeat considers silent as down. On by default.</summary>
    public bool UseHeartbeat { get; set; } = true;

    /// <summary>Silent after this many missed publish intervals (same rule as the Part 1 Pulse Board).</summary>
    public double SilentAfterIntervals { get; set; } = 3;

    // ================================================================== refresh

    /// <summary>Re-reads the sensor registry and heartbeat; rebuilds the tree when anything changed. Called by <see cref="MeshMonitor"/>.</summary>
    public HealingEvent? Refresh()
    {
        lock (_lock) return RefreshUnsafe(null);
    }

    private HealingEvent? RefreshUnsafe(string? trigger)
    {
        var rebuilt = EnsureGraphUnsafe();
        var graph = _graph!;
        var heartbeatTrigger = UseHeartbeat ? SyncHeartbeatUnsafe(graph) : ClearHeartbeatUnsafe();

        if (_state is null || rebuilt)
        {
            _state = Compute(graph, SpanningTreeAlgorithm.Prim, ++_version);
            _previous = null;
            return null;
        }

        var reason = string.Join("; ", new[] { trigger, heartbeatTrigger }.Where(t => !string.IsNullOrEmpty(t)));
        if (reason.Length == 0) return null;

        var next = Compute(graph, SpanningTreeAlgorithm.Prim, ++_version);
        var healing = Diff(_state, next, reason);
        _previous = _state;
        _state = next;

        _healing.AddFirst(healing);
        while (_healing.Count > MaxHealingEvents) _healing.RemoveLast();
        if (healing.Rerouted > 0 || healing.NewlyIsolated.Count > 0)
            logger.LogInformation("Mesh healed: {Trigger} → {Rerouted} rerouted, {Isolated} isolated.", reason, healing.Rerouted, healing.NewlyIsolated.Count);
        return healing;
    }

    /// <summary>Rebuilds the graph when sensors were registered or deleted in Part 1. Returns true on rebuild.</summary>
    private bool EnsureGraphUnsafe()
    {
        var signature = (uint)gateway.Sensors.Count;
        foreach (var mac in gateway.Sensors.Keys) signature ^= MeshTopologyBuilder.StableHash(mac);
        if (_graph is not null && signature == _graphSignature) return false;

        _graph = MeshTopologyBuilder.Build(gateway.Sensors.Values);
        _graphSignature = signature;
        _failed.RemoveWhere(mac => _graph.Find(mac) is null);
        logger.LogInformation("Mesh graph built: {Vertices} vertices, {Edges} links.", _graph.Vertices.Count, _graph.Edges.Count);
        return true;
    }

    /// <summary>Compares the heartbeat with the last known silent set; returns a description of what changed, or null.</summary>
    private string? SyncHeartbeatUnsafe(MeshGraph graph)
    {
        var now = DateTimeOffset.UtcNow;
        var silent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in gateway.Channels.Values)
        {
            if (channel.LastSeen is { } seen &&
                (now - seen).TotalSeconds > channel.Profile.PublishIntervalSeconds * SilentAfterIntervals &&
                graph.Find(channel.Profile.MacAddress) is not null)
                silent.Add(channel.Profile.MacAddress);
        }
        if (silent.SetEquals(_silent)) return null;

        var wentSilent = silent.Except(_silent, StringComparer.OrdinalIgnoreCase).ToList();
        var cameBack = _silent.Except(silent, StringComparer.OrdinalIgnoreCase).ToList();
        _silent = silent;

        var parts = new List<string>();
        if (wentSilent.Count > 0) parts.Add($"{Describe(graph, wentSilent)} went silent (Part 1 heartbeat)");
        if (cameBack.Count > 0) parts.Add($"{Describe(graph, cameBack)} back online");
        return string.Join("; ", parts);
    }

    private string? ClearHeartbeatUnsafe()
    {
        if (_silent.Count == 0) return null;
        _silent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return "Heartbeat failures ignored";
    }

    private static string Describe(MeshGraph graph, List<string> macs)
    {
        var names = macs.Take(2).Select(m => graph.Find(m)?.Name ?? m).ToList();
        return macs.Count > 2 ? $"{string.Join(", ", names)} and {macs.Count - 2} more" : string.Join(" and ", names);
    }

    // ================================================================== compute

    private MeshState Compute(MeshGraph graph, SpanningTreeAlgorithm algorithm, long version)
    {
        var n = graph.Vertices.Count;
        var down = new bool[n];
        foreach (var v in graph.Vertices)
        {
            if (v.Kind == MeshNodeKind.Gateway) continue;               // the gateway is the root; it never goes down
            down[v.Index] = _failed.Contains(v.MacAddress) || (v.Kind == MeshNodeKind.Sensor && _silent.Contains(v.MacAddress));
        }
        bool IsUp(MeshVertex v) => !down[v.Index];

        var tree = SpanningTrees.Build(algorithm, graph, graph.Gateway, IsUp);

        // Orient the tree: hop depth by BFS from the gateway over tree edges, then subtree sizes bottom-up.
        var children = new List<MeshVertex>?[n];
        foreach (var v in graph.Vertices)
            if (tree.ParentEdge[v.Index] is { } up)
                (children[up.Other(v).Index] ??= new()).Add(v);

        var hops = new int[n];
        Array.Fill(hops, -1);
        var order = new List<MeshVertex>(n);
        var queue = new Queue<MeshVertex>();
        if (IsUp(graph.Gateway)) { hops[graph.Gateway.Index] = 0; queue.Enqueue(graph.Gateway); }
        while (queue.TryDequeue(out var v))
        {
            order.Add(v);
            if (children[v.Index] is not { } kids) continue;
            foreach (var c in kids) { hops[c.Index] = hops[v.Index] + 1; queue.Enqueue(c); }
        }

        var downstream = new int[n];
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var v = order[i];
            if (tree.ParentEdge[v.Index] is { } up) downstream[up.Other(v).Index] += downstream[v.Index] + 1;
        }

        var status = new MeshNodeStatus[n];
        foreach (var v in graph.Vertices)
        {
            status[v.Index] = down[v.Index]
                ? (_failed.Contains(v.MacAddress) ? MeshNodeStatus.Failed : MeshNodeStatus.Silent)
                : tree.Reached[v.Index] ? MeshNodeStatus.Online : MeshNodeStatus.Isolated;
        }

        // Energy per reporting round, with and without in-network aggregation (see MeshStats).
        var shortest = SpanningTrees.Dijkstra(graph, graph.Gateway, IsUp);
        var routeCost = new double[n];
        double treeRoutes = 0, shortestRoutes = 0, shortestTree = 0;
        foreach (var v in order)                                   // BFS order: parents before children
        {
            if (tree.ParentEdge[v.Index] is not { } up) continue;
            routeCost[v.Index] = routeCost[up.Other(v).Index] + up.EnergyMicroJoules;
            treeRoutes += routeCost[v.Index];
            shortestRoutes += shortest.Distance[v.Index];
            shortestTree += shortest.Previous[v.Index]?.EnergyMicroJoules ?? 0;
        }

        var articulation = GraphTraversal.ArticulationPoints(graph, IsUp);
        var critical = new bool[n];
        foreach (var v in graph.Vertices)
            critical[v.Index] = articulation[v.Index] && tree.Reached[v.Index] && v.Kind != MeshNodeKind.Gateway;

        return new MeshState
        {
            Graph = graph,
            Down = down,
            Status = status,
            Tree = tree,
            Hops = hops,
            Downstream = downstream,
            Shortest = shortest,
            ShortestTreeWeight = Math.Round(shortestTree, 2),
            TreeRoutesSum = Math.Round(treeRoutes, 2),
            ShortestRoutesSum = Math.Round(shortestRoutes, 2),
            Critical = critical,
            Components = GraphTraversal.ConnectedComponents(graph, IsUp).Count,
            Version = version
        };
    }

    /// <summary>Which sensors changed uplink, lost their route or got it back between two builds.</summary>
    private static HealingEvent Diff(MeshState before, MeshState after, string trigger)
    {
        var ev = new HealingEvent
        {
            At = DateTimeOffset.UtcNow,
            Trigger = trigger,
            EnergyBeforeMicroJoules = before.Tree.TotalEnergyMicroJoules,
            EnergyAfterMicroJoules = after.Tree.TotalEnergyMicroJoules
        };

        foreach (var v in after.Graph.Vertices)
        {
            if (v.Kind == MeshNodeKind.Gateway) continue;
            var was = before.Status[v.Index];
            var now = after.Status[v.Index];

            if (was == MeshNodeStatus.Online && now == MeshNodeStatus.Online)
            {
                var oldUp = before.Uplink(v);
                var newUp = after.Uplink(v);
                if (!ReferenceEquals(oldUp, newUp))
                    ev.Changes.Add(new RerouteEntry { MacAddress = v.MacAddress, Name = v.Name, OldNextHop = oldUp?.Name, NewNextHop = newUp?.Name });
            }
            else if (was == MeshNodeStatus.Online && now == MeshNodeStatus.Isolated) ev.NewlyIsolated.Add(v.Name);
            else if (was != MeshNodeStatus.Online && now == MeshNodeStatus.Online) ev.Reconnected.Add(v.Name);
        }
        ev.Rerouted = ev.Changes.Count;
        return ev;
    }

    // ================================================================== operator actions

    /// <summary>Takes a node offline (simulated hardware failure) or brings it back. Null when the MAC is unknown.</summary>
    public NodeStateChange? SetFailed(string mac, bool failed)
    {
        lock (_lock)
        {
            RefreshUnsafe(null);
            var v = _graph!.Find(GatewayState.NormaliseMac(mac));
            if (v is null) return null;
            if (v.Kind == MeshNodeKind.Gateway)
                throw new InvalidOperationException("The central gateway is the root of every route and cannot be taken offline.");

            var changed = failed ? _failed.Add(v.MacAddress) : _failed.Remove(v.MacAddress);
            var healing = changed
                ? RefreshUnsafe(failed ? $"{v.Name} taken offline by operator" : $"{v.Name} restored by operator")
                : null;
            return new NodeStateChange { MacAddress = v.MacAddress, Name = v.Name, Failed = _failed.Contains(v.MacAddress), Healing = healing };
        }
    }

    /// <summary>Fails the powered node that carries the most downstream traffic: the worst single failure.</summary>
    public NodeStateChange? FailBusiest()
    {
        lock (_lock)
        {
            RefreshUnsafe(null);
            var s = _state!;
            var busiest = s.Graph.Vertices
                .Where(v => v.Kind != MeshNodeKind.Gateway && s.Status[v.Index] == MeshNodeStatus.Online)
                .OrderByDescending(v => s.Downstream[v.Index])
                .ThenBy(v => v.Index)
                .FirstOrDefault();
            return busiest is null ? null : SetFailed(busiest.MacAddress, true);
        }
    }

    /// <summary>Restores every node the operator took offline.</summary>
    public HealingEvent? RestoreAll()
    {
        lock (_lock)
        {
            RefreshUnsafe(null);
            if (_failed.Count == 0) return null;
            var count = _failed.Count;
            _failed.Clear();
            return RefreshUnsafe($"Operator restored {count} node(s)");
        }
    }

    public List<HealingEvent> Healing(int take = 10)
    {
        lock (_lock) return _healing.Take(Math.Clamp(take, 1, MaxHealingEvents)).ToList();
    }

    /// <summary>Counts for the health endpoint and the sidebar badge.</summary>
    public (int Vertices, int Online, int Down, int Isolated) Summary()
    {
        lock (_lock)
        {
            RefreshUnsafe(null);
            var s = _state!;
            return (s.Status.Length,
                s.Status.Count(x => x == MeshNodeStatus.Online),
                s.Status.Count(x => x is MeshNodeStatus.Failed or MeshNodeStatus.Silent),
                s.Status.Count(x => x == MeshNodeStatus.Isolated));
        }
    }
}
