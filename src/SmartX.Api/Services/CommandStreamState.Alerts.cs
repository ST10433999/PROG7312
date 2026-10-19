using SmartX.Shared.Commands;
using SmartX.Shared.Models;

namespace SmartX.Api.Services;

// Part 2 — critical alerts (Rubric: Priority Queue) and active faults (Rubric: HashSet<T>).
public sealed partial class CommandStreamState
{
    /// <summary>
    /// Critical alerts that bypass the telemetry FIFO. The priority is a (severity, sequence) tuple:
    /// severity orders Critical before High, and the sequence number keeps alerts of equal severity
    /// in arrival order – <see cref="PriorityQueue{TElement,TPriority}"/> is not stable on its own.
    /// </summary>
    private readonly PriorityQueue<CriticalAlert, (int Severity, long Sequence)> _criticalAlerts = new();

    /// <summary>Display details for each MAC in the active-fault HashSet.</summary>
    private readonly Dictionary<string, FaultEntry> _faultDetails = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Recently processed alerts, newest first, bounded.</summary>
    private readonly List<CriticalAlert> _processedAlerts = new();

    /// <summary>Monotonic sequence shared by the PriorityQueue and the SortedDictionary keys.</summary>
    private long _sequence;
    private long _duplicatesSuppressed;
    private long _criticalProcessed;

    public const int MaxProcessedAlerts = 50;

    public int PendingCriticalCount { get { lock (_lock) return _criticalAlerts.Count; } }
    public int ActiveFaultCount { get { lock (_lock) return _activeFaults.Count; } }
    public long DuplicatesSuppressed { get { lock (_lock) return _duplicatesSuppressed; } }
    public long CriticalProcessed { get { lock (_lock) return _criticalProcessed; } }

    public IReadOnlyCollection<string> ActiveFaults
    {
        get { lock (_lock) return _activeFaults.ToArray(); }
    }

    /// <summary>
    /// Raises a critical alert. If the MAC is already in the active-fault set the alert is a
    /// duplicate: it is counted and dropped, and the method returns false. Otherwise the MAC is
    /// added to the set and the alert is enqueued ahead of all normal telemetry.
    /// </summary>
    public bool TryRaiseCritical(CriticalAlert alert, string faultReason, bool isDevice)
    {
        lock (_lock)
        {
            alert.MacAddress = NormaliseMac(alert.MacAddress);
            if (!_activeFaults.Add(alert.MacAddress))
            {
                _duplicatesSuppressed++;
                return false;
            }

            _faultDetails[alert.MacAddress] = new FaultEntry
            {
                MacAddress = alert.MacAddress,
                Name = alert.DeviceName,
                Reason = faultReason,
                Since = DateTimeOffset.UtcNow,
                IsDevice = isDevice
            };
            if (_devices.TryGetValue(alert.MacAddress, out var device)) device.InAlertState = true;

            if (alert.Id == Guid.Empty) alert.Id = Guid.NewGuid();
            if (alert.RaisedAt == default) alert.RaisedAt = DateTimeOffset.UtcNow;
            alert.PriorityScore = SeverityPriority(alert.Severity);
            _criticalAlerts.Enqueue(alert, (alert.PriorityScore, ++_sequence));
            return true;
        }
    }

    public CriticalAlert? DequeueCritical()
    {
        lock (_lock) return _criticalAlerts.TryDequeue(out var alert, out _) ? alert : null;
    }

    /// <summary>Non-destructive view of the alerts still waiting, in the order they will be processed.</summary>
    public List<CriticalAlert> PendingAlerts(int take = 10)
    {
        lock (_lock)
        {
            return _criticalAlerts.UnorderedItems
                .OrderBy(x => x.Priority.Severity)
                .ThenBy(x => x.Priority.Sequence)
                .Take(take)
                .Select(x => Clone(x.Element))
                .ToList();
        }
    }

    /// <summary>Stamps an alert as processed and records how many FIFO packets it overtook.</summary>
    public void MarkAlertProcessed(CriticalAlert alert, int queuedAhead)
    {
        lock (_lock)
        {
            alert.ProcessedAt = DateTimeOffset.UtcNow;
            alert.LatencyMs = Math.Round((alert.ProcessedAt.Value - alert.RaisedAt).TotalMilliseconds, 1);
            alert.QueuedAheadAtProcess = queuedAhead;
            _criticalProcessed++;
            _processedAlerts.Insert(0, alert);
            if (_processedAlerts.Count > MaxProcessedAlerts) _processedAlerts.RemoveAt(_processedAlerts.Count - 1);
        }
    }

    public List<CriticalAlert> RecentAlerts(int take = 10)
    {
        lock (_lock) return _processedAlerts.Take(take).Select(x => Clone(x)).ToList();
    }

    public CriticalAlert? AcknowledgeAlert(Guid id)
    {
        lock (_lock)
        {
            var alert = _processedAlerts.FirstOrDefault(a => a.Id == id);
            if (alert is null) return null;
            alert.Acknowledged = true;
            return Clone(alert);
        }
    }

    public bool IsFaulty(string mac)
    {
        lock (_lock) return _activeFaults.Contains(NormaliseMac(mac));
    }

    /// <summary>Removes a MAC from the fault set. When <paramref name="reasonPrefix"/> is given, only a fault with that reason is cleared.</summary>
    public bool ClearFault(string mac, string? reasonPrefix = null)
    {
        lock (_lock) return ClearFaultUnsafe(NormaliseMac(mac), reasonPrefix);
    }

    private bool ClearFaultUnsafe(string mac, string? reasonPrefix = null)
    {
        if (reasonPrefix is not null
            && (!_faultDetails.TryGetValue(mac, out var entry) || !entry.Reason.StartsWith(reasonPrefix, StringComparison.OrdinalIgnoreCase)))
            return false;

        var removed = _activeFaults.Remove(mac);
        _faultDetails.Remove(mac);
        if (_devices.TryGetValue(mac, out var device)) device.InAlertState = false;
        return removed;
    }

    public List<FaultEntry> Faults()
    {
        lock (_lock)
        {
            return _activeFaults
                .Select(mac => _faultDetails.TryGetValue(mac, out var f)
                    ? new FaultEntry { MacAddress = f.MacAddress, Name = f.Name, Reason = f.Reason, Since = f.Since, IsDevice = f.IsDevice }
                    : new FaultEntry { MacAddress = mac, Name = mac, Reason = "unknown", Since = DateTimeOffset.UtcNow })
                .OrderByDescending(f => f.Since)
                .ToList();
        }
    }

    private static int SeverityPriority(Severity severity) => severity switch
    {
        Severity.Critical => 0,
        Severity.High => 1,
        Severity.Warn => 2,
        _ => 3
    };

    private static CriticalAlert Clone(CriticalAlert a) => new()
    {
        Id = a.Id, MacAddress = a.MacAddress, DeviceName = a.DeviceName, Severity = a.Severity, Source = a.Source,
        Title = a.Title, Detail = a.Detail, RaisedAt = a.RaisedAt, PriorityScore = a.PriorityScore, Reading = a.Reading,
        ConditionKey = a.ConditionKey, ProcessedAt = a.ProcessedAt, LatencyMs = a.LatencyMs,
        QueuedAheadAtProcess = a.QueuedAheadAtProcess, Acknowledged = a.Acknowledged
    };
}
