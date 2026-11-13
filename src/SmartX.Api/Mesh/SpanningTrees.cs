using System.Diagnostics;
using SmartX.Shared.Collections;
using SmartX.Shared.Mesh;

namespace SmartX.Api.Mesh;

/// <summary>A minimum spanning tree rooted at the gateway (or the part of it the gateway can still reach).</summary>
public sealed class SpanningTree
{
    public required SpanningTreeAlgorithm Algorithm { get; init; }
    /// <summary>For every reached vertex except the root: the tree edge towards the root (its uplink).</summary>
    public required MeshEdge?[] ParentEdge { get; init; }
    /// <summary>Tree edges in the order the algorithm accepted them.</summary>
    public required List<MeshEdge> Accepted { get; init; }
    public required bool[] Reached { get; init; }
    public required double TotalEnergyMicroJoules { get; init; }
    public required long Operations { get; init; }
    public required double Microseconds { get; init; }
}

/// <summary>Single-source shortest paths (Dijkstra), used to compare against the tree routes.</summary>
public sealed class ShortestPaths
{
    public required double[] Distance { get; init; }
    public required MeshEdge?[] Previous { get; init; }
}

/// <summary>
/// Part 3 — minimum spanning tree and shortest-path algorithms over the mesh graph. Edge weight = energy to move one
/// telemetry frame across the link (<see cref="RadioModel"/>).
/// <para>
/// The <b>minimum spanning tree</b> connects every reachable node to the gateway with the least total link energy.
/// No cycles means each sensor has exactly one uplink. That is the most power-efficient routing backbone for the
/// whole fleet, and the tree a routing protocol would converge to.
/// </para>
/// </summary>
public static class SpanningTrees
{
    private readonly record struct Candidate(double Energy, int Edge, int To);

    private static readonly Comparison<Candidate> CheapestFirst = (a, b) =>
    {
        var c = a.Energy.CompareTo(b.Energy);
        return c != 0 ? c : a.Edge.CompareTo(b.Edge);              // deterministic tie-break
    };

    /// <summary>
    /// <b>Prim's algorithm</b>: grow the tree outward from the gateway, always adding the cheapest link that reaches a new node.
    /// Candidate links wait in a <see cref="BinaryMinHeap{T}"/>. A popped link whose far end is already in the tree is
    /// stale and is skipped ("lazy deletion"). O(E log V).
    /// </summary>
    public static SpanningTree Prim(MeshGraph graph, MeshVertex root, Func<MeshVertex, bool> isUp)
    {
        var sw = Stopwatch.StartNew();
        var n = graph.Vertices.Count;
        var inTree = new bool[n];
        var parent = new MeshEdge?[n];
        var accepted = new List<MeshEdge>(n);
        var heap = new BinaryMinHeap<Candidate>(CheapestFirst, Math.Max(16, graph.Edges.Count));
        double total = 0;

        void AddFrontier(MeshVertex v)
        {
            foreach (var link in v.Links)
            {
                var next = link.Other(v);
                if (!inTree[next.Index] && isUp(next))
                    heap.Push(new Candidate(link.EnergyMicroJoules, link.Index, next.Index));
            }
        }

        if (isUp(root))
        {
            inTree[root.Index] = true;
            AddFrontier(root);
        }

        while (heap.TryPop(out var c))
        {
            if (inTree[c.To]) continue;                                // stale candidate
            var link = graph.Edges[c.Edge];
            inTree[c.To] = true;
            parent[c.To] = link;
            accepted.Add(link);
            total += link.EnergyMicroJoules;
            AddFrontier(graph.Vertices[c.To]);
        }
        sw.Stop();

        return new SpanningTree
        {
            Algorithm = SpanningTreeAlgorithm.Prim,
            ParentEdge = parent,
            Accepted = accepted,
            Reached = inTree,
            TotalEnergyMicroJoules = Math.Round(total, 2),
            Operations = heap.Operations,
            Microseconds = Math.Round(sw.Elapsed.TotalMicroseconds, 1)
        };
    }

