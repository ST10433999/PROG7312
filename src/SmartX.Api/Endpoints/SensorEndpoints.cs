using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Services;
using SmartX.Shared.Models;
using SmartX.Shared.Topology;

namespace SmartX.Api.Endpoints;

public static class SensorEndpoints
{
    public static RouteGroupBuilder MapSensorEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/sensors").WithTags("Sensors");

        g.MapGet("/", (GatewayState state, string? category, string? search, int page = 1, int pageSize = 50) =>
        {
            IEnumerable<SensorProfile> q = state.Sensors.Values;
            if (Enum.TryParse<SensorCategory>(category, true, out var cat)) q = q.Where(s => s.Category == cat);
            if (!string.IsNullOrWhiteSpace(search))
                q = q.Where(s => s.MacAddress.Contains(search, StringComparison.OrdinalIgnoreCase)
                              || s.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                              || s.DeploymentPathDisplay.Contains(search, StringComparison.OrdinalIgnoreCase));
            var list = q.OrderByDescending(s => s.RegisteredAt).ToList();
            pageSize = Math.Clamp(pageSize, 1, 500);
            return Results.Ok(new
            {
                total = list.Count, page, pageSize,
                items = list.Skip((page - 1) * pageSize).Take(pageSize)
            });
        }).WithSummary("List registered sensors (paged, filterable).");

        g.MapGet("/{id:guid}", Results<Ok<SensorProfile>, NotFound> (Guid id, GatewayState state) =>
            state.Sensors.Values.FirstOrDefault(s => s.Id == id) is { } s ? TypedResults.Ok(s) : TypedResults.NotFound());

        g.MapGet("/by-mac/{mac}", Results<Ok<SensorProfile>, NotFound> (string mac, GatewayState state) =>
            state.Sensors.TryGetValue(GatewayState.NormaliseMac(mac), out var s) ? TypedResults.Ok(s) : TypedResults.NotFound());

        g.MapPost("/", Results<Created<SensorProfile>, ValidationProblem, Conflict<ProblemDetails>> (SensorRegistrationRequest req, GatewayState state) =>
        {
            var errors = ValidateModel(req);
            if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

            var mac = GatewayState.NormaliseMac(req.MacAddress);
            if (state.Sensors.ContainsKey(mac))
                return TypedResults.Conflict(new ProblemDetails { Title = "Duplicate MAC", Detail = $"Sensor {mac} is already registered." });

            // Recursive placement validation against the deployment tree before accepting the record.
            var path = new List<string> { req.Facility, req.Zone };
            if (!string.IsNullOrWhiteSpace(req.SubZone)) path.Add(req.SubZone);
            var placement = TopologyValidator.ValidatePlacement(state.Topology, new NodePlacementRequest
                { MacAddress = mac, Path = path, Category = req.Category, PublishIntervalSeconds = req.PublishIntervalSeconds, RatedWatts = req.Category == SensorCategory.PowerConsumption ? 150 : 0 });
            var blocking = placement.Issues.Where(i => i.Severity >= Severity.High && i.Rule is not ("TierNotFound" or "UnknownRoot")).ToList();
            if (blocking.Count > 0)
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["deployment"] = blocking.Select(b => b.Message).ToArray() });

            var profile = state.Register(req);
            return TypedResults.Created($"/api/sensors/{profile.Id}", profile);
        }).WithSummary("Register a sensor (validated client-side and server-side with the same DataAnnotations).");

        g.MapDelete("/{id:guid}", (Guid id, GatewayState state) =>
        {
            var s = state.Sensors.Values.FirstOrDefault(x => x.Id == id);
            if (s is null) return Results.NotFound();
            state.Sensors.TryRemove(s.MacAddress, out _);
            state.Channels.TryRemove(s.MacAddress, out _);
            return Results.NoContent();
        });

        return api;
    }

    public static Dictionary<string, string[]> ValidateModel(object model)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        return results
            .SelectMany(r => r.MemberNames.DefaultIfEmpty(""), (r, m) => (m, r.ErrorMessage ?? "Invalid"))
            .GroupBy(x => x.m)
            .ToDictionary(x => x.Key, x => x.Select(y => y.Item2).ToArray());
    }
}
