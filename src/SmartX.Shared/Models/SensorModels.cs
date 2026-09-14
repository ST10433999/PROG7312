using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SmartX.Shared.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SensorCategory
{
    Environmental,      // soil moisture, temperature, humidity → float
    PowerConsumption,   // wattage, current → int
    Actuator            // valve / relay state → bool
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PayloadKind { Float, Int, Bool }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NodeState { Healthy, Drift, Spike, Silent, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Severity { Info, Warn, High, Critical }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TriageSource { System, User }

/// <summary>Shared registration form – the same DataAnnotations validate on the client and on the API.</summary>
public sealed class SensorRegistrationRequest
{
    [Required(ErrorMessage = "MAC address is required.")]
    [RegularExpression(@"^([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$", ErrorMessage = "Use the format AA:BB:CC:DD:EE:FF.")]
    public string MacAddress { get; set; } = "";

    [Required(ErrorMessage = "Friendly name is required."), StringLength(40, MinimumLength = 3, ErrorMessage = "Name must be 3–40 characters.")]
    public string Name { get; set; } = "";

    [Required(ErrorMessage = "Facility is required.")]
    public string Facility { get; set; } = "";

    [Required(ErrorMessage = "Zone is required.")]
    public string Zone { get; set; } = "";

    public string? SubZone { get; set; }

    [Required(ErrorMessage = "Node ID / room is required."), StringLength(24)]
    public string NodeId { get; set; } = "";

    [Required]
    public SensorCategory Category { get; set; } = SensorCategory.Environmental;

    [Range(1, 3600, ErrorMessage = "Publish interval must be 1–3600 seconds.")]
    public int PublishIntervalSeconds { get; set; } = 5;
}

public sealed class SensorProfile
{
    public Guid Id { get; set; }
    public string MacAddress { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Deployment path from root to leaf, e.g. ["Facility A", "Zone 1", "Sub-Zone B", "Rack 3"].</summary>
    public List<string> DeploymentPath { get; set; } = new();
    public SensorCategory Category { get; set; }
    public PayloadKind Payload { get; set; }
    public string Unit { get; set; } = "";
    public int PublishIntervalSeconds { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public List<AttachmentInfo> Attachments { get; set; } = new();

    public string DeploymentPathDisplay => string.Join(" → ", DeploymentPath);
}

public sealed class AttachmentInfo
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public bool Encrypted { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTimeOffset UploadedAt { get; set; }
}

/// <summary>A single raw reading pushed by (or simulated for) a node. The API turns it into a typed TelemetryPacket&lt;T&gt;.</summary>
public sealed class TelemetryIngestRequest
{
    [Required, RegularExpression(@"^([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$", ErrorMessage = "Invalid MAC address.")]
    public string MacAddress { get; set; } = "";

    public PayloadKind Kind { get; set; } = PayloadKind.Float;

    /// <summary>Numeric value for Float/Int kinds.</summary>
    public double? Value { get; set; }

    /// <summary>State for Bool kind.</summary>
    public bool? State { get; set; }

    public DateTimeOffset? Timestamp { get; set; }
}

public sealed class TelemetryIngestResult
{
    public string MacAddress { get; set; } = "";
    public PayloadKind Kind { get; set; }
    public string PayloadType { get; set; } = "";
    public NodeState State { get; set; }
    public double ZScore { get; set; }
    public double WindowMean { get; set; }
    public double WindowStdDev { get; set; }
    public int WindowSize { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>One Pulse Board tile.</summary>
public sealed class NodePulse
{
    public Guid SensorId { get; set; }
    public string MacAddress { get; set; } = "";
    public string Name { get; set; } = "";
    public string Location { get; set; } = "";
    public SensorCategory Category { get; set; }
    public PayloadKind Payload { get; set; }
    public string Unit { get; set; } = "";
    public NodeState State { get; set; }
    public double ZScore { get; set; }
    public double? LastValue { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
    public double SecondsSinceLastPacket { get; set; }
    public int ExpectedIntervalSeconds { get; set; }
    /// <summary>Last N values for the sparkline (oldest → newest).</summary>
    public List<double> Sparkline { get; set; } = new();
}

public sealed class FleetPulse
{
    public int Healthy { get; set; }
    public int Drift { get; set; }
    public int Spike { get; set; }
    public int Silent { get; set; }
    public int TotalSensors { get; set; }
    public double PacketsPerSecond { get; set; }
    public long TotalPacketsIngested { get; set; }
    public int OpenTriageItems { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public List<NodePulse> Nodes { get; set; } = new();
}

public sealed class TriageItem
{
    public Guid Id { get; set; }
    public Severity Severity { get; set; }
    public TriageSource Source { get; set; }
    public string MacAddress { get; set; } = "";
    public string Location { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
    public bool IsOpen => AcknowledgedAt is null;
}

public sealed class ReportIssueRequest
{
    [Required, RegularExpression(@"^([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$", ErrorMessage = "Invalid MAC address.")]
    public string MacAddress { get; set; } = "";

    [Required, StringLength(80, MinimumLength = 5, ErrorMessage = "Title must be 5–80 characters.")]
    public string Title { get; set; } = "";

    [StringLength(500)]
    public string Detail { get; set; } = "";

    public Severity Severity { get; set; } = Severity.Warn;
}

public sealed class BatchStoreStats
{
    public int BatchCount { get; set; }
    public int TotalPackets { get; set; }
    public int[] BatchLengths { get; set; } = Array.Empty<int>();
    public int FlattenedListCount { get; set; }
    public double FlattenMilliseconds { get; set; }
    public double[][] HourlyGrid { get; set; } = Array.Empty<double[]>();
}

public sealed class AggregateDemoResult
{
    public string Expression { get; set; } = "";
    public string LeftPacket { get; set; } = "";
    public string RightPacket { get; set; } = "";
    public string SumPacket { get; set; } = "";
    public string DeltaPacket { get; set; } = "";
    public bool LeftGreaterThanRight { get; set; }
    public string PayloadType { get; set; } = "";
}