    /// <summary>
    /// <b>Kruskal's algorithm</b>: sort every active link by energy, then accept each one unless it would close a cycle,
    /// which a <see cref="DisjointSet"/> checks in near-constant time. This yields a spanning forest; the tree containing
    /// the gateway is kept. O(E log E).
    /// </summary>
    public static SpanningTree Kruskal(MeshGraph graph, MeshVertex root, Func<MeshVertex, bool> isUp)
    {
        var sw = Stopwatch.StartNew();
        var n = graph.Vertices.Count;
        var links = graph.Edges.Where(e => isUp(e.A) && isUp(e.B)).ToList();
        links.Sort((a, b) =>
        {
            var c = a.EnergyMicroJoules.CompareTo(b.EnergyMicroJoules);
            return c != 0 ? c : a.Index.CompareTo(b.Index);
        });

        var sets = new DisjointSet(n);
        var forest = new List<MeshEdge>(n);
        long operations = 0;
        foreach (var link in links)
        {
            operations++;
            if (sets.Union(link.A.Index, link.B.Index)) forest.Add(link);
            if (forest.Count == n - 1) break;
        }

        // Keep only the tree the gateway belongs to, then orient it: BFS from the gateway over tree edges gives each
        // node its uplink.
        var reached = new bool[n];
        var parent = new MeshEdge?[n];
        var accepted = new List<MeshEdge>();
        double total = 0;
        if (isUp(root))
        {
            var rootSet = sets.Find(root.Index);
            var treeLinks = new List<MeshEdge>?[n];
            foreach (var link in forest)
            {
                if (sets.Find(link.A.Index) != rootSet) continue;
                accepted.Add(link);
                total += link.EnergyMicroJoules;
                (treeLinks[link.A.Index] ??= new()).Add(link);
                (treeLinks[link.B.Index] ??= new()).Add(link);
            }

            var queue = new Queue<MeshVertex>();
            reached[root.Index] = true;
            queue.Enqueue(root);
            while (queue.TryDequeue(out var v))
            {
                if (treeLinks[v.Index] is not { } own) continue;
                foreach (var link in own)
                {
                    var next = link.Other(v);
                    if (reached[next.Index]) continue;
                    reached[next.Index] = true;
                    parent[next.Index] = link;
                    queue.Enqueue(next);
                }
            }
        }
        sw.Stop();

        return new SpanningTree
        {
            Algorithm = SpanningTreeAlgorithm.Kruskal,
            ParentEdge = parent,
            Accepted = accepted,
            Reached = reached,
            TotalEnergyMicroJoules = Math.Round(total, 2),
            Operations = operations,
            Microseconds = Math.Round(sw.Elapsed.TotalMicroseconds, 1)
        };
    }

    public static SpanningTree Build(SpanningTreeAlgorithm algorithm, MeshGraph graph, MeshVertex root, Func<MeshVertex, bool> isUp) =>
        algorithm == SpanningTreeAlgorithm.Kruskal ? Kruskal(graph, root, isUp) : Prim(graph, root, isUp);

    /// <summary>
    /// <b>Dijkstra's algorithm</b> from the gateway. The cheapest single path from every node, for comparison with the
    /// tree route. Same min-heap with lazy deletion. O(E log V).
    /// </summary>
    public static ShortestPaths Dijkstra(MeshGraph graph, MeshVertex source, Func<MeshVertex, bool> isUp)
    {
        var n = graph.Vertices.Count;
        var dist = new double[n];
        Array.Fill(dist, double.PositiveInfinity);
        var previous = new MeshEdge?[n];
        var heap = new BinaryMinHeap<(double Distance, int Vertex)>((a, b) => a.Distance.CompareTo(b.Distance));

        if (isUp(source))
        {
            dist[source.Index] = 0;
            heap.Push((0, source.Index));
        }

        while (heap.TryPop(out var item))
        {
            if (item.Distance > dist[item.Vertex]) continue;           // stale entry
            var v = graph.Vertices[item.Vertex];
            foreach (var link in v.Links)
            {
                var next = link.Other(v);
                if (!isUp(next)) continue;
                var candidate = item.Distance + link.EnergyMicroJoules;
                if (candidate >= dist[next.Index]) continue;
                dist[next.Index] = candidate;
                previous[next.Index] = link;
                heap.Push((candidate, next.Index));
            }
        }
        return new ShortestPaths { Distance = dist, Previous = previous };
    }
}
