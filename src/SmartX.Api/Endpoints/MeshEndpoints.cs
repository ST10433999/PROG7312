using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Mesh;
using SmartX.Shared.Mesh;

namespace SmartX.Api.Endpoints;

// Part 3 — /api/mesh – network topology, route tracking, BFS reachability, MST and self-healing.
public static class MeshEndpoints
{
    public static RouteGroupBuilder MapMeshEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/mesh").WithTags("Mesh Topology");

        g.MapGet("/", (MeshNetworkService mesh, SpanningTreeAlgorithm algorithm = SpanningTreeAlgorithm.Prim) =>
            Results.Ok(mesh.Snapshot(algorithm)))
            .WithSummary("Every node and link of the mesh graph, the minimum spanning tree and recent self-healing events.");

        g.MapGet("/route/{mac}", (string mac, MeshNetworkService mesh) =>
            mesh.Route(mac) is { } route
                ? Results.Ok(route)
                : Results.NotFound(new ProblemDetails { Title = $"No mesh node with MAC {mac}.", Status = 404 }))
            .WithSummary("Track a sensor: its hop-by-hop route to the gateway along the MST, Dijkstra's cheapest path, and any re-route.");

        g.MapGet("/reachability", (MeshNetworkService mesh, string? root, string? subnet) =>
        {
            try { return Results.Ok(mesh.Reachability(root, subnet)); }
            catch (ArgumentException ex) { return Results.BadRequest(new ProblemDetails { Title = ex.Message, Status = 400 }); }
        }).WithSummary("BFS from the gateway (or a subnet's router): devices reached per hop, devices down, and where cut-off devices can hop.");

        g.MapGet("/spanning-tree", (MeshNetworkService mesh, SpanningTreeAlgorithm algorithm = SpanningTreeAlgorithm.Prim, int steps = 25) =>
            Results.Ok(mesh.BuildSpanningTreeReport(algorithm, steps)))
            .WithSummary("How Prim's or Kruskal's algorithm built the minimum spanning tree, with a cross-check against the other.");

        g.MapGet("/healing", (MeshNetworkService mesh, int take = 10) => Results.Ok(mesh.Healing(take)))
            .WithSummary("Recent self-healing events (newest first).");

        g.MapPost("/nodes/{mac}/fail", (string mac, MeshNetworkService mesh) => ChangeState(mesh, mac, failed: true))
            .WithSummary("Simulate a hardware failure: take the node offline and let the mesh re-route around it.");

        g.MapPost("/nodes/{mac}/restore", (string mac, MeshNetworkService mesh) => ChangeState(mesh, mac, failed: false))
            .WithSummary("Bring a failed node back online.");

        g.MapPost("/fail-busiest", (MeshNetworkService mesh) =>
            mesh.FailBusiest() is { } change ? Results.Ok(change) : Results.NotFound(new ProblemDetails { Title = "No node left to fail.", Status = 404 }))
            .WithSummary("Fail the relay that carries the most downstream traffic (the worst single failure).");

        g.MapPost("/restore-all", (MeshNetworkService mesh) => Results.Ok(new { healing = mesh.RestoreAll() }))
            .WithSummary("Restore every node taken offline by the operator.");

        return api;
    }

    private static IResult ChangeState(MeshNetworkService mesh, string mac, bool failed)
    {
        try
        {
            return mesh.SetFailed(mac, failed) is { } change
                ? Results.Ok(change)
                : Results.NotFound(new ProblemDetails { Title = $"No mesh node with MAC {mac}.", Status = 404 });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new ProblemDetails { Title = ex.Message, Status = 400 });
        }
    }
}
