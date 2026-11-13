using SmartX.Shared.Commands;

namespace SmartX.Api.Services;

// Part 2 — turns learned (condition → action) rules and active faults into dashboard suggestions.
// Part 3 — confidence uses decayed weights and is adjusted by operator feedback; snoozed suggestions are skipped.
public sealed partial class PredictiveEngine
{
    // ------------------------------------------------------------------ recommendation

    public List<SuggestedAction> Suggest(CommandStreamState stream, int take = 5)
    {
        var now = Clock();
        var learned = new List<SuggestedAction>();
        var insights = new List<SuggestedAction>();

        lock (_lock)
        {
            Expire(now);
            PruneIfDue(now);

            // ---- 1. learned rules for every condition that is active right now
            var activeConditions = _recent
                .GroupBy(c => c.Key)
                .Select(g => g.OrderByDescending(c => c.At).First())
                .ToList();

            foreach (var condition in activeConditions)
            {
                if (!_pairCounts.TryGetValue(condition.Key, out var actions)) continue;
                var occurrences = _conditionCounts.TryGetValue(condition.Key, out var n) ? n.ValueAt(now, HalfLife) : 0;

                foreach (var (actionKey, counter) in actions)
                {
                    var support = counter.ValueAt(now, HalfLife);
                    if (support < MinEffectiveSupport) continue;                    // too weak, or too old
                    if (condition.CreditedActions.Contains(actionKey)) continue;    // operator already did it this time

                    var ruleKey = RuleKey(condition.Key, actionKey);
                    if (IsSnoozedUnsafe(ruleKey, now)) continue;                    // operator dismissed it recently

                    // Occurrences still inside the window that have NOT yet been followed by this action
                    // haven't had their chance yet, so they are excluded from the denominator.
                    var pending = _recent.Count(c => c.Key == condition.Key && !c.CreditedActions.Contains(actionKey));
                    var prior = Math.Max(support, occurrences - pending);
                    var factor = FeedbackFactorUnsafe(ruleKey, now);
                    var confidence = Math.Min(1.0, support / Math.Max(1e-9, prior)) * factor;

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
                        RuleKey = ruleKey,
                        Kind = command is null ? SuggestionKind.InspectDevice : SuggestionKind.IssueCommand,
                        MacAddress = device.MacAddress,
                        DeviceName = device.Name,
                        Command = command,
                        Rationale = $"After \"{condition.Label}\", operators {actionLabel} " +
                                    $"(weighted support {support:0.0} of {prior:0.0}; recent behaviour counts most, half-life {HalfLife.TotalHours:0.#} h)." +
                                    FeedbackNoteUnsafe(ruleKey),
                        Confidence = Math.Round(confidence, 2),
                        Support = counter.RawCount,
                        EffectiveSupport = Math.Round(support, 2),
                        Trigger = condition.Label,
                        GeneratedAt = now
                    });
                }
            }

            // ---- 2. repeat offenders: the same node raising several alerts inside the window
            foreach (var group in _recent.GroupBy(c => c.MacAddress).Where(g => g.Count() >= RepeatOffenderThreshold))
            {
                var latest = group.OrderByDescending(c => c.At).First();
                var ruleKey = $"insight|repeat|{latest.MacAddress}";
                if (IsSnoozedUnsafe(ruleKey, now)) continue;
                insights.Add(new SuggestedAction
                {
                    Id = Guid.NewGuid(),
                    RuleKey = ruleKey,
                    Kind = SuggestionKind.Insight,
                    MacAddress = latest.MacAddress,
                    DeviceName = latest.Name,
                    Rationale = $"{latest.Name} raised {group.Count()} alerts in the last {Window.TotalMinutes:0} minutes – likely a hardware or calibration fault.",
                    Confidence = Math.Round(Math.Min(0.95, 0.4 + 0.1 * group.Count()) * FeedbackFactorUnsafe(ruleKey, now), 2),
                    Support = group.Count(),
                    EffectiveSupport = group.Count(),
                    Trigger = "Repeated alerts",
                    GeneratedAt = now
                });
            }

            // ---- 3. unresolved faults from the HashSet
            foreach (var fault in stream.Faults().Take(10))
            {
                var ruleKey = $"fault|{fault.MacAddress}";
                if (IsSnoozedUnsafe(ruleKey, now)) continue;
                var minutes = Math.Max(0, (now - fault.Since).TotalMinutes);
                insights.Add(new SuggestedAction
                {
                    Id = Guid.NewGuid(),
                    RuleKey = ruleKey,
                    Kind = fault.IsDevice ? SuggestionKind.IssueCommand : SuggestionKind.Insight,
                    MacAddress = fault.MacAddress,
                    DeviceName = fault.Name,
                    Command = fault.IsDevice ? CommandKind.Reset : null,
                    Rationale = fault.IsDevice
                        ? $"{fault.Name} has been faulted for {minutes:0} min ({fault.Reason}). A Reset clears it from the active-fault set."
                        : $"{fault.Name} has an unresolved fault for {minutes:0} min: {fault.Reason}.",
                    Confidence = Math.Round(0.5 * FeedbackFactorUnsafe(ruleKey, now), 2),
                    Trigger = "Active fault",
                    GeneratedAt = now
                });
            }
        }

        // Learned rules outrank generic insights; one suggestion per (kind, device, command).
        return learned.OrderByDescending(s => s.Confidence).ThenByDescending(s => s.EffectiveSupport)
            .Concat(insights.OrderByDescending(s => s.Confidence))
            .GroupBy(s => (s.Kind, s.MacAddress, s.Command))
            .Select(g => g.First())
            .Take(Math.Clamp(take, 1, 20))
            .ToList();
    }

    private static string RuleKey(string conditionKey, string actionKey) => $"rule|{conditionKey}|{actionKey}";

    /// <summary>Numbers for the "how does the engine work" panel and the README tests.</summary>
    public object Stats()
    {
        var now = Clock();
        lock (_lock)
        {
            var pairWeights = _pairCounts.Values.SelectMany(a => a.Values).Select(c => c.ValueAt(now, HalfLife)).ToList();
            return new
            {
                windowMinutes = Window.TotalMinutes,
                minSupport = MinSupport,
                minEffectiveSupport = MinEffectiveSupport,
                halfLifeHours = HalfLife.TotalHours,
                activeConditions = _recent.Count,
                distinctConditions = _conditionCounts.Count,
                learnedPairs = pairWeights.Count,
                strongRules = pairWeights.Count(w => w >= MinEffectiveSupport),
                prunedPairs = _prunedPairs,
                prunedConditions = _prunedConditions,
                acceptedFeedback = _feedbackTotals.Accepted,
                dismissedFeedback = _feedbackTotals.Dismissed,
                snoozed = _snoozedUntil.Count(s => s.Value > now)
            };
        }
    }
}
