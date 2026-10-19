using SmartX.Shared.Commands;
using SmartX.Shared.Models;

namespace SmartX.Api.Services;

// Part 2 — decides, on arrival, whether a telemetry packet joins the standard FIFO or jumps it.

/// <summary>Routes incoming packets: normal → Queue&lt;T&gt;, critical → PriorityQueue, repeat critical → suppressed by the HashSet.</summary>
public static class StreamRouter
{
    /// <summary>Returns null when the MAC is not a registered sensor.</summary>
    public static StreamIngestResult? Route(TelemetryIngestRequest reading, GatewayState gateway, CommandStreamState stream)
    {
        var mac = GatewayState.NormaliseMac(reading.MacAddress);
        if (!gateway.Sensors.TryGetValue(mac, out var sensor)) return null;     // O(1) dictionary lookup

        reading.MacAddress = mac;
        reading.Timestamp ??= DateTimeOffset.UtcNow;

        var hit = TelemetryRules.Classify(sensor, reading);
        if (hit is { } rule)
        {
            var alert = new CriticalAlert
            {
                Id = Guid.NewGuid(),
                MacAddress = mac,
                DeviceName = sensor.Name,
                Severity = rule.Severity,
                Source = AlertSource.Telemetry,
                Title = rule.Title,
                Detail = $"{sensor.Name} · {sensor.DeploymentPathDisplay}",
                RaisedAt = DateTimeOffset.UtcNow,
                Reading = reading,
                ConditionKey = $"{mac}|{rule.Kind}"
            };

            if (stream.TryRaiseCritical(alert, $"threshold: {rule.Title}", isDevice: false))
            {
                return new StreamIngestResult
                {
                    MacAddress = mac,
                    Route = StreamRoute.Priority,
                    AlertId = alert.Id,
                    TelemetryQueueDepth = stream.TelemetryQueueDepth,
                    PendingCriticalAlerts = stream.PendingCriticalCount,
                    Message = $"{rule.Title} – bypassed {stream.TelemetryQueueDepth} queued packet(s) via the PriorityQueue."
                };
            }

            // The node is already in the active-fault HashSet: no second alert, but the reading itself
            // is still data and goes through the normal FIFO.
            var depthAfterDuplicate = stream.EnqueueTelemetry(reading);
            return new StreamIngestResult
            {
                MacAddress = mac,
                Route = StreamRoute.SuppressedDuplicate,
                TelemetryQueueDepth = depthAfterDuplicate,
                PendingCriticalAlerts = stream.PendingCriticalCount,
                Message = $"{sensor.Name} already has an active fault – duplicate alert suppressed by the HashSet; reading queued normally."
            };
        }

        var depth = stream.EnqueueTelemetry(reading);
        return new StreamIngestResult
        {
            MacAddress = mac,
            Route = StreamRoute.Fifo,
            TelemetryQueueDepth = depth,
            PendingCriticalAlerts = stream.PendingCriticalCount,
            Message = $"Queued at position {depth} in the FIFO."
        };
    }
}
