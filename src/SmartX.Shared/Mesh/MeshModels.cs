using System.Text.Json.Serialization;

namespace SmartX.Shared.Mesh;

// -------------------------------------------------------------------------------------------------
// Part 3 — Network Topology & Mesh Routing. Data sent from the API to the Blazor topology dashboard.
// -------------------------------------------------------------------------------------------------

/// <summary>Role of a vertex in the mesh graph.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MeshNodeKind { Gateway, Router, Sensor }

/// <summary>Live status of a vertex after the latest traversal.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MeshNodeStatus
{
    /// <summary>Powered and reachable from the central gateway.</summary>
    Online,
    /// <summary>Taken offline by an operator ("simulate failure").</summary>
    Failed,
    /// <summary>Missed three publish intervals. Detected by the Part 1 heartbeat.</summary>
    Silent,
    /// <summary>Powered, but every path to the gateway runs through a down node.</summary>
    Isolated
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpanningTreeAlgorithm { Prim, Kruskal }

/// <summary>One vertex (ESP32 node, zone border router or the central gateway).</summary>
public sealed class MeshNode
{
    public string MacAddress { get; set; } = "";
    public string Name { get; set; } = "";
    public MeshNodeKind Kind { get; set; }
    /// <summary>The subnet the node belongs to, e.g. "Zone 2" ("Backbone" for the central gateway).</summary>
    public string Subnet { get; set; } = "";
    /// <summary>Floor-plan position in metres. The SVG view uses the same coordinates.</summary>
    public double X { get; set; }
    public double Y { get; set; }
    public MeshNodeStatus Status { get; set; }
    /// <summary>Hops to the gateway along the spanning tree; −1 when unreachable.</summary>
    public int Hops { get; set; } = -1;
    /// <summary>Next hop towards the gateway on the spanning tree (null for the gateway or unreachable nodes).</summary>
    public string? NextHop { get; set; }
    /// <summary>How many nodes route their telemetry through this one, i.e. its spanning-tree subtree size minus itself.</summary>
    public int Downstream { get; set; }
    /// <summary>True when the DFS articulation-point search shows that losing this node would cut others off.</summary>
    public bool Critical { get; set; }
    /// <summary>Part 1 sensor id (links to the sensor profile page); null for gateway and routers.</summary>
    public Guid? SensorId { get; set; }
    public int Degree { get; set; }
}

/// <summary>One undirected radio (or wired backbone) link.</summary>
public sealed class MeshLink
{
    public string A { get; set; } = "";
    public string B { get; set; } = "";
    public double DistanceMetres { get; set; }
    /// <summary>Estimated received signal strength (log-distance path-loss model).</summary>
    public int Rssi { get; set; }
    /// <summary>Energy to move one 250-byte telemetry frame across the link (transmit + receive), in microjoules.</summary>
    public double EnergyMicroJoules { get; set; }
    public bool Backbone { get; set; }
    /// <summary>Both end points are up.</summary>
    public bool Active { get; set; }
    /// <summary>The link belongs to the current minimum spanning tree.</summary>
    public bool InTree { get; set; }
}

public sealed class MeshStats
{
    public int Vertices { get; set; }
    public int Links { get; set; }
    public int ActiveLinks { get; set; }
    public int Online { get; set; }
    public int Failed { get; set; }
    public int Silent { get; set; }
    public int Isolated { get; set; }
    /// <summary>Connected components among the powered nodes (DFS). 1 means the mesh is in one piece.</summary>
    public int Components { get; set; }
    public int CriticalNodes { get; set; }
    public int TreeLinks { get; set; }
    /// <summary>Total energy of the spanning tree: the cheapest way to connect every reachable node.</summary>
    public double TreeEnergyMicroJoules { get; set; }
    /// <summary>Total energy if every active link were used. Shows how much the MST prunes.</summary>
    public double AllLinksEnergyMicroJoules { get; set; }
    /// <summary>
    /// Weight of Dijkstra's shortest-path tree. With in-network aggregation (each relay merges what it forwards into one
    /// frame per round) a round costs the weight of the routing tree, and the MST is never heavier than this.
    /// </summary>
    public double ShortestPathTreeEnergyMicroJoules { get; set; }
    /// <summary>Without aggregation: every node's packet relayed separately along its MST route (sum of route costs).</summary>
    public double TreeRoutesEnergyMicroJoules { get; set; }
    /// <summary>Without aggregation: every node's packet relayed separately along its Dijkstra route.</summary>
    public double ShortestRoutesEnergyMicroJoules { get; set; }
    public double AverageHops { get; set; }
    public int MaxHops { get; set; }
    public SpanningTreeAlgorithm Algorithm { get; set; }
    /// <summary>Wall-clock time of the last spanning-tree build, in microseconds.</summary>
    public double ComputeMicroseconds { get; set; }
    /// <summary>Heap pushes/pops (Prim) or union-find calls (Kruskal) for the last build.</summary>
    public long Operations { get; set; }
    public long Version { get; set; }
}

/// <summary>Everything the topology dashboard draws in one poll.</summary>
public sealed class MeshSnapshot
{
    public DateTimeOffset GeneratedAt { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string GatewayMac { get; set; } = "";
    public List<string> Subnets { get; set; } = new();
    public List<MeshNode> Nodes { get; set; } = new();
    public List<MeshLink> Links { get; set; } = new();
    public MeshStats Stats { get; set; } = new();
    public List<HealingEvent> RecentHealing { get; set; } = new();
}

/// <summary>One step of the spanning-tree construction, shown on the "how the MST was built" panel.</summary>
public sealed class SpanningTreeStep
{
    public int Step { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public double EnergyMicroJoules { get; set; }
    public double RunningTotal { get; set; }
}

public sealed class SpanningTreeReport
{
    public SpanningTreeAlgorithm Algorithm { get; set; }
    public int TreeLinks { get; set; }
    public double TotalEnergyMicroJoules { get; set; }
    public double ComputeMicroseconds { get; set; }
    public long Operations { get; set; }
    public string Complexity { get; set; } = "";
    /// <summary>The first links chosen, in the order the algorithm accepted them.</summary>
    public List<SpanningTreeStep> FirstSteps { get; set; } = new();
    /// <summary>The same graph solved with the other algorithm. Totals must match because the MST weight is unique.</summary>
    public double OtherAlgorithmEnergyMicroJoules { get; set; }
}
