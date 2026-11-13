namespace SmartX.Shared.History;

// -------------------------------------------------------------------------------------------------
// Part 3 — historical telemetry served from the per-sensor AVL tree index.
// -------------------------------------------------------------------------------------------------

/// <summary>One reading in the historical log.</summary>
public sealed class HistoryRow
{
    public DateTimeOffset Timestamp { get; set; }
    public double Value { get; set; }
    /// <summary>Change from the previous reading in the page (null for the first row).</summary>
    public double? Delta { get; set; }
}

/// <summary>Min/avg/max over a fixed time bucket (e.g. one hour). Computed from the tree's subtree aggregates.</summary>
public sealed class HistoryBucket
{
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public int Count { get; set; }
    public double? Min { get; set; }
    public double? Average { get; set; }
    public double? Max { get; set; }
}

/// <summary>How the query was answered, shown under the log so the cost of the range query is visible.</summary>
public sealed class HistoryQueryStats
{
    /// <summary>Readings stored for this sensor (the tree's Count).</summary>
    public int SeriesSize { get; set; }
    public int TreeHeight { get; set; }
    /// <summary>The AVL height bound for this many keys, ≈1.44·log₂(n).</summary>
    public int MaxAvlHeight { get; set; }
    /// <summary>Tree nodes touched to fetch this page.</summary>
    public int NodesVisited { get; set; }
    /// <summary>Tree nodes touched to compute count/min/avg/max over the whole range.</summary>
    public int AggregateNodesVisited { get; set; }
    public double TreeMicroseconds { get; set; }
    /// <summary>Elements a linear scan of an unsorted list would have to compare (= every stored reading).</summary>
    public int LinearComparisons { get; set; }
    /// <summary>Measured time of that linear scan over the same readings, when requested.</summary>
    public double? LinearMicroseconds { get; set; }
}

public sealed class HistoryQueryResult
{
    public string MacAddress { get; set; } = "";
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    /// <summary>Readings inside [From, To]. From the tree's size augmentation, without enumerating them.</summary>
    public int TotalInRange { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
    public bool NewestFirst { get; set; }
    public double? Min { get; set; }
    public double? Average { get; set; }
    public double? Max { get; set; }
    public DateTimeOffset? FirstReading { get; set; }
    public DateTimeOffset? LastReading { get; set; }
    public List<HistoryRow> Rows { get; set; } = new();
    public List<HistoryBucket> Buckets { get; set; } = new();
    public int BucketMinutes { get; set; }
    public HistoryQueryStats Stats { get; set; } = new();
}

/// <summary>Whole-index numbers for the dashboard header.</summary>
public sealed class HistoryIndexStats
{
    public int Sensors { get; set; }
    public long TotalReadings { get; set; }
    public int TallestTree { get; set; }
    public long Rotations { get; set; }
    public long Evicted { get; set; }
    public int MaxPointsPerSensor { get; set; }
    public DateTimeOffset? Oldest { get; set; }
    public DateTimeOffset? Newest { get; set; }
}

/// <summary>Result of <c>GET /api/history/benchmark</c>: AVL range queries vs scanning an unsorted list.</summary>
public sealed class HistoryBenchmarkResult
{
    public int Points { get; set; }
    public int Queries { get; set; }
    public double WindowHours { get; set; }
    public double BuildMilliseconds { get; set; }
    public int TreeHeight { get; set; }
    public int MaxAvlHeight { get; set; }
    public long Rotations { get; set; }
    public double AverageMatches { get; set; }
    public double AvlCountMicroseconds { get; set; }
    public double AvlAggregateMicroseconds { get; set; }
    public double AvlFetchMicroseconds { get; set; }
    public double LinearScanMicroseconds { get; set; }
    public double Speedup { get; set; }
    public bool ResultsMatch { get; set; }
}
