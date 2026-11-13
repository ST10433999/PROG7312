using SmartX.Api.Services;
using SmartX.Shared.Models;

namespace SmartX.Api.History;

/// <summary>
/// Part 3 — fills the AVL history index at start-up:
/// <list type="number">
/// <item>every packet already in the Part 1 jagged batch stores (the last 24 hours), and</item>
/// <item>an older archive for the days before that, one reading every few minutes per sensor, so range queries
/// have realistic multi-day history to work on ("moisture between 14:00 and 16:00 three days ago").</item>
/// </list>
/// Readings arrive in timestamp order, the worst case for an unbalanced BST. The AVL rotations keep the trees
/// at logarithmic height anyway.
/// </summary>
public static class HistoryArchiveSeeder
{
    public static void Seed(GatewayState state, TelemetryHistoryIndex index, int archiveDays, int intervalMinutes, ILogger logger)
    {
        state.FlushPending();
        var fromBatches = index.RecordMany(Part1Batches(state));

        var archived = 0;
        if (archiveDays > 1 && intervalMinutes > 0)
            archived = index.RecordMany(Archive(state, archiveDays, intervalMinutes));

        // Warm the JIT for the query path so the first query an operator runs reports realistic timings.
        if (state.Sensors.Keys.FirstOrDefault() is { } anyMac)
            for (var i = 0; i < 3; i++) index.Query(anyMac, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, compareLinear: true);

        var stats = index.Stats();
        logger.LogInformation(
            "History index: {Batches} readings from the Part 1 jagged batches + {Archive} archived readings → {Total} readings in {Sensors} AVL trees (tallest {Height}).",
            fromBatches, archived, stats.TotalReadings, stats.Sensors, stats.TallestTree);
    }

    private static IEnumerable<(string, DateTimeOffset, double, string)> Part1Batches(GatewayState state)
    {
        foreach (var p in state.EnvironmentalHistory.Flatten()) yield return (p.MacAddress, p.Timestamp, p.Value, p.Unit);
        foreach (var p in state.PowerHistory.Flatten()) yield return (p.MacAddress, p.Timestamp, p.Value, p.Unit);
        foreach (var p in state.ActuatorHistory.Flatten()) yield return (p.MacAddress, p.Timestamp, p.Value ? 1d : 0d, "");
    }

    /// <summary>
    /// Synthetic archive from (now − days) up to 24 h ago, with a diurnal cycle, per-sensor offsets and occasional
    /// dry spells below the 20 % critical moisture threshold, so the queries have something to find.
    /// </summary>
    private static IEnumerable<(string, DateTimeOffset, double, string)> Archive(GatewayState state, int days, int intervalMinutes)
    {
        var rng = new Random(7312);
        var end = DateTimeOffset.UtcNow.AddHours(-24);
        var start = end.AddDays(-(days - 1));
        start = new DateTimeOffset(start.Year, start.Month, start.Day, start.Hour, 0, 0, TimeSpan.Zero);
        var step = TimeSpan.FromMinutes(intervalMinutes);

        foreach (var sensor in state.Sensors.Values.OrderBy(s => s.MacAddress, StringComparer.Ordinal))
        {
            var offset = rng.NextDouble() * 10 - 5;
            var drySpell = 0;
            for (var t = start; t < end; t += step)
            {
                var hour = t.Hour + t.Minute / 60.0;
                switch (sensor.Payload)
                {
                    case PayloadKind.Float:
                        if (drySpell == 0 && rng.NextDouble() < 0.002) drySpell = rng.Next(3, 8);
                        double moisture;
                        if (drySpell > 0) { moisture = 12 + rng.NextDouble() * 7; drySpell--; }
                        else moisture = 45 + offset + 12 * Math.Sin((hour - 9) / 24.0 * Math.PI * 2) + rng.NextDouble() * 4;
                        yield return (sensor.MacAddress, t, Math.Round(moisture, 2), "%");
                        break;
                    case PayloadKind.Int:
                        var watts = 800 + (hour is >= 7 and <= 21 ? 600 : 0) + rng.Next(150) + (int)(offset * 10);
                        yield return (sensor.MacAddress, t, watts, "W");
                        break;
                    default:
                        yield return (sensor.MacAddress, t, hour is >= 6 and <= 18 && rng.NextDouble() > 0.2 ? 1 : 0, "");
                        break;
                }
            }
        }
    }
}
