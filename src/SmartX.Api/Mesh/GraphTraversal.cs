namespace SmartX.Api.Mesh;

/// <summary>Result of a breadth-first search: hop depth and discovery edge per vertex, plus the visit order.</summary>
public sealed class TraversalResult
{
    public required int[] Depth { get; init; }
    public required MeshEdge?[] Via { get; init; }
    public required List<MeshVertex> Order { get; init; }
    public bool Reached(MeshVertex v) => Depth[v.Index] >= 0;
}

/// <summary>
/// Part 3 — graph traversals over the mesh.
/// <list type="bullet">
/// <item><b>BFS</b> (<see cref="BreadthFirst"/>) explores in rings: everything 1 hop away, then 2 hops, and so on. That is
/// exactly "which devices can this gateway or subnet router reach, and in how many hops". It runs in O(V + E) with a
/// FIFO <see cref="Queue{T}"/>.</item>
/// <item><b>DFS</b> (<see cref="ConnectedComponents"/>) follows one path as deep as it goes before backtracking. That is
/// enough to label the islands the mesh has split into after failures. It runs in O(V + E) with an explicit
/// <see cref="Stack{T}"/>, so a long chain of hops cannot overflow the call stack.</item>
/// <item><b>Articulation points</b> (<see cref="ArticulationPoints"/>, Tarjan's DFS low-link method) are the single points
/// of failure: nodes whose loss would cut part of the mesh off from the gateway. The dashboard flags them before
/// anything fails.</item>
/// </list>
/// </summary>
public static class GraphTraversal
{
    /// <param name="canEnter">false for nodes that are down or outside the subnet being searched</param>
    public static TraversalResult BreadthFirst(MeshGraph graph, MeshVertex root, Func<MeshVertex, bool> canEnter)
    {
        var n = graph.Vertices.Count;
        var depth = new int[n];
        Array.Fill(depth, -1);
        var via = new MeshEdge?[n];
        var order = new List<MeshVertex>();
        if (!canEnter(root)) return new TraversalResult { Depth = depth, Via = via, Order = order };

        var queue = new Queue<MeshVertex>();
        depth[root.Index] = 0;
        queue.Enqueue(root);
        while (queue.TryDequeue(out var v))
        {
            order.Add(v);
            foreach (var link in v.Links)
            {
                var next = link.Other(v);
                if (depth[next.Index] >= 0 || !canEnter(next)) continue;   // already discovered, or down / out of scope
                depth[next.Index] = depth[v.Index] + 1;
                via[next.Index] = link;
                queue.Enqueue(next);
            }
        }
        return new TraversalResult { Depth = depth, Via = via, Order = order };
    }

    /// <summary>
    /// Labels every powered vertex with a component number using iterative DFS. Vertices that are down get −1.
    /// </summary>
    /// <returns>The component of each vertex and the number of components.</returns>
    public static (int[] Component, int Count) ConnectedComponents(MeshGraph graph, Func<MeshVertex, bool> isUp)
    {
        var component = new int[graph.Vertices.Count];
        Array.Fill(component, -1);
        var count = 0;
        var stack = new Stack<MeshVertex>();

        foreach (var start in graph.Vertices)
        {
            if (component[start.Index] >= 0 || !isUp(start)) continue;
            component[start.Index] = count;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var v = stack.Pop();
                foreach (var link in v.Links)
                {
                    var next = link.Other(v);
                    if (component[next.Index] >= 0 || !isUp(next)) continue;
                    component[next.Index] = count;
                    stack.Push(next);
                }
            }
            count++;
        }
        return (component, count);
    }

    /// <summary>
    /// Tarjan's articulation-point algorithm, written iteratively. A DFS gives each vertex a discovery time <c>disc</c> and
    /// a <c>low</c> value: the earliest discovery time reachable from its subtree using one back edge. A non-root vertex
    /// <c>u</c> is an articulation point when some child <c>c</c> has <c>low[c] ≥ disc[u]</c>, i.e. nothing below <c>c</c>
    /// can reach above <c>u</c> without going through <c>u</c>. The DFS root is one when it has two or more DFS children.
    /// O(V + E).
    /// </summary>
    public static bool[] ArticulationPoints(MeshGraph graph, Func<MeshVertex, bool> isUp)
    {
        var n = graph.Vertices.Count;
        var disc = new int[n];
        Array.Fill(disc, -1);
        var low = new int[n];
        var parentEdge = new int[n];
        Array.Fill(parentEdge, -1);
        var parent = new MeshVertex?[n];
        var nextLink = new int[n];
        var isArticulation = new bool[n];
        var time = 0;
        var stack = new Stack<MeshVertex>();

        foreach (var root in graph.Vertices)
        {
            if (disc[root.Index] >= 0 || !isUp(root)) continue;
            var rootChildren = 0;
            disc[root.Index] = low[root.Index] = time++;
            stack.Push(root);

            while (stack.Count > 0)
            {
                var v = stack.Peek();
                if (nextLink[v.Index] < v.Links.Count)
                {
                    var link = v.Links[nextLink[v.Index]++];
                    if (link.Index == parentEdge[v.Index]) continue;          // don't walk straight back up the tree edge
                    var w = link.Other(v);
                    if (!isUp(w)) continue;

                    if (disc[w.Index] < 0)                                    // tree edge: descend
                    {
                        parent[w.Index] = v;
                        parentEdge[w.Index] = link.Index;
                        disc[w.Index] = low[w.Index] = time++;
                        if (ReferenceEquals(v, root)) rootChildren++;
                        stack.Push(w);
                    }
                    else                                                       // back edge
                    {
                        low[v.Index] = Math.Min(low[v.Index], disc[w.Index]);
                    }
                }
                else                                                           // finished v: report to its parent
                {
                    stack.Pop();
                    if (parent[v.Index] is { } p)
                    {
                        low[p.Index] = Math.Min(low[p.Index], low[v.Index]);
                        if (!ReferenceEquals(p, root) && low[v.Index] >= disc[p.Index])
                            isArticulation[p.Index] = true;
                    }
                }
            }
            if (rootChildren > 1) isArticulation[root.Index] = true;
        }
        return isArticulation;
    }
}
