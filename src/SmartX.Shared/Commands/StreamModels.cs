using System.Text.Json.Serialization;

namespace SmartX.Shared.Commands;

// -------------------------------------------------------------------------------------------------
// Part 2 — Real-Time Command Stream & History. Telemetry stream routing and timeline.
// Shared by the API and the Blazor client, so the same DataAnnotations validate on both sides.
// -------------------------------------------------------------------------------------------------

/// <summary>Which path an incoming telemetry packet took through the stream.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StreamRoute
{
    /// <summary>Normal packet – appended to the FIFO Queue&lt;T&gt;.</summary>
    Fifo,
    /// <summary>Critical packet – bypassed the FIFO via the PriorityQueue.</summary>
    Priority,
    /// <summary>Critical packet for a node already in the fault HashSet – no second alert; reading still queued.</summary>
    SuppressedDuplicate
}

/// <summary>Returned by <c>POST /api/stream/telemetry</c> – tells the device (or tester) which path its packet took.</summary>
public sealed class StreamIngestResult
{
    public string MacAddress { get; set; } = "";
    public StreamRoute Route { get; set; }
    public Guid? AlertId { get; set; }
    public int TelemetryQueueDepth { get; set; }
    public int PendingCriticalAlerts { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>Summary of a burst submitted to <c>POST /api/stream/telemetry/batch</c>.</summary>
public sealed class StreamBatchResult
{
    public int Fifo { get; set; }
    public int Priority { get; set; }
    public int SuppressedDuplicates { get; set; }
    public int Rejected { get; set; }
    public int TelemetryQueueDepth { get; set; }
}

/// <summary>One processed reading in the timestamp-ordered timeline (SortedDictionary).</summary>
public sealed class TimelinePoint
{
    public DateTimeOffset Timestamp { get; set; }
    public string MacAddress { get; set; } = "";
    public string Name { get; set; } = "";
    public double Value { get; set; }
    public bool Critical { get; set; }
}

/// <summary>A sensor that appears in the timeline, for the graph's picker.</summary>
public sealed class TimelineSensor
{
    public string MacAddress { get; set; } = "";
    public string Name { get; set; } = "";
    public int Points { get; set; }
}
