namespace SmartX.Shared.Commands;

// -------------------------------------------------------------------------------------------------
// Part 2 — Real-Time Command Stream & History. Dashboard snapshot.
// Shared by the API and the Blazor client, so the same DataAnnotations validate on both sides.
// -------------------------------------------------------------------------------------------------

/// <summary>Everything the Command Stream dashboard needs in one poll.</summary>
public sealed class CommandStreamSnapshot
{
    public int TelemetryQueueDepth { get; set; }
    public int CommandQueueDepth { get; set; }
    public int PendingCriticalAlerts { get; set; }
    public int UndoStackDepth { get; set; }
    public string? NextUndoLabel { get; set; }
    public int ActiveFaultCount { get; set; }
    public long DuplicatesSuppressed { get; set; }
    public int DeviceCount { get; set; }
    public int HistoryCount { get; set; }
    public long TelemetryProcessed { get; set; }
    public long CriticalProcessed { get; set; }
    public long TelemetryRejected { get; set; }

    public List<SmartDevice> Devices { get; set; } = new();
    public List<CommandRecord> RecentHistory { get; set; } = new();
    public List<CriticalAlert> PendingAlerts { get; set; } = new();
    public List<CriticalAlert> RecentAlerts { get; set; } = new();
    public List<FaultEntry> Faults { get; set; } = new();
    public List<SuggestedAction> Suggestions { get; set; } = new();
    public DateTimeOffset GeneratedAt { get; set; }
}
