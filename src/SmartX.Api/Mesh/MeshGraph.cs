using SmartX.Shared.Mesh;

namespace SmartX.Api.Mesh;

/// <summary>A vertex of the mesh graph: an ESP32 sensor, a zone border router or the central gateway.</summary>
public sealed class MeshVertex
{
    public required int Index { get; init; }
    public required string MacAddress { get; init; }
    public required string Name { get; init; }
    public required MeshNodeKind Kind { get; init; }
    public required string Subnet { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public Guid? SensorId { get; init; }

    /// <summary>Adjacency list: every radio or backbone link touching this vertex.</summary>
    public List<MeshEdge> Links { get; } = new();

    public override string ToString() => $"{Name} ({MacAddress})";
}

/// <summary>An undirected, weighted edge. The weight used by the algorithms is <see cref="EnergyMicroJoules"/>.</summary>
public sealed class MeshEdge
{
    public required int Index { get; init; }
    public required MeshVertex A { get; init; }
    public required MeshVertex B { get; init; }
    public required double DistanceMetres { get; init; }
    public required int Rssi { get; init; }
    public required double EnergyMicroJoules { get; init; }
    public bool Backbone { get; init; }

    public MeshVertex Other(MeshVertex v) => ReferenceEquals(v, A) ? B : A;
}

/// <summary>
/// Part 3 — the physical IoT mesh as a <b>graph</b>. Vertices are the ESP32 nodes (plus the gateway and zone routers);
/// edges are the wireless links between nodes in radio range.
/// <para>
/// Stored as an <b>adjacency list</b> (each vertex keeps a <c>List&lt;MeshEdge&gt;</c>), not an adjacency matrix. A mesh
/// is sparse: each node hears a handful of neighbours, so the list needs O(V + E) memory where a matrix needs O(V²).
/// BFS, DFS, Prim and Dijkstra also only ever ask "who are my neighbours?", which a list answers in O(degree).
/// A <c>Dictionary&lt;MAC, vertex&gt;</c> gives O(1) lookup when the operator types a MAC address.
/// </para>
/// </summary>
public sealed class MeshGraph
{
    private readonly List<MeshVertex> _vertices = new();
    private readonly List<MeshEdge> _edges = new();
    private readonly Dictionary<string, MeshVertex> _byMac = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<MeshVertex> Vertices => _vertices;
    public IReadOnlyList<MeshEdge> Edges => _edges;
    public double Width { get; init; }
    public double Height { get; init; }
    public MeshVertex Gateway => _vertices.Count > 0 ? _vertices[0] : throw new InvalidOperationException("The graph is empty.");

    /// <summary>The first vertex added becomes the gateway (index 0).</summary>
    public MeshVertex AddVertex(string mac, string name, MeshNodeKind kind, string subnet, double x, double y, Guid? sensorId = null)
    {
        if (_byMac.ContainsKey(mac)) throw new ArgumentException($"Vertex {mac} already exists.", nameof(mac));
        var v = new MeshVertex
        {
            Index = _vertices.Count, MacAddress = mac, Name = name, Kind = kind, Subnet = subnet,
            X = Math.Round(x, 2), Y = Math.Round(y, 2), SensorId = sensorId
        };
        _vertices.Add(v);
        _byMac[mac] = v;
        return v;
    }

    /// <summary>Adds an undirected link (once). Returns the existing link when the two vertices are already joined.</summary>
    public MeshEdge Connect(MeshVertex a, MeshVertex b, bool backbone = false)
    {
        if (ReferenceEquals(a, b)) throw new ArgumentException("A vertex cannot link to itself.");
        foreach (var e in a.Links)
            if (ReferenceEquals(e.Other(a), b)) return e;

        var d = Distance(a, b);
        var edge = new MeshEdge
        {
            Index = _edges.Count, A = a, B = b,
            DistanceMetres = Math.Round(d, 2),
            Rssi = backbone ? 0 : RadioModel.Rssi(d),
            EnergyMicroJoules = backbone ? RadioModel.BackboneMicroJoules : RadioModel.LinkEnergyMicroJoules(d),
            Backbone = backbone
        };
        _edges.Add(edge);
        a.Links.Add(edge);
        b.Links.Add(edge);
        return edge;
    }

    public MeshVertex? Find(string mac) => _byMac.TryGetValue(mac, out var v) ? v : null;

    public static double Distance(MeshVertex a, MeshVertex b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
