using System.Diagnostics;
using SmartX.Shared.Collections;
using SmartX.Shared.History;

namespace SmartX.Api.History;

/// <summary>
/// Part 3 — historical telemetry indexed by time.
/// <para>
/// Each sensor gets its own <see cref="AvlTree{TKey,TValue}"/> keyed by <see cref="HistoryKey"/> (timestamp + sequence),
/// held in a <c>Dictionary&lt;MAC, tree&gt;</c>. A question like "every moisture reading between 14:00 and 16:00 for
/// Node A" is then one O(1) dictionary lookup plus an O(log n + k) range walk. The Part 1 jagged batch store
/// has to scan every packet of every sensor for the same question.
/// </para>
/// <para>
/// The trees are augmented with subtree size and min/max/sum. The number of readings in a range and their
/// average, minimum and maximum come back in O(log n) without touching the readings. That is what makes the
/// hourly summary of a week (168 buckets) instant.
/// </para>
/// <para>
/// Live packets from the Part 1 ingestion path are added as they arrive (<see cref="Record"/>). A retention cap per
/// sensor evicts the oldest reading with an O(log n) delete-min so memory stays bounded on a long demo. Many
/// readers can query at once; writers take the lock exclusively (<see cref="ReaderWriterLockSlim"/>).
/// </para>
/// </summary>
public sealed class TelemetryHistoryIndex
{
    private sealed class Series(string unit)
    {
        public AvlTree<HistoryKey, double> Tree { get; } = new(static v => v);
        public string Unit { get; } = unit;
    }

    public const int MaxPageSize = 500;
    public const int MaxBuckets = 400;

    private readonly Dictionary<string, Series> _series = new(StringComparer.OrdinalIgnoreCase);
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private long _sequence;
    private long _evicted;

    /// <summary>Retention cap per sensor. When exceeded the oldest reading is evicted.</summary>
    public int MaxPointsPerSensor { get; set; } = 10_000;

    // ------------------------------------------------------------------ writes

