using SmartX.Api.Mesh;
using SmartX.Shared.Collections;
using SmartX.Shared.Mesh;

namespace SmartX.Tests;

/// <summary>Part 3 — BFS, DFS, articulation points, Prim, Kruskal and Dijkstra on small hand-built graphs.</summary>
public class GraphAlgorithmTests
{
    /// <summary>Gateway at x=0, then sensors every 10 m along a line: G - s1 - s2 - s3 - s4.</summary>
    private static MeshGraph Chain(int sensors)
    {
        var g = new MeshGraph();
        var previous = g.AddVertex("00:00:00:00:00:00", "GW", MeshNodeKind.Gateway, "Backbone", 0, 0);
        for (var i = 1; i <= sensors; i++)
        {
            var v = g.AddVertex($"00:00:00:00:00:{i:X2}", $"s{i}", MeshNodeKind.Sensor, "Zone 1", i * 10, 0);
            g.Connect(previous, v);
            previous = v;
        }
        return g;
    }

    [Fact]
    public void Bfs_reports_hop_depth_and_stops_at_down_nodes()
    {
        var g = Chain(4);
        var bfs = GraphTraversal.BreadthFirst(g, g.Gateway, _ => true);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, bfs.Depth);

        var s2 = g.Find("00:00:00:00:00:02")!;
        var blocked = GraphTraversal.BreadthFirst(g, g.Gateway, v => v != s2);
        Assert.Equal(new[] { 0, 1, -1, -1, -1 }, blocked.Depth);
    }

    [Fact]
    public void Dfs_counts_islands_after_a_failure()
    {
        var g = Chain(4);
        var s2 = g.Find("00:00:00:00:00:02")!;
        Assert.Equal(1, GraphTraversal.ConnectedComponents(g, _ => true).Count);
        Assert.Equal(2, GraphTraversal.ConnectedComponents(g, v => v != s2).Count);   // {GW, s1} and {s3, s4}
    }

    [Fact]
    public void Articulation_points_on_chain_and_ring()
    {
        var chain = Chain(4);
        var ap = GraphTraversal.ArticulationPoints(chain, _ => true);
        Assert.Equal(new[] { false, true, true, true, false }, ap);   // s1–s3 are single points of failure; the ends are not

        // Close the ring s4 → GW: now no single failure disconnects anything.
        var ring = Chain(4);
        ring.Connect(ring.Vertices[4], ring.Gateway);
        Assert.All(GraphTraversal.ArticulationPoints(ring, _ => true), Assert.False);
    }

    /// <summary>Small graph whose MST weight is checked against brute force over every spanning subset.</summary>
    private static MeshGraph Diamond()
    {
        var g = new MeshGraph();
        var gw = g.AddVertex("10:00:00:00:00:00", "GW", MeshNodeKind.Gateway, "Backbone", 0, 0);
        var a = g.AddVertex("10:00:00:00:00:01", "A", MeshNodeKind.Sensor, "Zone 1", 8, 3);
        var b = g.AddVertex("10:00:00:00:00:02", "B", MeshNodeKind.Sensor, "Zone 1", 6, -9);
        var c = g.AddVertex("10:00:00:00:00:03", "C", MeshNodeKind.Sensor, "Zone 2", 17, -2);
        var d = g.AddVertex("10:00:00:00:00:04", "D", MeshNodeKind.Sensor, "Zone 2", 22, 8);
        g.Connect(gw, a); g.Connect(gw, b); g.Connect(a, b); g.Connect(a, c);
        g.Connect(b, c); g.Connect(c, d); g.Connect(a, d);
        return g;
    }

    private static double BruteForceMst(MeshGraph g)
    {
        var edges = g.Edges.ToList();
        var n = g.Vertices.Count;
        var best = double.MaxValue;
        for (var mask = 0; mask < 1 << edges.Count; mask++)
        {
            if (System.Numerics.BitOperations.PopCount((uint)mask) != n - 1) continue;
            var sets = new DisjointSet(n);
            double total = 0;
            var acyclic = true;
            for (var i = 0; i < edges.Count; i++)
            {
                if ((mask & 1 << i) == 0) continue;
                if (!sets.Union(edges[i].A.Index, edges[i].B.Index)) { acyclic = false; break; }
                total += edges[i].EnergyMicroJoules;
            }
            if (acyclic && sets.Sets == 1) best = Math.Min(best, total);
        }
        return Math.Round(best, 2);
    }

    [Fact]
    public void Prim_and_Kruskal_find_the_true_minimum()
    {
        var g = Diamond();
        var prim = SpanningTrees.Prim(g, g.Gateway, _ => true);
        var kruskal = SpanningTrees.Kruskal(g, g.Gateway, _ => true);
        var expected = BruteForceMst(g);

        Assert.Equal(expected, prim.TotalEnergyMicroJoules, 2);
        Assert.Equal(expected, kruskal.TotalEnergyMicroJoules, 2);
        Assert.Equal(g.Vertices.Count - 1, prim.Accepted.Count);
        Assert.All(prim.Reached, Assert.True);
    }

    [Fact]
    public void Spanning_tree_routes_around_a_down_node()
    {
        var g = Diamond();
        var a = g.Find("10:00:00:00:00:01")!;
        var tree = SpanningTrees.Prim(g, g.Gateway, v => v != a);

        Assert.False(tree.Reached[a.Index]);
        Assert.Equal(3, tree.Accepted.Count);                        // GW, B, C, D still connected
        Assert.DoesNotContain(tree.Accepted, e => e.A == a || e.B == a);
    }

    [Fact]
    public void Dijkstra_never_costs_more_than_the_tree_path()
    {
        var g = Diamond();
        var tree = SpanningTrees.Prim(g, g.Gateway, _ => true);
        var shortest = SpanningTrees.Dijkstra(g, g.Gateway, _ => true);

        foreach (var v in g.Vertices.Skip(1))
        {
            double treeCost = 0;
            for (var at = v; tree.ParentEdge[at.Index] is { } up; at = up.Other(at)) treeCost += up.EnergyMicroJoules;
            Assert.True(shortest.Distance[v.Index] <= treeCost + 1e-9);
        }
    }

    [Fact]
    public void Radio_model_costs_grow_with_distance_and_range_is_enforced()
    {
        // Energy rises with d² (amplifier) on top of a fixed electronics cost per hop.
        Assert.True(RadioModel.LinkEnergyMicroJoules(5) < RadioModel.LinkEnergyMicroJoules(10));
        Assert.True(RadioModel.LinkEnergyMicroJoules(10) < RadioModel.LinkEnergyMicroJoules(20));
        Assert.Equal(200, RadioModel.LinkEnergyMicroJoules(0), 2);           // 2 × 50 nJ/bit × 2000 bits
        Assert.True(RadioModel.Rssi(5) > RadioModel.Rssi(20));
        Assert.True(RadioModel.InRange(20));
        Assert.False(RadioModel.InRange(40));
        Assert.InRange(RadioModel.MaxRangeMetres, 24, 27);
    }
}
