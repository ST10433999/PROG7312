using SmartX.Shared.Commands;

namespace SmartX.Api.Services;

// -------------------------------------------------------------------------------------------------
// Part 2 — Predictive Action & Recommendation Engine (observation half; suggestions are in
// PredictiveEngine.Suggestions.cs, operator feedback in PredictiveEngine.Feedback.cs).
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
//          confidence = weight of (condition → action) ÷ weight of earlier occurrences of the condition
//      provided the pattern's effective support is at least MinEffectiveSupport.
//
// Part 3 (lecturer feedback on Part 2): counts now DECAY with a half-life, so recent behaviour outweighs old
// behaviour; weak patterns are PRUNED; and operators give FEEDBACK (accept / dismiss) that reinforces or
// weakens a rule and snoozes it.
// -------------------------------------------------------------------------------------------------

public sealed partial class PredictiveEngine
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public const int MinSupport = 2;
    /// <summary>Decayed support a rule needs before it is suggested: two recent occurrences, or more older ones.</summary>
    public const double MinEffectiveSupport = 1.5;
    public const int RepeatOffenderThreshold = 3;
    /// <summary>Weights below this are pruned.</summary>
    public const double PruneBelow = 0.05;
    public const int MaxActionsPerCondition = 25;
    public const int MaxConditions = 1_000;

    /// <summary>How quickly learned behaviour fades. Set from configuration (Predictive:HalfLifeHours).</summary>
    public TimeSpan HalfLife { get; set; } = TimeSpan.FromHours(6);

    /// <summary>How often the prune pass runs at most.</summary>
    public TimeSpan PruneInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Overridable clock so the unit tests can fast-forward time.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

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

    /// <summary>condition key → decaying count of occurrences.</summary>
    private readonly Dictionary<string, DecayingCounter> _conditionCounts = new(StringComparer.Ordinal);

    /// <summary>condition key → (action key → decaying count of times the action followed the condition).</summary>
    private readonly Dictionary<string, Dictionary<string, DecayingCounter>> _pairCounts = new(StringComparer.Ordinal);

    /// <summary>action key → human-readable description, e.g. "turned on Pump 01".</summary>
    private readonly Dictionary<string, string> _actionLabels = new(StringComparer.Ordinal);

    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;
    private long _prunedPairs;
    private long _prunedConditions;

    // ------------------------------------------------------------------ observation

    public void ObserveCondition(string key, string label, string macAddress, string name, DateTimeOffset? at = null)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var now = Clock();
        var when = at ?? now;
        lock (_lock)
        {
            Expire(now);
            _recent.Enqueue(new ConditionEvent { Key = key, Label = label, MacAddress = macAddress, Name = name, At = when });
            if (!_conditionCounts.TryGetValue(key, out var counter))
                _conditionCounts[key] = counter = new DecayingCounter();
            counter.Add(1, now, HalfLife);
            PruneIfDue(now);
        }
    }

    public void ObserveCommand(string macAddress, string deviceName, CommandKind command) =>
        ObserveAction($"cmd|{macAddress}|{command}", $"issued {command} on {deviceName}");

    public void ObserveInspection(string macAddress, string deviceName) =>
        ObserveAction($"inspect|{macAddress}", $"looked up {deviceName}");

    private void ObserveAction(string actionKey, string label)
    {
        var now = Clock();
        lock (_lock)
        {
            Expire(now);
            _actionLabels[actionKey] = label;
            foreach (var condition in _recent)
            {
                if (!condition.CreditedActions.Add(actionKey)) continue;   // already credited for this occurrence
                Pair(condition.Key, actionKey).Add(1, now, HalfLife);
            }
            PruneIfDue(now);
        }
    }

    private DecayingCounter Pair(string conditionKey, string actionKey)
    {
        if (!_pairCounts.TryGetValue(conditionKey, out var actions))
        {
            actions = new Dictionary<string, DecayingCounter>(StringComparer.Ordinal);
            _pairCounts[conditionKey] = actions;
        }
        if (!actions.TryGetValue(actionKey, out var counter))
            actions[actionKey] = counter = new DecayingCounter();
        return counter;
    }

    /// <summary>Drops condition occurrences that have left the sliding window.</summary>
    private void Expire(DateTimeOffset now)
    {
        while (_recent.TryPeek(out var oldest) && now - oldest.At > Window)
            _recent.Dequeue();
    }

    // ------------------------------------------------------------------ pruning

    private void PruneIfDue(DateTimeOffset now)
    {
        if (now - _lastPrune < PruneInterval) return;
        _lastPrune = now;
        Prune(now);
    }

    /// <summary>
    /// Removes patterns whose decayed weight has fallen below <see cref="PruneBelow"/>, keeps at most
    /// <see cref="MaxActionsPerCondition"/> actions per condition and <see cref="MaxConditions"/> conditions (weakest
    /// first), so memory and suggestion time stay bounded however long the gateway runs.
    /// </summary>
    public int Prune(DateTimeOffset? at = null)
    {
        var now = at ?? Clock();
        var removed = 0;
        lock (_lock)
        {
            var active = new HashSet<string>(_recent.Select(r => r.Key), StringComparer.Ordinal);

            foreach (var (conditionKey, actions) in _pairCounts.ToList())
            {
                foreach (var (actionKey, counter) in actions.ToList())
                    if (counter.ValueAt(now, HalfLife) < PruneBelow) { actions.Remove(actionKey); removed++; }

                if (actions.Count > MaxActionsPerCondition)
                    foreach (var weakest in actions.OrderBy(a => a.Value.ValueAt(now, HalfLife)).Take(actions.Count - MaxActionsPerCondition).ToList())
                    { actions.Remove(weakest.Key); removed++; }

                if (actions.Count == 0) _pairCounts.Remove(conditionKey);
            }

            foreach (var (conditionKey, counter) in _conditionCounts.ToList())
            {
                if (active.Contains(conditionKey) || _pairCounts.ContainsKey(conditionKey)) continue;
                if (counter.ValueAt(now, HalfLife) < PruneBelow) { _conditionCounts.Remove(conditionKey); _prunedConditions++; }
            }

            if (_conditionCounts.Count > MaxConditions)
                foreach (var weakest in _conditionCounts.Where(c => !active.Contains(c.Key))
                             .OrderBy(c => c.Value.ValueAt(now, HalfLife)).Take(_conditionCounts.Count - MaxConditions).ToList())
                {
                    _conditionCounts.Remove(weakest.Key);
                    _pairCounts.Remove(weakest.Key);
                    _prunedConditions++;
                }

            PruneFeedbackUnsafe(now);
            _prunedPairs += removed;
        }
        return removed;
    }
}
