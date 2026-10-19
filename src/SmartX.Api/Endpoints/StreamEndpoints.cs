using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Services;
using SmartX.Shared.Commands;
using SmartX.Shared.Models;

namespace SmartX.Api.Endpoints;

// Part 2 — /api/stream – devices publish telemetry (FIFO or PriorityQueue routing); queue depth; timeline.
public static class StreamEndpoints
{
    public const int MaxBatch = 2000;

    public static RouteGroupBuilder MapStreamEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/stream").WithTags("Telemetry Stream");

        g.MapPost("/telemetry", (TelemetryIngestRequest req, GatewayState gateway, CommandStreamState stream) =>
        {
            var errors = ValidateReading(req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            if (PayloadMismatch(req, gateway) is { } why)
                return Results.BadRequest(new ProblemDetails { Title = "Payload rejected", Detail = why, Status = 400 });

            var result = StreamRouter.Route(req, gateway, stream);
            return result is null
                ? Results.NotFound(new ProblemDetails { Title = $"Sensor {GatewayState.NormaliseMac(req.MacAddress)} is not registered.", Status = 404 })
                : Results.Accepted(value: result);
        }).WithSummary("A device publishes one reading. Normal readings join the FIFO Queue; critical ones bypass it via the PriorityQueue.");

        g.MapPost("/telemetry/batch", (List<TelemetryIngestRequest> batch, GatewayState gateway, CommandStreamState stream) =>
        {
            if (batch.Count > MaxBatch)
                return Results.BadRequest(new ProblemDetails { Title = $"A batch may contain at most {MaxBatch} readings.", Status = 400 });

            var summary = new StreamBatchResult();
            foreach (var reading in batch)
            {
                if (ValidateReading(reading).Count > 0 || PayloadMismatch(reading, gateway) is not null) { summary.Rejected++; continue; }
                var routed = StreamRouter.Route(reading, gateway, stream);
                switch (routed?.Route)
                {
                    case null: summary.Rejected++; break;
                    case StreamRoute.Fifo: summary.Fifo++; break;
                    case StreamRoute.Priority: summary.Priority++; break;
                    case StreamRoute.SuppressedDuplicate: summary.SuppressedDuplicates++; break;
                }
            }
            summary.TelemetryQueueDepth = stream.TelemetryQueueDepth;
            return Results.Accepted(value: summary);
        }).WithSummary("A burst of readings, routed one by one.");

        g.MapGet("/queue", (CommandStreamState stream) => Results.Ok(new
        {
            telemetryQueueDepth = stream.TelemetryQueueDepth,
            pendingCriticalAlerts = stream.PendingCriticalCount,
            commandQueueDepth = stream.CommandQueueDepth,
            processed = stream.TelemetryProcessed,
            criticalProcessed = stream.CriticalProcessed,
            rejected = stream.TelemetryRejected
        })).WithSummary("Live depth of the FIFO, PriorityQueue and command queue.");

        g.MapGet("/timeline", (CommandStreamState stream, string? mac, int take = 120) =>
            Results.Ok(stream.Timeline(mac, take)))
            .WithSummary("Processed readings in timestamp order from the SortedDictionary (optionally for one sensor).");

        g.MapGet("/timeline/sensors", (CommandStreamState stream) => Results.Ok(stream.TimelineSensors()))
            .WithSummary("Sensors present in the timeline, most recently active first.");

        return api;
    }

    /// <summary>Same no-coercion rule as Part 1: the packet kind must match the sensor's registered payload.</summary>
    private static string? PayloadMismatch(TelemetryIngestRequest req, GatewayState gateway)
    {
        if (!gateway.Sensors.TryGetValue(GatewayState.NormaliseMac(req.MacAddress), out var sensor)) return null;   // 404 is reported by the router
        if (req.Kind != sensor.Payload)
            return $"Sensor {sensor.MacAddress} publishes {sensor.Payload} packets; received {req.Kind}.";
        if (req.Kind == PayloadKind.Int && req.Value is { } v && v != Math.Floor(v))
            return $"Sensor {sensor.MacAddress} publishes integer packets; {v} has a fractional part.";
        if (req.Kind == PayloadKind.Bool && req.State is null)
            return $"Sensor {sensor.MacAddress} publishes boolean packets; 'state' is required.";
        return null;
    }

    private static Dictionary<string, string[]> ValidateReading(TelemetryIngestRequest req)
    {
        var errors = SensorEndpoints.ValidateModel(req);
        if (req.Kind == PayloadKind.Bool ? req.State is null && req.Value is null : req.Value is null)
            errors["value"] = new[] { "A value (or state for Bool) is required." };
        return errors;
    }
}
