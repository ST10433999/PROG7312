using SmartX.Shared.Commands;
using SmartX.Shared.Models;

namespace SmartX.Api.Services;

// -------------------------------------------------------------------------------------------------
// Part 2 — the stream processor.
//
// Every tick (250 ms) it:
//   1. drains the PriorityQueue completely – critical alerts are always handled first;
//   2. processes up to Stream:TelemetryPerTick packets from the FIFO Queue (default 8, i.e. 32/s),
//      so a burst builds a visible backlog that critical packets can be seen to overtake;
//   3. executes every queued operator command.
// -------------------------------------------------------------------------------------------------

public sealed class CommandWorker(
    CommandStreamState stream,
    PredictiveEngine engine,
    GatewayState gateway,
    IConfiguration config,
    ILogger<CommandWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var perTick = Math.Clamp(config.GetValue("Stream:TelemetryPerTick", 8), 1, 10_000);
        logger.LogInformation("CommandWorker started: PriorityQueue first, then up to {PerTick} FIFO packets per 250 ms tick.", perTick);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                // 1. Critical alerts bypass everything waiting in the FIFO.
                while (stream.DequeueCritical() is { } alert)
                    ProcessAlert(alert);

                // 2. Normal telemetry, throttled.
                for (var i = 0; i < perTick && stream.DequeueTelemetry() is { } reading; i++)
                    IngestReading(reading, critical: false);

                // 3. Operator commands.
                while (stream.DequeueCommand() is { } envelope)
                    ExecuteCommand(envelope);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "CommandWorker tick failed.");
            }
        }
    }

    private void ProcessAlert(CriticalAlert alert)
    {
        // Record how many normal packets this alert overtook, then apply its reading immediately.
        stream.MarkAlertProcessed(alert, stream.TelemetryQueueDepth);
        if (alert.Reading is not null) IngestReading(alert.Reading, critical: true);

        if (!string.IsNullOrWhiteSpace(alert.ConditionKey))
            engine.ObserveCondition(alert.ConditionKey, $"{alert.DeviceName}: {alert.Title}", alert.MacAddress, alert.DeviceName, alert.RaisedAt);

        logger.LogInformation("[PriorityQueue] {Severity} '{Title}' on {Mac} processed after {Latency} ms, ahead of {Ahead} queued packet(s).",
            alert.Severity, alert.Title, alert.MacAddress, alert.LatencyMs, alert.QueuedAheadAtProcess);
    }

    private void IngestReading(TelemetryIngestRequest reading, bool critical)
    {
        try
        {
            var result = gateway.Ingest(reading);          // Part 1 pipeline: TelemetryPacket<T>, ring buffer, z-score, triage
            var name = gateway.Sensors.TryGetValue(result.MacAddress, out var sensor) ? sensor.Name : result.MacAddress;
            double value;
            if (reading.Kind == PayloadKind.Bool)
            {
                var on = reading.State ?? (reading.Value is { } raw && raw != 0);
                value = on ? 1d : 0d;
            }
            else
            {
                value = reading.Value ?? 0d;
            }

            // A repeat breach whose alert the HashSet suppressed arrives through the FIFO, so classify again.
            var breach = sensor is not null && TelemetryRules.Classify(sensor, reading) is not null;

            stream.AddTimelinePoint(new TimelinePoint
            {
                Timestamp = reading.Timestamp ?? DateTimeOffset.UtcNow,
                MacAddress = result.MacAddress,
                Name = name,
                Value = value,
                Critical = critical || breach
            });

            // A normal reading from a node with a threshold fault means the condition has cleared.
            if (!critical && !breach)
                stream.ClearFault(result.MacAddress, reasonPrefix: "threshold");
        }
        catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException)
        {
            stream.CountTelemetryRejected();
            logger.LogDebug("Rejected streamed packet for {Mac}: {Reason}", reading.MacAddress, ex.Message);
        }
    }

    private void ExecuteCommand(CommandEnvelope envelope)
    {
        try
        {
            var batch = stream.ProcessCommand(envelope);
            foreach (var record in batch.Records)
                engine.ObserveCommand(record.MacAddress, record.DeviceName, record.Command);
        }
        catch (KeyNotFoundException ex)
        {
            logger.LogWarning("Dropped command '{Label}': {Reason}", envelope.Label, ex.Message);
        }
    }
}
