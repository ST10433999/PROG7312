using Microsoft.Extensions.Logging.Abstractions;
using SmartX.Api.Mesh;
using SmartX.Api.Services;
using SmartX.Shared.Mesh;

namespace SmartX.Tests;

/// <summary>Part 3 — the live mesh built from the seeded Part 1 fleet: routing, BFS reachability and self-healing.</summary>
public class MeshNetworkServiceTests
{
    private static MeshNetworkService CreateMesh(int sensors = 240)
    {
        var gateway = new GatewayState();
        MockDataSeeder.Seed(gateway, sensors, NullLogger.Instance);
        // The heartbeat would mark nodes silent once the test runs longer than a few publish intervals.
        return new MeshNetworkService(gateway, NullLogger<MeshNetworkService>.Instance) { UseHeartbeat = false };
    }

    [Fact]
    public void Every_sensor_starts_online_with_a_route_to_the_gateway()
    {
        var mesh = CreateMesh();
        var snapshot = mesh.Snapshot();

        Assert.Equal(240 + 1 + 4, snapshot.Stats.Vertices);           // sensors + gateway + 4 zone routers
        Assert.Equal(snapshot.Stats.Vertices, snapshot.Stats.Online);
        Assert.Equal(1, snapshot.Stats.Components);
        Assert.Equal(snapshot.Stats.Vertices - 1, snapshot.Stats.TreeLinks);
        Assert.True(snapshot.Stats.TreeEnergyMicroJoules < snapshot.Stats.AllLinksEnergyMicroJoules);
        Assert.All(snapshot.Nodes.Where(n => n.Kind != MeshNodeKind.Gateway), n => Assert.NotNull(n.NextHop));
    }

    [Fact]
    public void Mst_is_cheapest_per_round_with_aggregation_and_dijkstra_without()
    {
        var stats = CreateMesh().Snapshot().Stats;
        // Aggregated: a round costs the routing tree's weight, and no spanning tree is lighter than the MST.
        Assert.True(stats.TreeEnergyMicroJoules <= stats.ShortestPathTreeEnergyMicroJoules + 0.01);
        // Un-aggregated: every packet travels alone, and Dijkstra's routes are each the cheapest possible.
        Assert.True(stats.ShortestRoutesEnergyMicroJoules <= stats.TreeRoutesEnergyMicroJoules + 0.01);
    }

    [Fact]
    public void Prim_and_Kruskal_agree_on_total_energy()
    {
        var mesh = CreateMesh();
        var prim = mesh.Snapshot(SpanningTreeAlgorithm.Prim).Stats.TreeEnergyMicroJoules;
        var kruskal = mesh.Snapshot(SpanningTreeAlgorithm.Kruskal).Stats.TreeEnergyMicroJoules;
        Assert.Equal(prim, kruskal, 1);

        var report = mesh.BuildSpanningTreeReport(SpanningTreeAlgorithm.Kruskal);
        Assert.Equal(report.TotalEnergyMicroJoules, report.OtherAlgorithmEnergyMicroJoules, 1);
        Assert.NotEmpty(report.FirstSteps);
    }

    [Fact]
    public void Route_runs_from_sensor_to_gateway_and_tree_path_is_never_cheaper_than_dijkstra()
    {
        var mesh = CreateMesh();
        var snapshot = mesh.Snapshot();
        foreach (var sensor in snapshot.Nodes.Where(n => n.Kind == MeshNodeKind.Sensor).Take(40))
        {
            var route = mesh.Route(sensor.MacAddress)!;
            Assert.True(route.Reachable);
            Assert.Equal(sensor.MacAddress, route.TreePath[0].MacAddress);
            Assert.Equal(snapshot.GatewayMac, route.TreePath[^1].MacAddress);
            Assert.Equal(snapshot.GatewayMac, route.ShortestPath[^1].MacAddress);
            Assert.True(route.ShortestPathEnergyMicroJoules <= route.TreePathEnergyMicroJoules + 0.01);
        }
        Assert.Null(mesh.Route("FF:FF:FF:FF:FF:FF"));
    }

    [Fact]
    public void Failing_the_busiest_relay_heals_the_mesh_and_restore_undoes_it()
    {
        var mesh = CreateMesh();
        var before = mesh.Snapshot();

        var change = mesh.FailBusiest()!;
        Assert.True(change.Failed);
        Assert.NotNull(change.Healing);
        Assert.True(change.Healing!.Rerouted > 0, "nodes that hopped through the failed relay must get a new uplink");

        var after = mesh.Snapshot();
        Assert.Equal(1, after.Stats.Failed);
        Assert.Equal(before.Stats.Vertices - 1, after.Stats.Online + after.Stats.Isolated);
        Assert.DoesNotContain(after.Nodes, n => n.NextHop == change.MacAddress);   // nobody routes through it any more

        var rerouted = change.Healing.Changes[0].MacAddress;
        var route = mesh.Route(rerouted)!;
        Assert.True(route.Rerouted);
        Assert.NotEmpty(route.PreviousPath);

        mesh.SetFailed(change.MacAddress, failed: false);
        Assert.Equal(before.Stats.Online, mesh.Snapshot().Stats.Online);
    }

    [Fact]
    public void Router_failure_cuts_its_subnet_off_from_the_router_but_bfs_shows_where_to_hop()
    {
        var mesh = CreateMesh();
        var router = mesh.Snapshot().Nodes.First(n => n.Kind == MeshNodeKind.Router && n.Subnet == "Zone 1");
        mesh.SetFailed(router.MacAddress, failed: true);

        // From the gateway, Zone 1 sensors are still reached through neighbouring zones (self-healing).
        var global = mesh.Reachability();
        Assert.Contains(global.Down, d => d.MacAddress == router.MacAddress);

        // Confined to Zone 1 with its router down, nothing is reached from the router, and every powered
        // sensor gets advice on which neighbour to hop through instead.
        var subnet = mesh.Reachability(subnet: "Zone 1");
        Assert.Equal(0, subnet.Reached);
        Assert.NotEmpty(subnet.CutOff);
        Assert.Contains(subnet.CutOff, c => c.HopVia is not null);
        Assert.Throws<ArgumentException>(() => mesh.Reachability(subnet: "Zone 99"));
    }

    [Fact]
    public void Gateway_cannot_be_failed_and_unknown_nodes_return_null()
    {
        var mesh = CreateMesh();
        Assert.Throws<InvalidOperationException>(() => mesh.SetFailed(MeshTopologyBuilder.GatewayMac, true));
        Assert.Null(mesh.SetFailed("AA:BB:CC:DD:EE:FF", true));
    }

    [Fact]
    public void Critical_nodes_are_articulation_points()
    {
        var mesh = CreateMesh();
        var critical = mesh.Snapshot().Nodes.Where(n => n.Critical).ToList();
        foreach (var node in critical.Take(5))
        {
            mesh.SetFailed(node.MacAddress, true);
            var stats = mesh.Snapshot().Stats;
            Assert.True(stats.Isolated > 0 || stats.Components > 1, $"failing {node.Name} should cut something off");
            mesh.SetFailed(node.MacAddress, false);
        }
    }
}
