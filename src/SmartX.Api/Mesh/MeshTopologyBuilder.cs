using SmartX.Shared.Mesh;
using SmartX.Shared.Models;

namespace SmartX.Api.Mesh;

/// <summary>
/// Part 3 — builds the mesh graph from the Part 1 sensor registry.
/// <list type="bullet">
/// <item>The facility is a 160 m × 100 m floor plan. Each zone (subnet) fills one quadrant. The central gateway
/// sits in the middle, and each zone has a border router at the centre of its quadrant, joined to the
/// gateway by the wired PoE backbone.</item>
/// <item>Sensors sit on a jittered grid inside their zone, ordered by sub-zone and rack. The jitter comes from
/// a hash of the MAC, so a sensor keeps its position across rebuilds.</item>
/// <item>Each sensor gets radio links to its <see cref="NeighboursPerSensor"/> nearest nodes in range.
/// Each router and the gateway get links to their nearest sensors. Nodes near a zone boundary link
/// across it, which gives traffic another way out when a router fails.</item>
/// <item>A final DFS pass joins any island to the main mesh, so every sensor has at least one route.</item>
/// </list>
/// </summary>
public static class MeshTopologyBuilder
{
    public const double Width = 160;
    public const double Height = 100;
    public const int NeighboursPerSensor = 4;
    public const int RouterFanOut = 6;
    public const int GatewayFanOut = 4;
    public const string GatewayMac = "02:53:58:00:00:00";   // locally administered; 0x53 0x58 = "SX"

    private static readonly string[] DefaultZones = { "Zone 1", "Zone 2", "Zone 3", "Zone 4" };

    public static string RouterMac(int zoneIndex) => $"02:53:58:00:00:{zoneIndex + 1:X2}";

    public static MeshGraph Build(IEnumerable<SensorProfile> sensors)
    {
        var all = sensors.ToList();
        var zones = DefaultZones
            .Concat(all.Select(ZoneOf).Where(z => !DefaultZones.Contains(z)).Distinct().OrderBy(z => z, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var graph = new MeshGraph { Width = Width, Height = Height };
        var gateway = graph.AddVertex(GatewayMac, "Smart-X Gateway", MeshNodeKind.Gateway, "Backbone", Width / 2, Height / 2);

        // ---- one border router per zone, at the centre of its quadrant
        var routers = new Dictionary<string, MeshVertex>(StringComparer.OrdinalIgnoreCase);
        for (var z = 0; z < zones.Count; z++)
        {
            var (qx, qy) = Quadrant(z);
            var router = graph.AddVertex(RouterMac(z), $"{zones[z]} router", MeshNodeKind.Router, zones[z], qx + Width / 4, qy + Height / 4);
            routers[zones[z]] = router;
            graph.Connect(gateway, router, backbone: true);
        }

        // ---- sensors on a jittered grid inside their zone's quadrant
        var sensorVertices = new List<MeshVertex>();
        foreach (var zoneGroup in all.GroupBy(ZoneOf, StringComparer.OrdinalIgnoreCase))
        {
            var zoneIndex = zones.FindIndex(z => z.Equals(zoneGroup.Key, StringComparison.OrdinalIgnoreCase));
            var (qx, qy) = Quadrant(zoneIndex);
            var members = zoneGroup
                .OrderBy(s => s.DeploymentPath.ElementAtOrDefault(2) ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.DeploymentPath.LastOrDefault() ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.MacAddress, StringComparer.Ordinal)
                .ToList();

            const double margin = 3;
            double w = Width / 2 - 2 * margin, h = Height / 2 - 2 * margin;
            var cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(members.Count * w / h)));
            var rows = Math.Max(1, (int)Math.Ceiling(members.Count / (double)cols));
            double cw = w / cols, ch = h / rows;

            for (var i = 0; i < members.Count; i++)
            {
                var s = members[i];
                var hash = StableHash(s.MacAddress);
                var jx = ((hash & 0xFFFF) / 65535.0 - 0.5) * cw * 0.6;
                var jy = (((hash >> 16) & 0xFFFF) / 65535.0 - 0.5) * ch * 0.6;
                var x = qx + margin + (i % cols + 0.5) * cw + jx;
                var y = qy + margin + (i / cols + 0.5) * ch + jy;

                // keep clear of the router icon in the middle of the quadrant
                var router = routers[zones[zoneIndex]];
                if (Math.Abs(x - router.X) < 2.5 && Math.Abs(y - router.Y) < 2.5) x += 3;

                sensorVertices.Add(graph.AddVertex(s.MacAddress, s.Name, MeshNodeKind.Sensor, zones[zoneIndex], x, y, s.Id));
            }
        }

        // ---- radio links: k nearest neighbours in range
        foreach (var v in sensorVertices)
            foreach (var (other, _) in Nearest(v, sensorVertices, NeighboursPerSensor))
                graph.Connect(v, other);

        foreach (var router in routers.Values)
            foreach (var (other, _) in Nearest(router, sensorVertices.Where(s => s.Subnet == router.Subnet), RouterFanOut))
                graph.Connect(router, other);

        foreach (var (other, _) in Nearest(gateway, sensorVertices, GatewayFanOut))
            graph.Connect(gateway, other);

        JoinIslands(graph);
        return graph;
    }

