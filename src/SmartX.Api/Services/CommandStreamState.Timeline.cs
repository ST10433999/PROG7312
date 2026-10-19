using SmartX.Shared.Commands;

namespace SmartX.Api.Services;

// Part 2 — timestamp-ordered timeline of processed sensor readings (Rubric: SortedDictionary).
public sealed partial class CommandStreamState
{
    /// <summary>Processed readings ordered by timestamp, feeding the dashboard's timeline graph.</summary>
    private readonly SortedDictionary<StreamKey, TimelinePoint> _timeline = new();

    private long _telemetryProcessed;

    public const int MaxTimeline = 5000;

    public long TelemetryProcessed { get { lock (_lock) return _telemetryProcessed; } }

    public void AddTimelinePoint(TimelinePoint point)
    {
        lock (_lock)
        {
            _timeline[new StreamKey(point.Timestamp, ++_sequence)] = point;
            _telemetryProcessed++;
            // Keys are sorted, so the first key is always the oldest – trimming needs no search.
            while (_timeline.Count > MaxTimeline) _timeline.Remove(_timeline.Keys.First());
        }
    }

    /// <summary>The newest <paramref name="take"/> points for one sensor (or all), oldest → newest, ready to plot.</summary>
    public List<TimelinePoint> Timeline(string? mac, int take = 120)
    {
        var normalised = string.IsNullOrWhiteSpace(mac) ? null : NormaliseMac(mac);
        lock (_lock)
        {
            return _timeline.Values
                .Where(p => normalised is null || p.MacAddress.Equals(normalised, StringComparison.OrdinalIgnoreCase))
                .TakeLast(Math.Clamp(take, 2, 1000))
                .Select(x => Clone(x))
                .ToList();
        }
    }

    /// <summary>Sensors that appear in the timeline, most recently active first.</summary>
    public List<TimelineSensor> TimelineSensors(int take = 30)
    {
        lock (_lock)
        {
            return _timeline.Values
                .GroupBy(p => p.MacAddress, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { Last = g.Last(), Count = g.Count() })
                .OrderByDescending(x => x.Last.Timestamp)
                .Take(take)
                .Select(x => new TimelineSensor { MacAddress = x.Last.MacAddress, Name = x.Last.Name, Points = x.Count })
                .ToList();
        }
    }

    private static TimelinePoint Clone(TimelinePoint p) => new()
    {
        Timestamp = p.Timestamp, MacAddress = p.MacAddress, Name = p.Name, Value = p.Value, Critical = p.Critical
    };
}

/// <summary>
/// Key for the SortedDictionaries. A timestamp alone would collide when two items land on the same
/// tick, so a monotonically increasing sequence number breaks ties.
/// </summary>
public readonly record struct StreamKey(DateTimeOffset Timestamp, long Sequence) : IComparable<StreamKey>
{
    public int CompareTo(StreamKey other)
    {
        var byTime = Timestamp.CompareTo(other.Timestamp);
        return byTime != 0 ? byTime : Sequence.CompareTo(other.Sequence);
    }
}
