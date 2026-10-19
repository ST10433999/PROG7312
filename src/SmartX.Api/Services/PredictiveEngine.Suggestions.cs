using SmartX.Shared.Commands;

namespace SmartX.Api.Services;

// Part 2 — turns learned (condition → action) rules and active faults into dashboard suggestions.
public sealed partial class PredictiveEngine
{
    // ------------------------------------------------------------------ recommendation

    public List<SuggestedAction> Suggest(CommandStreamState stream, int take = 5)
    {
        var now = DateTimeOffset.UtcNow;
        var learned = new List<SuggestedAction>();
        var insights = new List<SuggestedAction>();

        lock (_lock)
        {
            Prune(now);

            // ---- 1. learned rules for every condition that is active right now
            var activeConditions = _recent
                .GroupBy(c => c.Key)
                .Select(g => g.OrderByDescending(c => c.At).First())
                .ToList();

            foreach (var condition in activeConditions)
            {
                if (!_pairCounts.TryGetValue(condition.Key, out var actions)) continue;
                var occurrences = _conditionCounts.TryGetValue(condition.Key, out var n) ? n : 0;

                foreach (var (actionKey, count) in actions)
                {
                    if (count < MinSupport) continue;
                    if (condition.CreditedActions.Contains(actionKey)) continue;   // operator already did it this time

                    // Occurrences still inside the window that have NOT yet been followed by this action
                    // haven't had their chance yet, so they are excluded from the denominator.
                    var pending = _recent.Count(c => c.Key == condition.Key && !c.CreditedActions.Contains(actionKey));
                    var prior = Math.Max(1, occurrences - pending);
                    var confidence = Math.Min(1.0, (double)count / prior);

                    var parts = actionKey.Split('|');
                    if (parts.Length < 2) continue;
                    var device = stream.Find(parts[1]);
                    if (device is null) continue;

                    CommandKind? command = parts[0] == "cmd" && parts.Length > 2 && Enum.TryParse<CommandKind>(parts[2], out var parsed)
                        ? parsed
                        : null;

                    var actionLabel = _actionLabels.TryGetValue(actionKey, out var l) ? l : actionKey;
                    learned.Add(new SuggestedAction
                    {
                        Id = Guid.NewGuid(),
                        Kind = command is null ? SuggestionKind.InspectDevice : SuggestionKind.IssueCommand,
                        MacAddress = device.MacAddress,
                        DeviceName = device.Name,
                        Command = command,
                        Rationale = $"After \"{condition.Label}\", operators {actionLabel} in {count} of {prior} previous occurrence(s).",
                        Confidence = Math.Round(confidence, 2),
                        Support = count,
                        Trigger = condition.Label,
                        GeneratedAt = now
                    });
                }
            }

            // ---- 2. repeat offenders: the same node raising several alerts inside the window
            foreach (var group in _recent.GroupBy(c => c.MacAddress).Where(g => g.Count() >= RepeatOffenderThreshold))
            {
                var latest = group.OrderByDescending(c => c.At).First();
                insights.Add(new SuggestedAction
                {
                    Id = Guid.NewGuid(),
                    Kind = SuggestionKind.Insight,
                    MacAddress = latest.MacAddress,
                    DeviceName = latest.Name,
                    Rationale = $"{latest.Name} raised {group.Count()} alerts in the last {Window.TotalMinutes:0} minutes – likely a hardware or calibration fault.",
                    Confidence = Math.Round(Math.Min(0.95, 0.4 + 0.1 * group.Count()), 2),
                    Support = group.Count(),
                    Trigger = "Repeated alerts",
                    GeneratedAt = now
                });
            }
        }

        // ---- 3. unresolved faults from the HashSet
        foreach (var fault in stream.Faults().Take(10))
        {
            var minutes = Math.Max(0, (now - fault.Since).TotalMinutes);
            insights.Add(new SuggestedAction
            {
                Id = Guid.NewGuid(),
                Kind = fault.IsDevice ? SuggestionKind.IssueCommand : SuggestionKind.Insight,
                MacAddress = fault.MacAddress,
                DeviceName = fault.Name,
                Command = fault.IsDevice ? CommandKind.Reset : null,
                Rationale = fault.IsDevice
                    ? $"{fault.Name} has been faulted for {minutes:0} min ({fault.Reason}). A Reset clears it from the active-fault set."
                    : $"{fault.Name} has an unresolved fault for {minutes:0} min: {fault.Reason}.",
                Confidence = 0.5,
                Trigger = "Active fault",
                GeneratedAt = now
            });
        }

        // Learned rules outrank generic insights; one suggestion per (kind, device, command).
        return learned.OrderByDescending(s => s.Confidence).ThenByDescending(s => s.Support)
            .Concat(insights.OrderByDescending(s => s.Confidence))
            .GroupBy(s => (s.Kind, s.MacAddress, s.Command))
            .Select(g => g.First())
            .Take(Math.Clamp(take, 1, 20))
            .ToList();
    }

    /// <summary>Numbers for the "how does the engine work" panel and the README tests.</summary>
    public object Stats()
    {
        lock (_lock)
        {
            return new
            {
                windowMinutes = Window.TotalMinutes,
                minSupport = MinSupport,
                activeConditions = _recent.Count,
                distinctConditions = _conditionCounts.Count,
                learnedPairs = _pairCounts.Values.Sum(a => a.Count),
                strongRules = _pairCounts.Values.Sum(a => a.Values.Count(v => v >= MinSupport))
            };
        }
    }
}
