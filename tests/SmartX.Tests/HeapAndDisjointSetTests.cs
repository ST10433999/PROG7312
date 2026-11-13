using SmartX.Shared.Collections;

namespace SmartX.Tests;

public class HeapAndDisjointSetTests
{
    [Fact]
    public void Heap_pops_in_ascending_order()
    {
        var rng = new Random(4);
        var heap = new BinaryMinHeap<int>();
        var values = Enumerable.Range(0, 10_000).Select(_ => rng.Next(1_000_000)).ToList();
        foreach (var v in values)
        {
            heap.Push(v);
        }
        Assert.True(heap.IsValidHeap());

        var popped = new List<int>();
        while (heap.TryPop(out var v)) popped.Add(v);
        Assert.Equal(values.OrderBy(v => v), popped);
        Assert.Equal(20_000, heap.Operations);
    }

    [Fact]
    public void Heap_uses_custom_comparison()
    {
        var heap = new BinaryMinHeap<(double Cost, string Name)>((a, b) => a.Cost.CompareTo(b.Cost));
        heap.Push((3.5, "c"));
        heap.Push((1.2, "a"));
        heap.Push((2.0, "b"));
        Assert.Equal("a", heap.Peek().Name);
        Assert.Equal(new[] { "a", "b", "c" }, new[] { heap.Pop().Name, heap.Pop().Name, heap.Pop().Name });
        Assert.Throws<InvalidOperationException>(() => heap.Pop());
    }

    [Fact]
    public void Disjoint_set_rejects_cycles()
    {
        var sets = new DisjointSet(5);
        Assert.True(sets.Union(0, 1));
        Assert.True(sets.Union(1, 2));
        Assert.False(sets.Union(0, 2));           // would close the cycle 0-1-2
        Assert.True(sets.Connected(0, 2));
        Assert.False(sets.Connected(0, 3));
        Assert.Equal(3, sets.Sets);
    }
}