    public void Record(string mac, DateTimeOffset at, double value, string unit = "")
    {
        _lock.EnterWriteLock();
        try { RecordUnsafe(mac, at, value, unit); }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Bulk load under a single write lock (start-up seeding).</summary>
    public int RecordMany(IEnumerable<(string Mac, DateTimeOffset At, double Value, string Unit)> readings)
    {
        var n = 0;
        _lock.EnterWriteLock();
        try
        {
            foreach (var r in readings) { RecordUnsafe(r.Mac, r.At, r.Value, r.Unit); n++; }
        }
        finally { _lock.ExitWriteLock(); }
        return n;
    }

    private void RecordUnsafe(string mac, DateTimeOffset at, double value, string unit)
    {
        if (!_series.TryGetValue(mac, out var series))
        {
            series = new Series(unit);
            _series[mac] = series;
        }
        series.Tree.AddOrUpdate(new HistoryKey(at.UtcTicks, ++_sequence), value);
        while (series.Tree.Count > MaxPointsPerSensor && series.Tree.TryRemoveMin(out _, out _))
            _evicted++;
    }

    public bool Remove(string mac)
    {
        _lock.EnterWriteLock();
        try { return _series.Remove(mac); }
        finally { _lock.ExitWriteLock(); }
    }

    // ------------------------------------------------------------------ reads

    public bool Contains(string mac)
    {
        _lock.EnterReadLock();
        try { return _series.ContainsKey(mac); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Range query for one sensor. Returns null when the sensor has no history.
    /// </summary>
    /// <param name="skip">readings to skip (paging), in the requested order</param>
    /// <param name="take">page size, 1–500</param>
    /// <param name="newestFirst">descending order, as a log is usually read</param>
    /// <param name="bucketMinutes">size of the summary buckets; 0 for none</param>
    /// <param name="compareLinear">also time a linear scan over the same readings, for the comparison panel</param>
    public HistoryQueryResult? Query(string mac, DateTimeOffset from, DateTimeOffset to, int skip = 0, int take = 100,
        bool newestFirst = true, int bucketMinutes = 60, bool compareLinear = false)
    {
        take = Math.Clamp(take, 1, MaxPageSize);
        skip = Math.Max(0, skip);
        var lo = HistoryKey.Lowest(from);
        var hi = HistoryKey.Highest(to);

        _lock.EnterReadLock();
        try
        {
            if (!_series.TryGetValue(mac, out var series)) return null;
            var tree = series.Tree;

            // The three tree operations are timed together; the best of three runs is reported so a one-off
            // GC pause or cold cache line doesn't misrepresent an operation that takes a few microseconds.
            // One extra reading is fetched on the chronologically older side of the page, so the oldest row on
            // the page still gets a delta (ascending: the reading just before the page; descending: just after it).
            var fetchSkip = newestFirst ? skip : Math.Max(0, skip - 1);
            int total = 0, visited = 0;
            var aggregate = RangeAggregate.Empty;
            var page = new List<KeyValuePair<HistoryKey, double>>(take + 1);
            var best = double.MaxValue;
            for (var run = 0; run < 3; run++)
            {
                page.Clear();
                var t0 = Stopwatch.GetTimestamp();
                total = tree.CountRange(lo, hi);                                         // O(log n) – size augmentation
                aggregate = total > 0 ? tree.Aggregate(lo, hi) : RangeAggregate.Empty;   // O(log n) – sum/min/max augmentation
                visited = tree.Range(lo, hi, page, fetchSkip, take + 1, newestFirst);    // O(log n + take)
                best = Math.Min(best, Stopwatch.GetElapsedTime(t0).TotalMicroseconds);
            }

            var result = new HistoryQueryResult
            {
                MacAddress = mac,
                Unit = series.Unit,
                From = from,
                To = to,
                TotalInRange = total,
                Skip = skip,
                Take = take,
                NewestFirst = newestFirst,
                Min = total > 0 ? Math.Round(aggregate.Min, 3) : null,
                Average = total > 0 ? Math.Round(aggregate.Average, 3) : null,
                Max = total > 0 ? Math.Round(aggregate.Max, 3) : null,
                FirstReading = tree.Min()?.Key.Timestamp,
                LastReading = tree.Max()?.Key.Timestamp,
                BucketMinutes = bucketMinutes,
                Stats = new HistoryQueryStats
                {
                    SeriesSize = tree.Count,
                    TreeHeight = tree.Height,
                    MaxAvlHeight = AvlTree<HistoryKey, double>.MaxHeightFor(tree.Count),
                    NodesVisited = visited,
                    AggregateNodesVisited = aggregate.NodesVisited,
                    TreeMicroseconds = Math.Round(best, 1),
                    LinearComparisons = tree.Count
                }
            };

            // Split the fetched readings into the page itself and the older neighbour used only for its delta.
            KeyValuePair<HistoryKey, double>? olderNeighbour = null;
            if (!newestFirst && skip > 0 && page.Count > 0) { olderNeighbour = page[0]; page.RemoveAt(0); }
            if (page.Count > take)
            {
                if (newestFirst) olderNeighbour = page[^1];
                page.RemoveRange(take, page.Count - take);
            }

            // Rows, each with the change from the chronologically previous reading (which may sit just off the page).
            for (var i = 0; i < page.Count; i++)
            {
                var previousIndex = newestFirst ? i + 1 : i - 1;
                double? previous = previousIndex >= 0 && previousIndex < page.Count ? page[previousIndex].Value
                    : olderNeighbour?.Value;
                double? delta = previous is { } p ? Math.Round(page[i].Value - p, 3) : null;
                result.Rows.Add(new HistoryRow { Timestamp = page[i].Key.Timestamp, Value = Math.Round(page[i].Value, 3), Delta = delta });
            }

            if (bucketMinutes > 0 && total > 0)
            {
                var (buckets, usedMinutes) = Buckets(tree, from, to, bucketMinutes);
                result.Buckets = buckets;
                result.BucketMinutes = usedMinutes;
            }

            if (compareLinear)
                result.Stats.LinearMicroseconds = TimeLinearScan(tree, from.UtcTicks, to.UtcTicks);

            return result;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Fixed-width time buckets, each answered by one O(log n) aggregate on the tree.</summary>
    private static (List<HistoryBucket> Buckets, int Minutes) Buckets(AvlTree<HistoryKey, double> tree,
        DateTimeOffset from, DateTimeOffset to, int bucketMinutes)
    {
        var minutes = Math.Max(1, bucketMinutes);
        while ((to - from).TotalMinutes / minutes > MaxBuckets) minutes *= 2;     // widen rather than return thousands

        var buckets = new List<HistoryBucket>();
        var width = TimeSpan.FromMinutes(minutes);
        for (var start = from; start < to; start += width)
        {
            var end = start + width >= to ? to : start + width;
            // Half-open [start, end) so a reading on a boundary is counted once; the last bucket includes 'to'.
            var upper = end == to ? HistoryKey.Highest(to) : new HistoryKey(end.UtcTicks - 1, long.MaxValue);
            var agg = tree.Aggregate(HistoryKey.Lowest(start), upper);
            buckets.Add(new HistoryBucket
            {
                Start = start,
                End = end,
                Count = agg.Count,
                Min = agg.Count > 0 ? Math.Round(agg.Min, 3) : null,
                Average = agg.Count > 0 ? Math.Round(agg.Average, 3) : null,
                Max = agg.Count > 0 ? Math.Round(agg.Max, 3) : null
            });
        }
        return (buckets, minutes);
    }

    /// <summary>
    /// The baseline the tree replaces: copy the readings into a flat array (not timed), then scan every element and
    /// test its timestamp, as an unindexed list would have to.
    /// </summary>
    private static double TimeLinearScan(AvlTree<HistoryKey, double> tree, long fromTicks, long toTicks)
    {
        var flat = new (long Ticks, double Value)[tree.Count];
        var i = 0;
        foreach (var kv in tree) flat[i++] = (kv.Key.UtcTicks, kv.Value);

        var best = double.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            var t0 = Stopwatch.GetTimestamp();
            var (matches, sum, min, max) = Scan(flat, fromTicks, toTicks);
            best = Math.Min(best, Stopwatch.GetElapsedTime(t0).TotalMicroseconds);
            GC.KeepAlive((matches, sum, min, max));
        }
        return Math.Round(best, 1);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static (int, double, double, double) Scan((long Ticks, double Value)[] flat, long fromTicks, long toTicks)
    {
        int matches = 0; double sum = 0, min = double.MaxValue, max = double.MinValue;
        foreach (var (ticks, value) in flat)
        {
            if (ticks < fromTicks || ticks > toTicks) continue;
            matches++; sum += value;
            if (value < min) min = value;
            if (value > max) max = value;
        }
        return (matches, sum, min, max);
    }

    public HistoryIndexStats Stats()
    {
        _lock.EnterReadLock();
        try
        {
            var stats = new HistoryIndexStats { Sensors = _series.Count, Evicted = _evicted, MaxPointsPerSensor = MaxPointsPerSensor };
            foreach (var s in _series.Values)
            {
                stats.TotalReadings += s.Tree.Count;
                stats.TallestTree = Math.Max(stats.TallestTree, s.Tree.Height);
                stats.Rotations += s.Tree.Rotations;
                if (s.Tree.Min() is { } first && (stats.Oldest is null || first.Key.Timestamp < stats.Oldest)) stats.Oldest = first.Key.Timestamp;
                if (s.Tree.Max() is { } last && (stats.Newest is null || last.Key.Timestamp > stats.Newest)) stats.Newest = last.Key.Timestamp;
            }
            return stats;
        }
        finally { _lock.ExitReadLock(); }
    }
}
