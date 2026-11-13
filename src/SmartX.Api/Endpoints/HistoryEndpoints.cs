using Microsoft.AspNetCore.Mvc;
using SmartX.Api.History;
using SmartX.Api.Services;
using SmartX.Shared.History;

namespace SmartX.Api.Endpoints;

// Part 3 — /api/history – timestamp range queries over the per-sensor AVL trees.
public static class HistoryEndpoints
{
    public static RouteGroupBuilder MapHistoryEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/history").WithTags("Historical Telemetry");

        g.MapGet("/stats", (TelemetryHistoryIndex index) => Results.Ok(index.Stats()))
            .WithSummary("Size, height and rotations of the AVL history index.");

        g.MapGet("/benchmark", (int points = 200_000, int queries = 200, double windowHours = 2) =>
            Results.Ok(HistoryBenchmark.Run(points, queries, windowHours)))
            .WithSummary("Times AVL range queries against a linear scan over the same readings (up to 2,000,000).");

        g.MapGet("/{mac}", (string mac, GatewayState gateway, TelemetryHistoryIndex index,
            DateTimeOffset? from, DateTimeOffset? to, int skip = 0, int take = 100,
            string order = "desc", int bucketMinutes = 60, bool compare = false) =>
        {
            mac = GatewayState.NormaliseMac(mac);
            var end = to ?? DateTimeOffset.UtcNow;
            var start = from ?? end.AddHours(-24);
            if (start > end)
                return Results.BadRequest(new ProblemDetails { Title = "'from' must be earlier than 'to'.", Status = 400 });
            if ((end - start).TotalDays > 366)
                return Results.BadRequest(new ProblemDetails { Title = "The range may span at most 366 days.", Status = 400 });

            var result = index.Query(mac, start, end, skip, take,
                newestFirst: !order.Equals("asc", StringComparison.OrdinalIgnoreCase),
                bucketMinutes: Math.Clamp(bucketMinutes, 0, 1440), compareLinear: compare);
            if (result is null)
                return Results.NotFound(new ProblemDetails { Title = $"No history for {mac}.", Status = 404 });

            result.Name = gateway.Sensors.TryGetValue(mac, out var sensor) ? sensor.Name : mac;
            return Results.Ok(result);
        }).WithSummary("Readings for one sensor between 'from' and 'to' (ISO 8601), paged, with hourly min/avg/max buckets and query cost.");

        return api;
    }
}
