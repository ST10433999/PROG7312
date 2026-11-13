using SmartX.Shared.Collections;

namespace SmartX.Tests;

/// <summary>Part 3 — the AVL tree is checked against SortedDictionary / LINQ as the reference implementation.</summary>
public class AvlTreeTests
{
    [Fact]
    public void Sorted_inserts_stay_logarithmic()
    {
        // Timestamps arrive in order: a plain BST would reach height 100 000 here.
        var tree = new AvlTree<int, int>();
        for (var i = 0; i < 100_000; i++) tree.AddOrUpdate(i, i);

        Assert.Equal(100_000, tree.Count);
        Assert.True(tree.Height <= AvlTree<int, int>.MaxHeightFor(tree.Count), $"height {tree.Height}");
        Assert.True(tree.Validate(out var problem), problem);
        Assert.True(tree.Rotations > 0);
    }

    [Fact]
    public void Random_inserts_and_deletes_match_SortedDictionary()
    {
        var rng = new Random(1);
        var tree = new AvlTree<int, string>();
        var reference = new SortedDictionary<int, string>();

        for (var i = 0; i < 20_000; i++)
        {
            var key = rng.Next(5_000);
            if (rng.NextDouble() < 0.65)
            {
                var added = tree.AddOrUpdate(key, $"v{i}");
                Assert.Equal(!reference.ContainsKey(key), added);
                reference[key] = $"v{i}";
            }
            else
            {
                Assert.Equal(reference.Remove(key), tree.Remove(key));
            }
        }

        Assert.True(tree.Validate(out var problem), problem);
        Assert.Equal(reference.Count, tree.Count);
        Assert.Equal(reference.ToList(), tree.ToList());
        Assert.True(tree.Height <= AvlTree<int, string>.MaxHeightFor(tree.Count));
    }

    [Fact]
    public void Range_paging_matches_linq_in_both_directions()
    {
        var rng = new Random(2);
        var tree = new AvlTree<int, int>();
        var keys = Enumerable.Range(0, 3_000).Select(_ => rng.Next(100_000)).Distinct().ToList();
        foreach (var k in keys) tree.AddOrUpdate(k, k * 2);
        var sorted = keys.OrderBy(k => k).ToList();

        for (var q = 0; q < 400; q++)
        {
            var a = rng.Next(100_000);
            var b = rng.Next(100_000);
            var (from, to) = (Math.Min(a, b), Math.Max(a, b));
            var skip = rng.Next(0, 50);
            var take = rng.Next(1, 80);
            var descending = q % 2 == 0;

            var inRange = sorted.Where(k => k >= from && k <= to);
            var expected = (descending ? inRange.Reverse() : inRange).Skip(skip).Take(take).ToList();

            var page = new List<KeyValuePair<int, int>>();
            tree.Range(from, to, page, skip, take, descending);

            Assert.Equal(expected, page.Select(p => p.Key).ToList());
            Assert.Equal(sorted.Count(k => k >= from && k <= to), tree.CountRange(from, to));
        }
    }

    [Fact]
    public void Range_skips_whole_subtrees_when_paging_deep()
    {
        var tree = new AvlTree<int, int>();
        for (var i = 0; i < 100_000; i++) tree.AddOrUpdate(i, i);

        var page = new List<KeyValuePair<int, int>>();
        var visited = tree.Range(0, 99_999, page, skip: 90_000, take: 10);

        Assert.Equal(Enumerable.Range(90_000, 10), page.Select(p => p.Key));
        Assert.True(visited < 200, $"visited {visited} nodes; a linear walk would visit 90 010");
    }

    [Fact]
    public void Aggregates_match_linq()
    {
        var rng = new Random(3);
        var tree = new AvlTree<int, double>(v => v);
        var data = new Dictionary<int, double>();
        for (var i = 0; i < 5_000; i++)
        {
            var k = rng.Next(50_000);
            var v = Math.Round(rng.NextDouble() * 100, 3);
            tree.AddOrUpdate(k, v);
            data[k] = v;
        }

        for (var q = 0; q < 300; q++)
        {
            var a = rng.Next(50_000);
            var b = rng.Next(50_000);
            var (from, to) = (Math.Min(a, b), Math.Max(a, b));
            var expected = data.Where(kv => kv.Key >= from && kv.Key <= to).Select(kv => kv.Value).ToList();
            var agg = tree.Aggregate(from, to);

            Assert.Equal(expected.Count, agg.Count);
            if (expected.Count == 0) continue;
            Assert.Equal(expected.Sum(), agg.Sum, 6);
            Assert.Equal(expected.Min(), agg.Min, 6);
            Assert.Equal(expected.Max(), agg.Max, 6);
            Assert.True(agg.NodesVisited <= 4 * (tree.Height + 2), $"visited {agg.NodesVisited}");
        }
    }

    [Fact]
    public void Select_rank_and_remove_min()
    {
        var tree = new AvlTree<int, int>();
        foreach (var k in new[] { 50, 20, 80, 10, 30, 70, 90 }) tree.AddOrUpdate(k, k);

        Assert.Equal(30, tree.ElementAt(2).Key);
        Assert.Equal(2, tree.CountLessThan(30));                  // 10, 20
        Assert.Equal(3, tree.CountLessOrEqual(30));               // 10, 20, 30

        var drained = new List<int>();
        while (tree.TryRemoveMin(out var key, out _)) drained.Add(key);
        Assert.Equal(new[] { 10, 20, 30, 50, 70, 80, 90 }, drained);
        Assert.Equal(0, tree.Count);
    }
}
