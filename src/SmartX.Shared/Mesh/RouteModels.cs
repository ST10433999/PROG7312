namespace SmartX.Shared.Mesh;

// -------------------------------------------------------------------------------------------------
// Part 3 — route tracking, BFS reachability and self-healing reports.
// -------------------------------------------------------------------------------------------------

/// <summary>One hop on a telemetry route.</summary>
public sealed class RouteHop
{
    public string MacAddress { get; set; } = "";
    public string Name { get; set; } = "";
    public MeshNodeKind Kind { get; set; }
    public string Subnet { get; set; } = "";
    public MeshNodeStatus Status { get; set; }
    /// <summary>Energy of the link from the previous hop to this one (0 for the first hop).</summary>
    public double LinkEnergyMicroJoules { get; set; }
    public int LinkRssi { get; set; }
}

/// <summary>Result of tracking one sensor's route to the gateway.</summary>
public sealed class RouteResult
{
    public string MacAddress { get; set; } = "";
    public string Name { get; set; } = "";
    public string Subnet { get; set; } = "";
    public MeshNodeStatus Status { get; set; }
    public bool Reachable { get; set; }

    /// <summary>The path telemetry takes along the minimum spanning tree (sensor → … → gateway).</summary>
    public List<RouteHop> TreePath { get; set; } = new();
    public double TreePathEnergyMicroJoules { get; set; }

    /// <summary>Dijkstra's minimum-energy path for the same sensor, shown for comparison.</summary>
    public List<RouteHop> ShortestPath { get; set; } = new();
    public double ShortestPathEnergyMicroJoules { get; set; }

    /// <summary>How much more energy the tree path costs than the cheapest possible single path (0 = identical).</summary>
    public double StretchPercent { get; set; }

    /// <summary>The route before the most recent self-healing event, when that event changed it.</summary>
    public List<string> PreviousPath { get; set; } = new();
    public bool Rerouted { get; set; }

    /// <summary>For an isolated node: powered neighbours that could carry its traffic if a link came back.</summary>
    public List<string> Suggestions { get; set; } = new();
    public string Explanation { get; set; } = "";
}

public sealed class ReachNode
{
    public string MacAddress { get; set; } = "";
    public string Name { get; set; } = "";
    public string Subnet { get; set; } = "";
    public MeshNodeKind Kind { get; set; }
    public MeshNodeStatus Status { get; set; }
    /// <summary>BFS parent, i.e. the node through which this one was discovered.</summary>
    public string? Via { get; set; }
}

public sealed class HopLevel
{
    public int Hop { get; set; }
    public List<ReachNode> Nodes { get; set; } = new();
}

/// <summary>A powered node that the traversal could not reach, with where it could hop instead.</summary>
public sealed class CutOffNode
{
    public string MacAddress { get; set; } = "";
    public string Name { get; set; } = "";
    public string Subnet { get; set; } = "";
    public string? HopVia { get; set; }
    public string? HopViaName { get; set; }
    public string? HopViaSubnet { get; set; }
    public string Advice { get; set; } = "";
}

/// <summary>Breadth-first search result: which devices a gateway or subnet router can currently reach, level by level.</summary>
public sealed class ReachabilityResult
{
    public string RootMac { get; set; } = "";
    public string RootName { get; set; } = "";
    /// <summary>Subnet the search was confined to; null when searching the whole mesh from the gateway.</summary>
    public string? Subnet { get; set; }
    public string Algorithm { get; set; } = "BFS";
    public List<HopLevel> Levels { get; set; } = new();
    public int Reached { get; set; }
    /// <summary>Devices that belong to the scope (whole mesh or subnet).</summary>
    public int InScope { get; set; }
    public List<ReachNode> Down { get; set; } = new();
    public List<CutOffNode> CutOff { get; set; } = new();
    public double ElapsedMicroseconds { get; set; }
}

/// <summary>A device whose next hop changed when the mesh healed itself.</summary>
public sealed class RerouteEntry
{
    public string MacAddress { get; set; } = "";
    public string Name { get; set; } = "";
    public string? OldNextHop { get; set; }
    public string? NewNextHop { get; set; }
}

/// <summary>What happened the last time the spanning tree was rebuilt because a node went down or came back.</summary>
public sealed class HealingEvent
{
    public DateTimeOffset At { get; set; }
    public string Trigger { get; set; } = "";
    public int Rerouted { get; set; }
    public List<RerouteEntry> Changes { get; set; } = new();
    public List<string> NewlyIsolated { get; set; } = new();
    public List<string> Reconnected { get; set; } = new();
    public double EnergyBeforeMicroJoules { get; set; }
    public double EnergyAfterMicroJoules { get; set; }
}

/// <summary>Result of taking a node offline or restoring it.</summary>
public sealed class NodeStateChange
{
    public string MacAddress { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Failed { get; set; }
    public HealingEvent? Healing { get; set; }
}
