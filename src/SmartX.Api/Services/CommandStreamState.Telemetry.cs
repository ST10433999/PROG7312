using SmartX.Shared.Models;

namespace SmartX.Api.Services;

// Part 2 — FIFO queue of incoming telemetry packets (Rubric: Message Queue).
public sealed partial class CommandStreamState
{
    /// <summary>FIFO of incoming telemetry packets awaiting processing by the CommandWorker.</summary>
    private readonly Queue<TelemetryIngestRequest> _telemetryQueue = new();

    private long _telemetryRejected;

    public int TelemetryQueueDepth { get { lock (_lock) return _telemetryQueue.Count; } }
    public long TelemetryRejected { get { lock (_lock) return _telemetryRejected; } }

    /// <summary>Appends a normal packet to the back of the queue and returns its position.</summary>
    public int EnqueueTelemetry(TelemetryIngestRequest reading)
    {
        lock (_lock)
        {
            _telemetryQueue.Enqueue(reading);
            return _telemetryQueue.Count;
        }
    }

    /// <summary>Takes the oldest packet, or null when the queue is empty.</summary>
    public TelemetryIngestRequest? DequeueTelemetry()
    {
        lock (_lock) return _telemetryQueue.TryDequeue(out var r) ? r : null;
    }

    public void CountTelemetryRejected()
    {
        lock (_lock) _telemetryRejected++;
    }
}
