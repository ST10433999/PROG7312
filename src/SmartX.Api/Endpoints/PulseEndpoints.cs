using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Services;
using SmartX.Shared.Models;
using SmartX.Shared.Topology;

namespace SmartX.Api.Endpoints;

/// <summary>Pulse Board (engagement feature), triage feed and topology validation.</summary>
public static class PulseEndpoints
{
    public static RouteGroupBuilder MapPulseEndpoints(this RouteGroupBuilder api)
    {
        var pulse = api.MapGroup("/pulse").WithTags("Pulse Board");

        pulse.MapGet("/", (GatewayState state, int max = 400) => Results.Ok(state.BuildFleetPulse(Math.Clamp(max, 1, 5000))))
             .WithSummary("Fleet health snapshot: every node's state, z-score and sparkline, ordered worst-first.");

        var triage = api.MapGroup("/triage").WithTags("Triage");

        triage.MapGet("/", (GatewayState state, bool includeAcknowledged = false, int take = 50) =>
            Results.Ok(state.TriageFeed(includeAcknowledged, Math.Clamp(take, 1, 500))));

        triage.MapPost("/", (ReportIssueRequest req, GatewayState state) =>
        {
            var errors = SensorEndpoints.ValidateModel(req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var mac = GatewayState.NormaliseMac(req.MacAddress);
            if (!state.Sensors.TryGetValue(mac, out var sensor))
                return Results.NotFound(new ProblemDetails { Title = $"Sensor {mac} is not registered." });

            var item = state.AddTriage(new TriageItem
            {
                Id = Guid.NewGuid(), Severity = req.Severity, Source = TriageSource.User,
                MacAddress = mac, Location = sensor.DeploymentPathDisplay,
                Title = req.Title.Trim(), Detail = req.Detail.Trim(), CreatedAt = DateTimeOffset.UtcNow
            });
            return Results.Created($"/api/triage/{item.Id}", item);
        }).WithSummary("Operator reports an issue against a node; it joins the severity-ranked feed.");

        triage.MapPost("/{id:guid}/acknowledge", (Guid id, GatewayState state, string by = "operator") =>
            state.Acknowledge(id, by) is { } item ? Results.Ok(item) : Results.NotFound());

        var topo = api.MapGroup("/topology").WithTags("Topology");

        topo.MapGet("/", (GatewayState state) => Results.Ok(state.Topology))
            .WithSummary("Deployment tree: Facility → Zone → Sub-Zone → Rack → sensors.");

        topo.MapGet("/validate", (GatewayState state) =>
        {
            var result = TopologyValidator.ValidateTree(state.Topology, state.Sensors);
            result.Trace = result.Trace.Take(200).ToList();   // keep the payload readable
            return Results.Ok(result);
        }).WithSummary("Recursively validate the entire hierarchy (inherited constraints, quotas, budgets).");

        topo.MapPost("/validate-placement", (NodePlacementRequest req, GatewayState state) =>
            Results.Ok(TopologyValidator.ValidatePlacement(state.Topology, req)))
            .WithSummary("Recursively check that a node may be configured at e.g. Facility A → Zone 1 → Sub-Zone B.");

        topo.MapGet("/path/{mac}", (string mac, GatewayState state) =>
            TopologyValidator.FindPath(state.Topology, GatewayState.NormaliseMac(mac)) is { } p ? Results.Ok(p) : Results.NotFound());

        return api;
    }
}
