using System.Text.Json.Serialization;
using SmartX.Shared.Models;

namespace SmartX.Shared.Commands;

// -------------------------------------------------------------------------------------------------
// Part 2 — Real-Time Command Stream & History. Critical alerts and active faults.
// Shared by the API and the Blazor client, so the same DataAnnotations validate on both sides.
// -------------------------------------------------------------------------------------------------

/// <summary>Where a critical alert came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlertSource { Telemetry, Device, Operator }

/// <summary>Critical alert held in the <c>PriorityQueue</c> until the worker processes it.</summary>
public sealed class CriticalAlert
{
    public Guid Id { get; set; }
    public string MacAddress { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public Severity Severity { get; set; } = Severity.Critical;
    public AlertSource Source { get; set; } = AlertSource.Operator;
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTimeOffset RaisedAt { get; set; }

    /// <summary>0 = Critical, 1 = High, 2 = Warn, 3 = Info (smaller dequeues first).</summary>
    public int PriorityScore { get; set; }

    /// <summary>The telemetry packet that triggered the alert, if any. Ingested when the alert is processed.</summary>
    public TelemetryIngestRequest? Reading { get; set; }

    /// <summary>Key the predictive engine uses to recognise a recurring condition, e.g. "A1:2F:..|moisture-low".</summary>
    public string ConditionKey { get; set; } = "";

    public DateTimeOffset? ProcessedAt { get; set; }
    public double? LatencyMs { get; set; }
    /// <summary>How many normal packets were waiting in the FIFO when this alert jumped ahead of them.</summary>
    public int QueuedAheadAtProcess { get; set; }
    public bool Acknowledged { get; set; }
}

/// <summary>An entry of the active-fault HashSet, with details for display.</summary>
public sealed class FaultEntry
{
    public string MacAddress { get; set; } = "";
    public string Name { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset Since { get; set; }
    public bool IsDevice { get; set; }
}
