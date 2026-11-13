using Microsoft.Extensions.Logging.Abstractions;
using SmartX.Api.Services;
using SmartX.Shared.Commands;

namespace SmartX.Tests;

/// <summary>
/// Feedback fix — the Part 2 lecturer feedback said counts never decay, are never pruned, and there is no operator
/// feedback. These tests pin down the fixed behaviour.
/// </summary>
public class PredictiveEngineFeedbackTests
{
    private const string Condition = "A1:2F:00:00:00:07|LowMoisture";

    private sealed class Rig
    {
        public DateTimeOffset Now = new(2026, 11, 13, 8, 0, 0, TimeSpan.Zero);
        public CommandStreamState Stream { get; } = new();
        public PredictiveEngine Engine { get; } = new();
        public SmartDevice Pump { get; }

        public Rig()
        {
            DeviceRegistrySeeder.Seed(Stream, 12, NullLogger.Instance);
            Pump = Stream.SearchDevices("Pump").First();
            Engine.Clock = () => Now;
            Engine.HalfLife = TimeSpan.FromHours(6);
            Engine.PruneInterval = TimeSpan.MaxValue;              // prune only when the test asks
        }

        /// <summary>Moisture drops, and the operator turns the pump on two minutes later.</summary>
        public void Episode(bool respond = true)
        {
            Engine.ObserveCondition(Condition, "Soil probe 007: moisture below 20 %", "A1:2F:00:00:00:07", "Soil probe 007", Now);
            if (respond)
            {
                Now += TimeSpan.FromMinutes(2);
                Engine.ObserveCommand(Pump.MacAddress, Pump.Name, CommandKind.TurnOn);
            }
            Now += TimeSpan.FromMinutes(15);                       // leave the sliding window
        }

        public SuggestedAction? PumpSuggestion() =>
            Engine.Suggest(Stream, 10).FirstOrDefault(s => s.MacAddress == Pump.MacAddress && s.Command == CommandKind.TurnOn);
    }

    [Fact]
    public void Learns_after_two_episodes_like_part_2()
    {
        var rig = new Rig();
        rig.Episode();
        rig.Episode();
        rig.Episode(respond: false);
        rig.Now -= TimeSpan.FromMinutes(14);                       // back inside the third occurrence's window

        var suggestion = rig.PumpSuggestion();
        Assert.NotNull(suggestion);
        Assert.StartsWith("rule|", suggestion!.RuleKey);
        Assert.True(suggestion.EffectiveSupport >= PredictiveEngine.MinEffectiveSupport);
    }

    [Fact]
    public void Old_behaviour_decays_and_is_pruned()
    {
        var rig = new Rig();
        rig.Episode();
        rig.Episode();

        rig.Now += TimeSpan.FromDays(2);                          // 8 half-lives: weight ≈ 2 / 256
        rig.Engine.ObserveCondition(Condition, "Soil probe 007: moisture below 20 %", "A1:2F:00:00:00:07", "Soil probe 007", rig.Now);

        Assert.Null(rig.PumpSuggestion());                        // stale habit no longer drives suggestions
        Assert.True(rig.Engine.Prune(rig.Now) >= 1);              // and the weak pattern is removed
    }

    [Fact]
    public void Dismiss_snoozes_and_lowers_confidence_accept_reinforces()
    {
        var rig = new Rig();
        for (var i = 0; i < 4; i++) rig.Episode();               // weight ≈ 3.9, still ≥ 1.5 after halving
        rig.Episode(respond: false);
        rig.Now -= TimeSpan.FromMinutes(14);

        var first = rig.PumpSuggestion()!;
        var dismissed = rig.Engine.Feedback(first.RuleKey, SuggestionVerdict.Dismissed);
        Assert.NotNull(dismissed.SnoozedUntil);
        Assert.True(dismissed.ConfidenceFactor < 1);
        Assert.Null(rig.PumpSuggestion());                        // hidden while snoozed

        // A fresh occurrence after the snooze: suggested again, but with less confidence than before.
        rig.Now += TimeSpan.FromMinutes(40);
        rig.Engine.ObserveCondition(Condition, "Soil probe 007: moisture below 20 %", "A1:2F:00:00:00:07", "Soil probe 007", rig.Now);
        var again = rig.PumpSuggestion()!;
        Assert.True(again.Confidence < first.Confidence);

        var accepted = rig.Engine.Feedback(again.RuleKey, SuggestionVerdict.Accepted);
        Assert.True(accepted.RuleWeight > dismissed.RuleWeight);
        Assert.Null(rig.PumpSuggestion());                        // handled for this occurrence
    }
}
