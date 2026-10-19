using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SmartX.Shared.Commands;

// -------------------------------------------------------------------------------------------------
// Part 2 — Real-Time Command Stream & History. Manual override commands.
// Shared by the API and the Blazor client, so the same DataAnnotations validate on both sides.
// -------------------------------------------------------------------------------------------------

/// <summary>Manual override commands an operator can issue.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CommandKind { TurnOn, TurnOff, Trigger, Reset, EmergencyStop }

/// <summary>Manual override for a single device, submitted from the dashboard.</summary>
public sealed class IssueCommandRequest
{
    [Required(ErrorMessage = "MAC address is required.")]
    [RegularExpression(@"^([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$", ErrorMessage = "Use the format AA:BB:CC:DD:EE:FF.")]
    public string MacAddress { get; set; } = "";

    public CommandKind Command { get; set; } = CommandKind.TurnOn;

    [Range(1, 10, ErrorMessage = "Priority must be 1 (critical) – 10 (low).")]
    public int Priority { get; set; } = 5;

    [StringLength(160, ErrorMessage = "Reason must be at most 160 characters.")]
    public string Reason { get; set; } = "";
}

/// <summary>One override applied to every device of a kind and/or category (e.g. "shut down all valves").</summary>
public sealed class BulkCommandRequest
{
    public DeviceKind? Kind { get; set; }

    [StringLength(40)]
    public string? Category { get; set; }

    public CommandKind Command { get; set; } = CommandKind.TurnOff;

    [StringLength(160)]
    public string Reason { get; set; } = "";
}

/// <summary>A command while it waits in the FIFO <c>Queue&lt;CommandEnvelope&gt;</c>. Bulk commands carry several targets.</summary>
public sealed class CommandEnvelope
{
    public Guid Id { get; set; }
    public List<string> TargetMacs { get; set; } = new();
    public string Label { get; set; } = "";
    public CommandKind Command { get; set; }
    public int Priority { get; set; } = 5;
    public string Reason { get; set; } = "";
    public DateTimeOffset QueuedAt { get; set; }
    public string IssuedBy { get; set; } = "operator";
}

/// <summary>One device-level change, stored in the <c>SortedDictionary</c> history.</summary>
public sealed class CommandRecord
{
    public Guid Id { get; set; }
    /// <summary>The operator action (stack entry) this record belongs to.</summary>
    public Guid BatchId { get; set; }
    public string MacAddress { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public CommandKind Command { get; set; }
    public int Priority { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public string PreviousState { get; set; } = "";
    public string NewState { get; set; } = "";
    public string IssuedBy { get; set; } = "operator";
    public bool Undone { get; set; }
}

/// <summary>One operator action – the element type of the undo <c>Stack&lt;CommandBatch&gt;</c>.</summary>
public sealed class CommandBatch
{
    public Guid Id { get; set; }
    public string Label { get; set; } = "";
    public CommandKind Command { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string IssuedBy { get; set; } = "operator";
    public List<CommandRecord> Records { get; set; } = new();
}
