using SmartX.Api.Services;
using SmartX.Shared.Commands;

namespace SmartX.Api.Endpoints;

// Part 2 — /api/alerts – the critical-alert PriorityQueue.
public static class AlertEndpoints
{
    public static RouteGroupBuilder MapAlertEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/alerts").WithTags("Critical Alerts");

        g.MapGet("/pending", (CommandStreamState stream, int take = 10) => Results.Ok(stream.PendingAlerts(Math.Clamp(take, 1, 100))))
            .WithSummary("Alerts still waiting in the PriorityQueue, in processing order.");

        g.MapGet("/recent", (CommandStreamState stream, int take = 10) => Results.Ok(stream.RecentAlerts(Math.Clamp(take, 1, 50))))
            .WithSummary("Recently processed alerts with latency and the number of FIFO packets they overtook.");

        g.MapPost("/critical", (CriticalAlert alert, CommandStreamState stream) =>
        {
            if (string.IsNullOrWhiteSpace(alert.MacAddress) || string.IsNullOrWhiteSpace(alert.Title))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["alert"] = new[] { "macAddress and title are required." } });

            var device = stream.Find(alert.MacAddress);
            if (string.IsNullOrWhiteSpace(alert.DeviceName)) alert.DeviceName = device?.Name ?? alert.MacAddress;
            if (string.IsNullOrWhiteSpace(alert.ConditionKey)) alert.ConditionKey = $"{CommandStreamState.NormaliseMac(alert.MacAddress)}|operator-alert";
            alert.Reading = null;      // operator alerts never carry telemetry
            alert.Id = Guid.NewGuid();
            alert.RaisedAt = DateTimeOffset.UtcNow;

            var accepted = stream.TryRaiseCritical(alert, $"operator: {alert.Title}", isDevice: device is not null);
            return Results.Ok(new
            {
                accepted,
                duplicateSuppressed = !accepted,
                alertId = accepted ? alert.Id : (Guid?)null,
                pendingCriticalAlerts = stream.PendingCriticalCount
            });
        }).WithSummary("Raise a critical alert. Dropped (and counted) if the node is already in the active-fault HashSet.");

        g.MapPost("/{id:guid}/acknowledge", (Guid id, CommandStreamState stream) =>
            stream.AcknowledgeAlert(id) is { } alert ? Results.Ok(alert) : Results.NotFound())
            .WithSummary("Mark a processed alert as dispatched.");

        return api;
    }
}
