namespace SmartX.Api.History;

/// <summary>
/// Part 3 — key of the per-sensor AVL tree: the reading's UTC timestamp in ticks, with an ever-increasing
/// sequence number as a tie-breaker. Two packets can share a timestamp, and an AVL tree needs unique keys.
/// Comparing ticks first keeps the in-order traversal chronological.
/// </summary>
public readonly record struct HistoryKey(long UtcTicks, long Sequence) : IComparable<HistoryKey>
{
    public int CompareTo(HistoryKey other)
    {
        var c = UtcTicks.CompareTo(other.UtcTicks);
        return c != 0 ? c : Sequence.CompareTo(other.Sequence);
    }

    /// <summary>Smallest possible key at <paramref name="at"/>: the inclusive lower bound of a range query.</summary>
    public static HistoryKey Lowest(DateTimeOffset at) => new(at.UtcTicks, long.MinValue);

    /// <summary>Largest possible key at <paramref name="at"/>: the inclusive upper bound of a range query.</summary>
    public static HistoryKey Highest(DateTimeOffset at) => new(at.UtcTicks, long.MaxValue);

    public DateTimeOffset Timestamp => new(UtcTicks, TimeSpan.Zero);
}
