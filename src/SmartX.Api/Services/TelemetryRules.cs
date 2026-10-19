using SmartX.Shared.Models;

namespace SmartX.Api.Services;

// Part 2 — thresholds that make a single reading critical, independent of the Part 1 z-score.

/// <summary>Hard thresholds that make a single reading critical, independent of the Part 1 z-score.</summary>
public static class TelemetryRules
{
    public const double MoistureLowPercent = 20;
    public const double MoistureHighPercent = 85;
    public const double PowerSpikeWatts = 2000;

    /// <summary>Returns the alert a reading should raise, or null for a normal reading.</summary>
    public static (Severity Severity, string Kind, string Title)? Classify(SensorProfile sensor, TelemetryIngestRequest reading)
    {
        if (reading.Value is not { } value) return null;      // boolean actuators never breach a threshold

        return sensor.Category switch
        {
            SensorCategory.Environmental when value < MoistureLowPercent =>
                (Severity.Critical, "moisture-low", $"Critical moisture drop: {value:0.#}% (below {MoistureLowPercent}%)"),
            SensorCategory.Environmental when value > MoistureHighPercent =>
                (Severity.High, "moisture-high", $"Moisture saturation: {value:0.#}% (above {MoistureHighPercent}%)"),
            SensorCategory.PowerConsumption when value > PowerSpikeWatts =>
                (Severity.Critical, "power-spike", $"Severe power spike: {value:0} W (above {PowerSpikeWatts} W)"),
            _ => null
        };
    }
}
