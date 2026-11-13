using SmartX.Shared.Commands;

namespace SmartX.Api.Services;

// Feedback fix (Part 2 → Part 3): "there is no operator feedback".
// Operators now accept or dismiss each suggestion. Accepting reinforces the rule (+1 weight) and confirms the
// response for the current occurrence. Dismissing halves the rule's weight and snoozes it. Both verdicts feed a
// smoothed acceptance factor that scales future confidence, so the engine learns which suggestions are useful.
public sealed partial class PredictiveEngine
{
    public static readonly TimeSpan SnoozeDuration = TimeSpan.FromMinutes(30);

    private sealed class FeedbackRecord
    {
        public DecayingCounter Accepted { get; } = new();
        public DecayingCounter Dismissed { get; } = new();
    }

    private readonly Dictionary<string, FeedbackRecord> _feedback = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _snoozedUntil = new(StringComparer.Ordinal);
    private (long Accepted, long Dismissed) _feedbackTotals;

    /// <summary>Applies an operator verdict to the rule behind a suggestion.</summary>
    public SuggestionFeedbackResult Feedback(string ruleKey, SuggestionVerdict verdict)
    {
        var now = Clock();
        lock (_lock)
        {
            if (!_feedback.TryGetValue(ruleKey, out var record))
                _feedback[ruleKey] = record = new FeedbackRecord();

            var result = new SuggestionFeedbackResult { RuleKey = ruleKey, Verdict = verdict };
            var parts = ruleKey.Split('|');
            var isRule = parts.Length >= 3 && parts[0] == "rule";
            // rule|<condition key>|<action key>; the condition key itself contains '|', so split on the action prefix
            var (conditionKey, actionKey) = isRule ? SplitRule(ruleKey) : ("", "");

            if (verdict == SuggestionVerdict.Accepted)
            {
                record.Accepted.Add(1, now, HalfLife);
                _feedbackTotals.Accepted++;
                _snoozedUntil.Remove(ruleKey);
                if (isRule && _pairCounts.TryGetValue(conditionKey, out var actions) && actions.TryGetValue(actionKey, out var counter))
                {
                    counter.Add(1, now, HalfLife);                                  // reinforce
                    foreach (var occurrence in _recent.Where(r => r.Key == conditionKey))
                        occurrence.CreditedActions.Add(actionKey);                  // handled for this occurrence
                    result.RuleWeight = Math.Round(counter.ValueAt(now, HalfLife), 2);
                }
                result.Message = "Thanks – this pattern will be suggested with more confidence.";
            }
            else
            {
                record.Dismissed.Add(1, now, HalfLife);
                _feedbackTotals.Dismissed++;
                _snoozedUntil[ruleKey] = now + SnoozeDuration;
                result.SnoozedUntil = now + SnoozeDuration;
                if (isRule && _pairCounts.TryGetValue(conditionKey, out var actions) && actions.TryGetValue(actionKey, out var counter))
                {
                    counter.Multiply(0.5, now, HalfLife);                           // weaken
                    result.RuleWeight = Math.Round(counter.ValueAt(now, HalfLife), 2);
                }
                result.Message = $"Dismissed – hidden for {SnoozeDuration.TotalMinutes:0} minutes and weighted down.";
            }

            result.ConfidenceFactor = Math.Round(FeedbackFactorUnsafe(ruleKey, now), 2);
            return result;
        }
    }

    private static (string Condition, string Action) SplitRule(string ruleKey)
    {
        var body = ruleKey["rule|".Length..];
        foreach (var prefix in new[] { "|cmd|", "|inspect|" })
        {
            var i = body.LastIndexOf(prefix, StringComparison.Ordinal);
            if (i > 0) return (body[..i], body[(i + 1)..]);
        }
        return (body, "");
    }

    private bool IsSnoozedUnsafe(string ruleKey, DateTimeOffset now) =>
        _snoozedUntil.TryGetValue(ruleKey, out var until) && until > now;

    /// <summary>
    /// Laplace-smoothed acceptance factor (accepted + 2) / (accepted + dismissed + 2), from decayed counts.
    /// It is 1 with no feedback, drops with each dismissal, and recovers as dismissals age.
    /// </summary>
    private double FeedbackFactorUnsafe(string ruleKey, DateTimeOffset now)
    {
        if (!_feedback.TryGetValue(ruleKey, out var record)) return 1;
        var accepted = record.Accepted.ValueAt(now, HalfLife);
        var dismissed = record.Dismissed.ValueAt(now, HalfLife);
        return Math.Min(1, (accepted + 2) / (accepted + dismissed + 2));
    }

    private string FeedbackNoteUnsafe(string ruleKey)
    {
        if (!_feedback.TryGetValue(ruleKey, out var record)) return "";
        var notes = new List<string>();
        if (record.Accepted.RawCount > 0) notes.Add($"accepted {record.Accepted.RawCount}×");
        if (record.Dismissed.RawCount > 0) notes.Add($"dismissed {record.Dismissed.RawCount}×");
        return notes.Count == 0 ? "" : $" Operator feedback: {string.Join(", ", notes)}.";
    }

    /// <summary>Removes expired snoozes and feedback that has decayed to nothing (called from <see cref="Prune"/>).</summary>
    private void PruneFeedbackUnsafe(DateTimeOffset now)
    {
        foreach (var (key, until) in _snoozedUntil.ToList())
            if (until <= now) _snoozedUntil.Remove(key);
        foreach (var (key, record) in _feedback.ToList())
            if (record.Accepted.ValueAt(now, HalfLife) + record.Dismissed.ValueAt(now, HalfLife) < PruneBelow)
                _feedback.Remove(key);
    }
}
