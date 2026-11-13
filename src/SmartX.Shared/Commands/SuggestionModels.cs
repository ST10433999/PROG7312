using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SmartX.Shared.Commands;

// -------------------------------------------------------------------------------------------------
// Part 2 — Real-Time Command Stream & History. Predictive engine output.
// Shared by the API and the Blazor client, so the same DataAnnotations validate on both sides.
// -------------------------------------------------------------------------------------------------

/// <summary>What a recommendation asks the operator to do.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SuggestionKind { IssueCommand, InspectDevice, Insight }

/// <summary>A recommendation shown under "Suggested actions / Automated insights".</summary>
public sealed class SuggestedAction
{
    public Guid Id { get; set; }
    /// <summary>Stable key of the rule behind the suggestion. Sent back with operator feedback (Part 3).</summary>
    public string RuleKey { get; set; } = "";
    public SuggestionKind Kind { get; set; }
    public string MacAddress { get; set; } = "";
    public string DeviceName { get; set; } = "";
    /// <summary>Command to issue when <see cref="Kind"/> is IssueCommand; null for inspections and insights.</summary>
    public CommandKind? Command { get; set; }
    public string Rationale { get; set; } = "";
    /// <summary>0–1. For learned patterns: times the action followed the condition ÷ prior occurrences of the condition.</summary>
    public double Confidence { get; set; }
    /// <summary>How many times the pattern has been observed.</summary>
    public int Support { get; set; }
    /// <summary>Support after time-decay: recent observations count fully, old ones fade with the half-life (Part 3).</summary>
    public double EffectiveSupport { get; set; }
    public string Trigger { get; set; } = "";
    public DateTimeOffset GeneratedAt { get; set; }
}

/// <summary>Body of <c>POST /api/suggestions/search</c> – the dashboard logs operator searches for pattern analysis.</summary>
public sealed class SearchLogRequest
{
    [Required, StringLength(60, MinimumLength = 2)]
    public string Term { get; set; } = "";
}

// -------------------------------------------------------------------------------------------------
// Part 3 — operator feedback on suggestions (lecturer feedback on Part 2: "there is no operator feedback").
// -------------------------------------------------------------------------------------------------

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SuggestionVerdict { Accepted, Dismissed }

/// <summary>Body of <c>POST /api/suggestions/feedback</c>.</summary>
public sealed class SuggestionFeedbackRequest
{
    [Required(ErrorMessage = "The suggestion's rule key is required."), StringLength(300, MinimumLength = 3)]
    public string RuleKey { get; set; } = "";

    public SuggestionVerdict Verdict { get; set; }
}

public sealed class SuggestionFeedbackResult
{
    public string RuleKey { get; set; } = "";
    public SuggestionVerdict Verdict { get; set; }
    /// <summary>Decayed weight of the learned rule after the verdict (null for insights).</summary>
    public double? RuleWeight { get; set; }
    /// <summary>Multiplier now applied to this suggestion's confidence (1 = no penalty).</summary>
    public double ConfidenceFactor { get; set; }
    public DateTimeOffset? SnoozedUntil { get; set; }
    public string Message { get; set; } = "";
}
