using System.Diagnostics;
using SmartX.Api.Services;
using SmartX.Shared.Mesh;

namespace SmartX.Api.Mesh;

// Part 3 — read side of the mesh: dashboard snapshot, route tracking, BFS reachability and the MST report.
public sealed partial class MeshNetworkService
{
    // ================================================================== snapshot

    /// <summary>Nodes, links, statistics and recent healing events for the topology dashboard.</summary>
    public MeshSnapshot Snapshot(SpanningTreeAlgorithm algorithm = SpanningTreeAlgorithm.Prim)
    {
        lock (_lock)
        {
            RefreshUnsafe(null);
            // Routing always runs on Prim's tree; asking for Kruskal rebuilds the same graph with the other algorithm
            // so the two can be compared on screen (the total energy must be identical).
            var s = algorithm == SpanningTreeAlgorithm.Prim ? _state! : Compute(_graph!, algorithm, _state!.Version);
            var g = s.Graph;

            var treeEdges = new HashSet<int>(s.Tree.Accepted.Select(e => e.Index));
            var snapshot = new MeshSnapshot
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                Width = g.Width,
                Height = g.Height,
                GatewayMac = g.Gateway.MacAddress,
                Subnets = g.Vertices.Where(v => v.Kind == MeshNodeKind.Router).Select(v => v.Subnet).ToList(),
                RecentHealing = _healing.Take(8).ToList()
            };

            foreach (var v in g.Vertices)
            {
                snapshot.Nodes.Add(new MeshNode
                {
                    MacAddress = v.MacAddress,
                    Name = v.Name,
                    Kind = v.Kind,
                    Subnet = v.Subnet,
                    X = v.X,
                    Y = v.Y,
                    Status = s.Status[v.Index],
                    Hops = s.Hops[v.Index],
                    NextHop = s.Uplink(v)?.MacAddress,
                    Downstream = s.Downstream[v.Index],
                    Critical = s.Critical[v.Index],
                    SensorId = v.SensorId,
                    Degree = v.Links.Count
                });
            }

            double allActiveEnergy = 0;
            foreach (var e in g.Edges)
            {
                var active = s.IsUp(e.A) && s.IsUp(e.B);
                if (active) allActiveEnergy += e.EnergyMicroJoules;
                snapshot.Links.Add(new MeshLink
                {
                    A = e.A.MacAddress,
                    B = e.B.MacAddress,
                    DistanceMetres = e.DistanceMetres,
                    Rssi = e.Rssi,
                    EnergyMicroJoules = e.EnergyMicroJoules,
                    Backbone = e.Backbone,
                    Active = active,
                    InTree = treeEdges.Contains(e.Index)
                });
            }

            var reachedHops = s.Hops.Where(h => h > 0).ToList();
            snapshot.Stats = new MeshStats
            {
                Vertices = g.Vertices.Count,
                Links = g.Edges.Count,
                ActiveLinks = snapshot.Links.Count(l => l.Active),
                Online = s.Status.Count(x => x == MeshNodeStatus.Online),
                Failed = s.Status.Count(x => x == MeshNodeStatus.Failed),
                Silent = s.Status.Count(x => x == MeshNodeStatus.Silent),
                Isolated = s.Status.Count(x => x == MeshNodeStatus.Isolated),
                Components = s.Components,
                CriticalNodes = s.Critical.Count(c => c),
                TreeLinks = s.Tree.Accepted.Count,
                TreeEnergyMicroJoules = s.Tree.TotalEnergyMicroJoules,
                AllLinksEnergyMicroJoules = Math.Round(allActiveEnergy, 2),
                ShortestPathTreeEnergyMicroJoules = s.ShortestTreeWeight,
                TreeRoutesEnergyMicroJoules = s.TreeRoutesSum,
                ShortestRoutesEnergyMicroJoules = s.ShortestRoutesSum,
                AverageHops = reachedHops.Count > 0 ? Math.Round(reachedHops.Average(), 2) : 0,
                MaxHops = reachedHops.Count > 0 ? reachedHops.Max() : 0,
                Algorithm = s.Tree.Algorithm,
                ComputeMicroseconds = s.Tree.Microseconds,
                Operations = s.Tree.Operations,
                Version = s.Version
            };
            return snapshot;
        }
    }

    // ================================================================== route tracking

    /// <summary>The route a sensor's telemetry takes to the gateway, and how it compares with the cheapest single path.</summary>
    public RouteResult? Route(string mac)
    {
        lock (_lock)
        {
            RefreshUnsafe(null);
            var s = _state!;
            var v = s.Graph.Find(GatewayState.NormaliseMac(mac));
            if (v is null) return null;

            var status = s.Status[v.Index];
            var result = new RouteResult
            {
                MacAddress = v.MacAddress,
                Name = v.Name,
                Subnet = v.Subnet,
                Status = status,
                Reachable = status == MeshNodeStatus.Online
            };

            if (v.Kind == MeshNodeKind.Gateway)
            {
                result.TreePath.Add(Hop(s, v, null));
                result.ShortestPath.Add(Hop(s, v, null));
                result.Explanation = "This is the central gateway: every route ends here.";
                return result;
            }

            if (status is MeshNodeStatus.Failed or MeshNodeStatus.Silent)
            {
                var neighbours = v.Links.Select(l => l.Other(v)).Where(o => s.Status[o.Index] == MeshNodeStatus.Online).Select(o => o.Name).ToList();
                result.Suggestions = neighbours;
                result.Explanation = status == MeshNodeStatus.Failed
                    ? $"{v.Name} has been taken offline, so it sends no telemetry. Nodes that used to hop through it were re-routed."
                    : $"{v.Name} missed {SilentAfterIntervals:0} publish intervals (Part 1 heartbeat), so it is treated as down until it reports again.";
                AddPreviousPath(result, v);
                return result;
            }

            if (status == MeshNodeStatus.Isolated)
            {
                var blockers = v.Links.Select(l => l.Other(v)).Where(o => !s.IsUp(o)).Select(o => o.Name).Distinct().ToList();
                result.Suggestions = blockers.Select(b => $"Restore {b}").ToList();
                result.Explanation = $"{v.Name} is powered, but every path to the gateway runs through a node that is down"
                                     + (blockers.Count > 0 ? $" ({string.Join(", ", blockers.Take(4))})." : ".")
                                     + " Its readings are buffered until a neighbour comes back.";
                AddPreviousPath(result, v);
                return result;
            }

            // ---- online: follow uplinks to the gateway along the spanning tree
            for (MeshVertex? at = v, previous = null; at is not null; previous = at, at = s.Uplink(at))
                result.TreePath.Add(Hop(s, at, previous));
            result.TreePathEnergyMicroJoules = Math.Round(result.TreePath.Sum(h => h.LinkEnergyMicroJoules), 2);

            // ---- Dijkstra's cheapest path (computed from the gateway; links are undirected, so walk it backwards)
            for (MeshVertex? at = v, previous = null; at is not null; previous = at, at = s.Shortest.Previous[at.Index]?.Other(at))
                result.ShortestPath.Add(Hop(s, at, previous));
            result.ShortestPathEnergyMicroJoules = Math.Round(s.Shortest.Distance[v.Index], 2);

            result.StretchPercent = result.ShortestPathEnergyMicroJoules > 0
                ? Math.Round((result.TreePathEnergyMicroJoules - result.ShortestPathEnergyMicroJoules) / result.ShortestPathEnergyMicroJoules * 100, 1)
                : 0;

            AddPreviousPath(result, v);

            var firstRelay = result.TreePath.Count > 2 ? result.TreePath[1].Name : "the gateway";
            result.Explanation =
                $"{result.TreePath.Count - 1} hop(s) via {firstRelay} along the minimum spanning tree. Every relay merges the readings it forwards " +
                "into one frame per reporting round (the Part 1 TelemetryPacket<T> + aggregation), so a round costs the tree's total energy, " +
                $"and the MST makes that total as small as possible ({s.Tree.TotalEnergyMicroJoules / 1000:0.0} mJ vs {s.ShortestTreeWeight / 1000:0.0} mJ for Dijkstra's shortest-path tree). " +
                (result.StretchPercent <= 0.05
                    ? "This route is also Dijkstra's cheapest single path."
                    : $"For this one packet on its own, Dijkstra's path is cheaper ({result.ShortestPath.Count - 1} hops, {result.ShortestPathEnergyMicroJoules:0} µJ vs {result.TreePathEnergyMicroJoules:0} µJ). " +
                      "That is the latency-versus-fleet-energy trade-off a routing protocol has to make.");
            return result;
        }
    }

    private static RouteHop Hop(MeshState s, MeshVertex v, MeshVertex? previous)
    {
        var link = previous is null ? null : LinkBetween(previous, v);
        return new RouteHop
        {
            MacAddress = v.MacAddress,
            Name = v.Name,
            Kind = v.Kind,
            Subnet = v.Subnet,
            Status = s.Status[v.Index],
            LinkEnergyMicroJoules = link?.EnergyMicroJoules ?? 0,
            LinkRssi = link?.Rssi ?? 0
        };
    }

    private static MeshEdge? LinkBetween(MeshVertex a, MeshVertex b)
    {
        foreach (var link in a.Links)
            if (ReferenceEquals(link.Other(a), b)) return link;
        return null;
    }

    /// <summary>Adds the route the node used before the last self-healing event, when it was different.</summary>
    /// <summary>Adds the route the node used before its route last changed, if that differs from the current one.</summary>
    private void AddPreviousPath(RouteResult result, MeshVertex v)
    {
        if (!_previousRoutes.TryGetValue(v.Index, out var previous)) return;
        var now = result.TreePath.Select(h => h.MacAddress).ToList();
        if (previous.Path.Select(p => p.MacAddress).SequenceEqual(now)) return;
        result.PreviousPath = previous.Path.Select(p => p.Name).ToList();
        result.PreviousPathMacs = previous.Path.Select(p => p.MacAddress).ToList();
        result.ReroutedAt = previous.At;
        result.Rerouted = result.Reachable;
    }

    // ================================================================== BFS reachability

    /// <summary>
    /// Breadth-first search from the gateway, or from a subnet's border router confined to that subnet.
    /// Lists every device reached level by level, the ones that are down, and powered devices the search could not
    /// reach, with the neighbour each could hop through instead.
    /// </summary>
    /// <exception cref="ArgumentException">unknown subnet or root</exception>
    public ReachabilityResult Reachability(string? rootMac = null, string? subnet = null)
    {
        lock (_lock)
        {
            RefreshUnsafe(null);
            var s = _state!;
            var g = s.Graph;
            subnet = string.IsNullOrWhiteSpace(subnet) || subnet.Equals("all", StringComparison.OrdinalIgnoreCase) ? null : subnet.Trim();

            MeshVertex root;
            if (!string.IsNullOrWhiteSpace(rootMac))
                root = g.Find(GatewayState.NormaliseMac(rootMac)) ?? throw new ArgumentException($"No mesh node with MAC {rootMac}.");
            else if (subnet is not null)
                root = g.Vertices.FirstOrDefault(v => v.Kind == MeshNodeKind.Router && v.Subnet.Equals(subnet, StringComparison.OrdinalIgnoreCase))
                       ?? throw new ArgumentException($"Unknown subnet '{subnet}'.");
            else
                root = g.Gateway;

            bool InScope(MeshVertex v) => !ReferenceEquals(v, root) && v.Kind != MeshNodeKind.Gateway
                                          && (subnet is null || v.Subnet.Equals(subnet, StringComparison.OrdinalIgnoreCase));
            bool CanEnter(MeshVertex v) => s.IsUp(v) && (ReferenceEquals(v, root) || subnet is null || v.Subnet.Equals(subnet, StringComparison.OrdinalIgnoreCase));

            var sw = Stopwatch.StartNew();
            var bfs = GraphTraversal.BreadthFirst(g, root, CanEnter);
            sw.Stop();

            var result = new ReachabilityResult
            {
                RootMac = root.MacAddress,
                RootName = s.IsUp(root) ? root.Name : $"{root.Name} (down)",
                Subnet = subnet,
                ElapsedMicroseconds = Math.Round(sw.Elapsed.TotalMicroseconds, 1)
            };

            foreach (var level in bfs.Order.GroupBy(v => bfs.Depth[v.Index]).OrderBy(l => l.Key))
            {
                result.Levels.Add(new HopLevel
                {
                    Hop = level.Key,
                    Nodes = level.Select(v => Reach(s, v, bfs.Via[v.Index]?.Other(v))).ToList()
                });
            }

            foreach (var v in g.Vertices.Where(InScope))
            {
                result.InScope++;
                if (bfs.Reached(v)) { result.Reached++; continue; }
                if (!s.IsUp(v)) { result.Down.Add(Reach(s, v, null)); continue; }
                result.CutOff.Add(CutOff(s, v, root, subnet));
            }
            return result;
        }
    }

    private static ReachNode Reach(MeshState s, MeshVertex v, MeshVertex? via) => new()
    {
        MacAddress = v.MacAddress, Name = v.Name, Subnet = v.Subnet, Kind = v.Kind, Status = s.Status[v.Index], Via = via?.Name
    };

    /// <summary>Where a cut-off node could hop: the powered neighbour closest (in hops) to the gateway.</summary>
    private static CutOffNode CutOff(MeshState s, MeshVertex v, MeshVertex root, string? subnet)
    {
        var node = new CutOffNode { MacAddress = v.MacAddress, Name = v.Name, Subnet = v.Subnet };
        var best = v.Links
            .Select(l => (Vertex: l.Other(v), Link: l))
            .Where(t => s.Status[t.Vertex.Index] == MeshNodeStatus.Online)
            .OrderBy(t => s.Hops[t.Vertex.Index])
            .ThenBy(t => t.Link.EnergyMicroJoules)
            .Select(t => t.Vertex)
            .FirstOrDefault();

        if (best is not null)
        {
            node.HopVia = best.MacAddress;
            node.HopViaName = best.Name;
            node.HopViaSubnet = best.Subnet;
            node.Advice = subnet is not null && !best.Subnet.Equals(subnet, StringComparison.OrdinalIgnoreCase)
                ? $"{root.Name} cannot reach it inside {subnet}. Hop across to {best.Name} ({best.Subnet}), which reaches the gateway in {s.Hops[best.Index]} hop(s)."
                : $"Hop via {best.Name}, which reaches the gateway in {s.Hops[best.Index]} hop(s).";
        }
        else
        {
            var downNeighbours = v.Links.Select(l => l.Other(v)).Where(o => !s.IsUp(o)).Select(o => o.Name).Distinct().Take(3).ToList();
            node.Advice = downNeighbours.Count > 0
                ? $"No powered neighbour reaches the gateway. Restore {string.Join(" or ", downNeighbours)} to reconnect it."
                : "No neighbour in radio range reaches the gateway. Move the node or add a repeater.";
        }
        return node;
    }

    // ================================================================== MST report

    /// <summary>How the spanning tree was built: the first accepted links, cost, timing, and a cross-check with the other algorithm.</summary>
    public SpanningTreeReport BuildSpanningTreeReport(SpanningTreeAlgorithm algorithm, int steps = 25)
    {
        lock (_lock)
        {
            RefreshUnsafe(null);
            var g = _graph!;
            var s = _state!;
            bool IsUp(MeshVertex v) => s.IsUp(v);

            var tree = SpanningTrees.Build(algorithm, g, g.Gateway, IsUp);
            var other = SpanningTrees.Build(algorithm == SpanningTreeAlgorithm.Prim ? SpanningTreeAlgorithm.Kruskal : SpanningTreeAlgorithm.Prim, g, g.Gateway, IsUp);

            var report = new SpanningTreeReport
            {
                Algorithm = algorithm,
                TreeLinks = tree.Accepted.Count,
                TotalEnergyMicroJoules = tree.TotalEnergyMicroJoules,
                ComputeMicroseconds = tree.Microseconds,
                Operations = tree.Operations,
                OtherAlgorithmEnergyMicroJoules = other.TotalEnergyMicroJoules,
                Complexity = algorithm == SpanningTreeAlgorithm.Prim
                    ? "O(E log V): binary min-heap of candidate links, lazy deletion of stale entries"
                    : "O(E log E): sort all links once, then union–find (union by rank + path compression) rejects cycles"
            };

            double running = 0;
            foreach (var (link, i) in tree.Accepted.Take(Math.Clamp(steps, 1, 200)).Select((l, i) => (l, i)))
            {
                running += link.EnergyMicroJoules;
                report.FirstSteps.Add(new SpanningTreeStep
                {
                    Step = i + 1,
                    From = link.A.Name,
                    To = link.B.Name,
                    EnergyMicroJoules = link.EnergyMicroJoules,
                    RunningTotal = Math.Round(running, 2)
                });
            }
            return report;
        }
    }
}
