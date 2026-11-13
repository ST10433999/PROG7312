using System.Diagnostics;
using SmartX.Shared.Collections;
using SmartX.Shared.History;

namespace SmartX.Api.History;

/// <summary>
/// Part 3 — measures how the AVL range query scales against a linear scan of an unsorted list. The two hold the
/// same synthetic readings, so the README and report can quote real numbers rather than just Big-O.
/// </summary>
public static class HistoryBenchmark
{
    public const int MaxPoints = 2_000_000;

    public static HistoryBenchmarkResult Run(int points = 200_000, int queries = 200, double windowHours = 2, int seed = 42)
    {
        points = Math.Clamp(points, 1_000, MaxPoints);
        queries = Math.Clamp(queries, 1, 2_000);
        windowHours = Math.Clamp(windowHours, 0.05, 72);

        var rng = new Random(seed);
        var span = TimeSpan.FromDays(30).Ticks;
        var start = DateTimeOffset.UtcNow.AddDays(-30).UtcTicks;
        var step = span / points;

        // Readings arrive in time order, exactly how telemetry is produced, and the worst case for a plain BST.
        var tree = new AvlTree<HistoryKey, double>(static v => v);
        var list = new (long Ticks, double Value)[points];
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < points; i++)
        {
            var ticks = start + i * step + rng.NextInt64(0, Math.Max(1, step / 2));
            var value = 40 + rng.NextDouble() * 20;
            tree.AddOrUpdate(new HistoryKey(ticks, i), value);
            list[i] = (ticks, value);
        }
        sw.Stop();
        var buildMs = sw.Elapsed.TotalMilliseconds;

        // The "unorganised list" baseline: same readings in arrival order from many sensors (shuffled).
        for (var i = list.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        var window = TimeSpan.FromHours(windowHours).Ticks;
        var windows = new (long From, long To)[queries];
        for (var q = 0; q < queries; q++)
        {
            var from = start + rng.NextInt64(0, Math.Max(1, span - window));
            windows[q] = (from, from + window);
        }

        // Warm the JIT so the first timed query is not penalised.
        RunTree(tree, windows[0]);
        RunLinear(list, windows[0]);

        double countTicks = 0, aggTicks = 0, fetchTicks = 0, linearTicks = 0, matches = 0;
        var allMatch = true;
        var page = new List<KeyValuePair<HistoryKey, double>>();
        foreach (var (from, to) in windows)
        {
            var lo = new HistoryKey(from, long.MinValue);
            var hi = new HistoryKey(to, long.MaxValue);

            var t0 = Stopwatch.GetTimestamp();
            var count = tree.CountRange(lo, hi);
            var t1 = Stopwatch.GetTimestamp();
            var agg = tree.Aggregate(lo, hi);
            var t2 = Stopwatch.GetTimestamp();
            page.Clear();
            tree.Range(lo, hi, page);
            var t3 = Stopwatch.GetTimestamp();
            var (linearCount, linearSum) = RunLinear(list, (from, to));
            var t4 = Stopwatch.GetTimestamp();

            countTicks += t1 - t0;
            aggTicks += t2 - t1;
            fetchTicks += t3 - t2;
            linearTicks += t4 - t3;
            matches += count;
            if (count != linearCount || page.Count != count || Math.Abs(agg.Sum - linearSum) > 1e-6 * Math.Max(1, Math.Abs(linearSum)))
                allMatch = false;
        }

        double Micro(double ticks) => Math.Round(ticks * 1_000_000.0 / Stopwatch.Frequency / queries, 2);
        var linear = Micro(linearTicks);
        var fetch = Micro(fetchTicks);

        return new HistoryBenchmarkResult
        {
            Points = points,
            Queries = queries,
            WindowHours = windowHours,
            BuildMilliseconds = Math.Round(buildMs, 1),
            TreeHeight = tree.Height,
            MaxAvlHeight = AvlTree<HistoryKey, double>.MaxHeightFor(points),
            Rotations = tree.Rotations,
            AverageMatches = Math.Round(matches / queries, 1),
            AvlCountMicroseconds = Micro(countTicks),
            AvlAggregateMicroseconds = Micro(aggTicks),
            AvlFetchMicroseconds = fetch,
            LinearScanMicroseconds = linear,
            Speedup = fetch > 0 ? Math.Round(linear / fetch, 1) : 0,
            ResultsMatch = allMatch
        };
    }

    private static int RunTree(AvlTree<HistoryKey, double> tree, (long From, long To) w) =>
        tree.CountRange(new HistoryKey(w.From, long.MinValue), new HistoryKey(w.To, long.MaxValue));

    private static (int Count, double Sum) RunLinear((long Ticks, double Value)[] list, (long From, long To) w)
    {
        var count = 0;
        var sum = 0.0;
        foreach (var (ticks, value) in list)
        {
            if (ticks < w.From || ticks > w.To) continue;
            count++;
            sum += value;
        }
        return (count, sum);
    }
}