    /// <summary>The zone (subnet) of a sensor: the second element of its Part 1 deployment path.</summary>
    public static string ZoneOf(SensorProfile s) => s.DeploymentPath.Count > 1 ? s.DeploymentPath[1] : "Zone 1";

    private static (double X, double Y) Quadrant(int zoneIndex)
    {
        // Zones 1–4 fill the four quadrants. Any extra zones wrap around and share them.
        var q = zoneIndex % 4;
        return (q % 2 * (Width / 2), q / 2 * (Height / 2));
    }

    private static IEnumerable<(MeshVertex Vertex, double Distance)> Nearest(MeshVertex from, IEnumerable<MeshVertex> candidates, int k) =>
        candidates
            .Where(c => !ReferenceEquals(c, from))
            .Select(c => (Vertex: c, Distance: MeshGraph.Distance(from, c)))
            .Where(t => RadioModel.InRange(t.Distance))
            .OrderBy(t => t.Distance)
            .ThenBy(t => t.Vertex.Index)
            .Take(k);

    /// <summary>
    /// DFS from the gateway. Any vertex not reached sits on an island; link the island to the main mesh through the
    /// closest pair of nodes (a long-range link, flagged by its weak RSSI). Repeat until the graph is connected.
    /// </summary>
    private static void JoinIslands(MeshGraph graph)
    {
        while (true)
        {
            var reached = new bool[graph.Vertices.Count];
            var stack = new Stack<MeshVertex>();
            stack.Push(graph.Gateway);
            reached[graph.Gateway.Index] = true;
            while (stack.Count > 0)
            {
                var v = stack.Pop();
                foreach (var e in v.Links)
                {
                    var o = e.Other(v);
                    if (reached[o.Index]) continue;
                    reached[o.Index] = true;
                    stack.Push(o);
                }
            }

            var island = graph.Vertices.Where(v => !reached[v.Index]).ToList();
            if (island.Count == 0) return;
            var main = graph.Vertices.Where(v => reached[v.Index]).ToList();

            var best = (A: island[0], B: main[0], D: double.MaxValue);
            foreach (var a in island)
                foreach (var b in main)
                {
                    var d = MeshGraph.Distance(a, b);
                    if (d < best.D) best = (a, b, d);
                }
            graph.Connect(best.A, best.B);
        }
    }

    /// <summary>FNV-1a hash. string.GetHashCode is randomised per process, which would move nodes on every restart.</summary>
    public static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return hash;
    }
}
