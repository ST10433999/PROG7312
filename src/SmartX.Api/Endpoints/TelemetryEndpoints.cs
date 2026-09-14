using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Services;
using SmartX.Shared.Collections;
using SmartX.Shared.Models;
using SmartX.Shared.Telemetry;

namespace SmartX.Api.Endpoints;

public static class TelemetryEndpoints
{
    public static RouteGroupBuilder MapTelemetryEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/telemetry").WithTags("Telemetry");

        g.MapPost("/", (TelemetryIngestRequest req, GatewayState state) =>
        {
            var errors = SensorEndpoints.ValidateModel(req);
            if (req.Kind == PayloadKind.Bool ? req.State is null && req.Value is null : req.Value is null)
                errors["value"] = new[] { "A value (or state for Bool) is required." };
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            try { return Results.Ok(state.Ingest(req)); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new ProblemDetails { Title = ex.Message }); }
        }).WithSummary("Ingest one reading. The API wraps it in TelemetryPacket<float|int|bool> and scores it against the node's baseline.");

        g.MapPost("/batch", (List<TelemetryIngestRequest> batch, GatewayState state) =>
        {
            var results = new List<TelemetryIngestResult>(batch.Count);
            var failed = 0;
            foreach (var r in batch)
            {
                try { results.Add(state.Ingest(r)); } catch { failed++; }
            }
            return Results.Ok(new { accepted = results.Count, failed, results });
        }).WithSummary("Ingest a burst of readings.");

        g.MapGet("/{mac}/recent", (string mac, GatewayState state) =>
        {
            if (!state.Channels.TryGetValue(GatewayState.NormaliseMac(mac), out var ch)) return Results.NotFound();
            return Results.Ok(new
            {
                ch.Profile.MacAddress, ch.Profile.Name, ch.Profile.Payload, ch.Profile.Unit, ch.State,
                zScore = Math.Round(ch.LastZScore, 2), mean = Math.Round(ch.WindowMean, 3), stdDev = Math.Round(ch.WindowStdDev, 3),
                window = ch.WindowSize, ch.LastSeen, values = ch.Sparkline()
            });
        }).WithSummary("Rolling window (custom ring buffer) for one node.");

        g.MapGet("/{mac}/history", (string mac, GatewayState state) =>
        {
            mac = GatewayState.NormaliseMac(mac);
            if (!state.Sensors.TryGetValue(mac, out var s)) return Results.NotFound();
            object history = s.Payload switch
            {
                PayloadKind.Float => state.EnvironmentalHistory.FlattenFor(mac).Select(p => new { p.Timestamp, value = (double)p.Value }),
                PayloadKind.Int => state.PowerHistory.FlattenFor(mac).Select(p => new { p.Timestamp, value = (double)p.Value }),
                _ => state.ActuatorHistory.FlattenFor(mac).Select(p => new { p.Timestamp, value = p.Value ? 1d : 0d })
            };
            return Results.Ok(history);
        }).WithSummary("Historical packets for one node, flattened from the jagged batch store into a List<T>.");

        g.MapGet("/batches", (GatewayState state) => Results.Ok(new
        {
            environmental = Describe(state.EnvironmentalHistory),
            power = Describe(state.PowerHistory),
            actuator = Describe(state.ActuatorHistory)
        })).WithSummary("Shape of the jagged historical arrays and timing of the transfer into List<T>.");

        g.MapGet("/aggregate-demo", (GatewayState state, string? left, string? right) =>
        {
            // Operator overloading in action: Meter3 = Meter1 + Meter2
            var meters = state.Channels.Values.OfType<NodeChannel<int>>().Where(c => c.Window.Latest is not null).Take(2).ToList();
            NodeChannel<int>? a = null, b = null;
            if (left is not null && state.Channels.TryGetValue(GatewayState.NormaliseMac(left), out var lc)) a = lc as NodeChannel<int>;
            if (right is not null && state.Channels.TryGetValue(GatewayState.NormaliseMac(right), out var rc)) b = rc as NodeChannel<int>;
            a ??= meters.ElementAtOrDefault(0);
            b ??= meters.ElementAtOrDefault(1);
            if (a is null || b is null) return Results.NotFound(new ProblemDetails { Title = "Need two power meters with data." });

            var m1 = a.Window.Latest!.Value;
            var m2 = b.Window.Latest!.Value;
            var sum = m1 + m2;          // operator +
            var delta = m1 - m2;        // operator -
            var gt = m1 > m2;           // operator >

            return Results.Ok(new AggregateDemoResult
            {
                Expression = "Meter3 = Meter1 + Meter2;  Delta = Meter1 - Meter2;  Meter1 > Meter2",
                LeftPacket = m1.ToString(), RightPacket = m2.ToString(),
                SumPacket = sum.ToString(), DeltaPacket = delta.ToString(),
                LeftGreaterThanRight = gt, PayloadType = sum.PayloadType
            });
        }).WithSummary("Demonstrates TelemetryPacket<int> operator overloading on two live smart meters.");

        return api;
    }

    private static BatchStoreStats Describe<T>(HistoricalBatchStore<T> store) where T : struct
    {
        var sw = Stopwatch.StartNew();
        var list = store.Flatten();
        sw.Stop();
        var grid = store.HourlyGrid();
        var rows = new double[24][];
        for (var h = 0; h < 24; h++) rows[h] = new[] { Math.Round(grid[h, 0], 2), Math.Round(grid[h, 1], 2), Math.Round(grid[h, 2], 2) };
        return new BatchStoreStats
        {
            BatchCount = store.BatchCount, TotalPackets = store.TotalPackets, BatchLengths = store.BatchLengths,
            FlattenedListCount = list.Count, FlattenMilliseconds = Math.Round(sw.Elapsed.TotalMilliseconds, 3), HourlyGrid = rows
        };
    }
}
