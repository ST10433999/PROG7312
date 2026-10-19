using SmartX.Shared.Commands;

namespace SmartX.Api.Services;

// -------------------------------------------------------------------------------------------------
// Part 2 — Predictive Action & Recommendation Engine (observation half; suggestions are in
// PredictiveEngine.Suggestions.cs).
//
// Algorithm: sequential association-rule mining over a sliding time window.
//
//   1. Conditions are things the system observes: a critical reading ("Soil probe 000 moisture
//      dropped below 20%"), a Part 1 triage alert, an operator E-STOP.
//   2. Actions are things the operator does: issuing a command on a device, or searching for a
//      device on the dashboard.
//   3. Every action is credited to each condition seen in the previous Window (10 minutes). Each
//      condition occurrence credits a given action at most once.
//   4. When a condition fires again, the engine looks up the actions that historically followed it
//      and proposes them, with
//          confidence = times the action followed the condition ÷ earlier occurrences of the condition
//      provided the pattern has been seen at least MinSupport times.
//
// This is exactly the brief's example: if an operator keeps searching for "Pump 01" after
// "Moisture Sensor B" drops below 20 %, the next time the moisture drops Pump 01 is highlighted
// before anyone searches for it. On top of the learned rules the engine adds algorithmic insights:
// unresolved faults and nodes raising repeated alerts.
// -------------------------------------------------------------------------------------------------

public sealed partial class PredictiveEngine
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public const int MinSupport = 2;
    public const int RepeatOffenderThreshold = 3;

    private sealed class ConditionEvent
    {
        public required string Key { get; init; }
        public required string Label { get; init; }
        public required string MacAddress { get; init; }
        public required string Name { get; init; }
        public required DateTimeOffset At { get; init; }
        public HashSet<string> CreditedActions { get; } = new(StringComparer.Ordinal);
    }

    private readonly object _lock = new();

    /// <summary>Condition occurrences inside the window, oldest first (pruned from the front).</summary>
    private readonly Queue<ConditionEvent> _recent = new();

    /// <summary>condition key → number of occurrences ever seen.</summary>
    private readonly Dictionary<string, int> _conditionCounts = new(StringComparer.Ordinal);

    /// <summary>condition key → (action key → times the action followed the condition).</summary>
    private readonly Dictionary<string, Dictionary<string, int>> _pairCounts = new(StringComparer.Ordinal);

    /// <summary>action key → human-readable description, e.g. "turned on Pump 01".</summary>
    private readonly Dictionary<string, string> _actionLabels = new(StringComparer.Ordinal);

    // ------------------------------------------------------------------ observation

    public void ObserveCondition(string key, string label, string macAddress, string name, DateTimeOffset? at = null)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var when = at ?? DateTimeOffset.UtcNow;
        lock (_lock)
        {
            Prune(DateTimeOffset.UtcNow);
            _recent.Enqueue(new ConditionEvent { Key = key, Label = label, MacAddress = macAddress, Name = name, At = when });
            _conditionCounts[key] = (_conditionCounts.TryGetValue(key, out var n) ? n : 0) + 1;
        }
    }

    public void ObserveCommand(string macAddress, string deviceName, CommandKind command) =>
        ObserveAction($"cmd|{macAddress}|{command}", $"issued {command} on {deviceName}");

    public void ObserveInspection(string macAddress, string deviceName) =>
        ObserveAction($"inspect|{macAddress}", $"looked up {deviceName}");

    private void ObserveAction(string actionKey, string label)
    {
        lock (_lock)
        {
            Prune(DateTimeOffset.UtcNow);
            _actionLabels[actionKey] = label;
            foreach (var condition in _recent)
            {
                if (!condition.CreditedActions.Add(actionKey)) continue;   // already credited for this occurrence
                if (!_pairCounts.TryGetValue(condition.Key, out var actions))
                {
                    actions = new Dictionary<string, int>(StringComparer.Ordinal);
                    _pairCounts[condition.Key] = actions;
                }
                actions[actionKey] = (actions.TryGetValue(actionKey, out var c) ? c : 0) + 1;
            }
        }
    }

    private void Prune(DateTimeOffset now)
    {
        while (_recent.TryPeek(out var oldest) && now - oldest.At > Window)
            _recent.Dequeue();
    }
}
