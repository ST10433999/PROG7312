using SmartX.Api.History;

namespace SmartX.Tests;

/// <summary>Part 3 — range queries over the per-sensor AVL history index.</summary>
public class HistoryIndexTests
{
    private const string Mac = "A1:2F:00:00:00:01";
    private static readonly DateTimeOffset Day = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);

    private static TelemetryHistoryIndex SevenDaysEveryFiveMinutes()
    {
        var index = new TelemetryHistoryIndex { MaxPointsPerSensor = 100_000 };
        var readings = new List<(string, DateTimeOffset, double, string)>();
        for (var t = Day; t < Day.AddDays(7); t += TimeSpan.FromMinutes(5))
            readings.Add((Mac, t, t.Hour, "%"));          // value = hour of day, easy to check
        index.RecordMany(readings);
        return index;
    }

    [Fact]
    public void Two_hour_window_returns_exactly_those_readings_newest_first()
    {
        var index = SevenDaysEveryFiveMinutes();
        var from = Day.AddDays(3).AddHours(14);
        var to = Day.AddDays(3).AddHours(16);

        var result = index.Query(Mac, from, to, take: 500)!;

        Assert.Equal(25, result.TotalInRange);                      // 14:00, 14:05 … 16:00 inclusive
        Assert.Equal(25, result.Rows.Count);
        Assert.Equal(to, result.Rows[0].Timestamp);
        Assert.Equal(from, result.Rows[^1].Timestamp);
        Assert.All(result.Rows, r => Assert.InRange(r.Timestamp, from, to));
        Assert.Equal(14.0, result.Min);
        Assert.Equal(16.0, result.Max);
        Assert.True(result.Stats.NodesVisited < 80, $"visited {result.Stats.NodesVisited} of {result.Stats.SeriesSize}");
        Assert.True(result.Stats.TreeHeight <= result.Stats.MaxAvlHeight);
    }

    [Fact]
    public void Paging_and_ascending_order()
    {
        var index = SevenDaysEveryFiveMinutes();
        var from = Day.AddDays(1);
        var to = Day.AddDays(2);

        var page1 = index.Query(Mac, from, to, skip: 0, take: 10, newestFirst: false)!;
        var page2 = index.Query(Mac, from, to, skip: 10, take: 10, newestFirst: false)!;

        Assert.Equal(from, page1.Rows[0].Timestamp);
        Assert.Equal(from.AddMinutes(50), page2.Rows[0].Timestamp);
        Assert.Equal(289, page1.TotalInRange);
        Assert.Null(page1.Rows[0].Delta);                       // first reading of the range: nothing before it
        Assert.Equal(0.0, page1.Rows[1].Delta);
        Assert.NotNull(page2.Rows[0].Delta);                    // page edge: delta from the reading on the previous page
        Assert.Equal(10, page2.Rows.Count);
    }

    [Fact]
    public void Newest_first_pages_have_a_delta_on_every_row_including_the_page_edge()
    {
        var index = new TelemetryHistoryIndex();
        for (var i = 0; i < 30; i++) index.Record(Mac, Day.AddMinutes(i), i * 2);   // value rises by 2 each minute

        var page = index.Query(Mac, Day, Day.AddHours(1), skip: 0, take: 10, newestFirst: true)!;

        Assert.Equal(10, page.Rows.Count);
        Assert.Equal(Day.AddMinutes(29), page.Rows[0].Timestamp);
        Assert.All(page.Rows, r => Assert.Equal(2.0, r.Delta));     // the oldest row uses the reading just off the page
    }

    [Fact]
    public void Hourly_buckets_cover_every_reading_once()
    {
        var index = SevenDaysEveryFiveMinutes();
        var from = Day.AddDays(2);
        var to = Day.AddDays(2).AddHours(24);

        var result = index.Query(Mac, from, to, bucketMinutes: 60)!;

        Assert.Equal(24, result.Buckets.Count);
        Assert.Equal(result.TotalInRange, result.Buckets.Sum(b => b.Count));
        Assert.All(result.Buckets.Take(23), b => Assert.Equal(12, b.Count));   // 12 five-minute readings per hour
        Assert.Equal(13, result.Buckets[^1].Count);                           // last bucket also includes 'to'
        Assert.Equal(15.0, result.Buckets[15].Average);
    }

    [Fact]
    public void Retention_evicts_the_oldest_readings()
    {
        var index = new TelemetryHistoryIndex { MaxPointsPerSensor = 100 };
        for (var i = 0; i < 250; i++) index.Record(Mac, Day.AddMinutes(i), i);

        var all = index.Query(Mac, Day, Day.AddDays(1), take: 500, newestFirst: false)!;
        Assert.Equal(100, all.TotalInRange);
        Assert.Equal(Day.AddMinutes(150), all.Rows[0].Timestamp);
        Assert.Equal(150, index.Stats().Evicted);
    }

    [Fact]
    public void Unknown_sensor_returns_null_and_linear_comparison_is_measured()
    {
        var index = SevenDaysEveryFiveMinutes();
        Assert.Null(index.Query("00:00:00:00:00:00", Day, Day.AddDays(1)));

        var result = index.Query(Mac, Day, Day.AddDays(1), compareLinear: true)!;
        Assert.NotNull(result.Stats.LinearMicroseconds);
        Assert.Equal(result.Stats.SeriesSize, result.Stats.LinearComparisons);
    }

    [Fact]
    public void Benchmark_results_match_the_linear_scan()
    {
        var bench = HistoryBenchmark.Run(points: 50_000, queries: 50);
        Assert.True(bench.ResultsMatch);
        Assert.True(bench.TreeHeight <= bench.MaxAvlHeight);
        Assert.True(bench.AverageMatches > 0);
    }
}
