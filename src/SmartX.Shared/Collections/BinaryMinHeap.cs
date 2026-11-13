namespace SmartX.Shared.Collections;

/// <summary>
/// Part 3 — an array-backed binary <b>min-heap</b> written from scratch.
/// <para>
/// The heap is a complete binary tree stored level by level in a <see cref="List{T}"/>: the children of index
/// <c>i</c> are <c>2i+1</c> and <c>2i+2</c>, and its parent is <c>(i−1)/2</c>. No pointers are needed. Every
/// parent is ≤ its children, so the cheapest item is always at index 0.
/// <list type="bullet">
/// <item><see cref="Push"/>: append, then sift up. O(log n).</item>
/// <item><see cref="Pop"/>: move the last item to the root, then sift down. O(log n).</item>
/// <item><see cref="Peek"/>: O(1).</item>
/// </list>
/// Prim's MST and Dijkstra's shortest-path both pull "the cheapest radio link not yet used" out of this
/// heap. That is why they run in O(E log V) instead of the O(V²) of scanning every candidate.
/// </para>
/// </summary>
public sealed class BinaryMinHeap<T>
{
    private readonly List<T> _items;
    private readonly IComparer<T> _comparer;

    public BinaryMinHeap(IComparer<T>? comparer = null, int capacity = 16)
    {
        _comparer = comparer ?? Comparer<T>.Default;
        _items = new List<T>(capacity);
    }

    public BinaryMinHeap(Comparison<T> comparison, int capacity = 16)
        : this(Comparer<T>.Create(comparison), capacity) { }

    public int Count => _items.Count;

    /// <summary>Total pushes + pops, reported by the MST endpoint as evidence of the O(E log V) work done.</summary>
    public long Operations { get; private set; }

    public void Push(T item)
    {
        Operations++;
        _items.Add(item);
        SiftUp(_items.Count - 1);
    }

    public T Peek() => _items.Count > 0 ? _items[0] : throw new InvalidOperationException("The heap is empty.");

    public T Pop()
    {
        if (_items.Count == 0) throw new InvalidOperationException("The heap is empty.");
        Operations++;
        var top = _items[0];
        var last = _items[^1];
        _items.RemoveAt(_items.Count - 1);
        if (_items.Count > 0)
        {
            _items[0] = last;
            SiftDown(0);
        }
        return top;
    }

    public bool TryPop(out T item)
    {
        if (_items.Count == 0) { item = default!; return false; }
        item = Pop();
        return true;
    }

    public void Clear() => _items.Clear();

    private void SiftUp(int i)
    {
        while (i > 0)
        {
            var parent = (i - 1) / 2;
            if (_comparer.Compare(_items[i], _items[parent]) >= 0) return;
            (_items[i], _items[parent]) = (_items[parent], _items[i]);
            i = parent;
        }
    }

    private void SiftDown(int i)
    {
        var n = _items.Count;
        while (true)
        {
            var left = 2 * i + 1;
            if (left >= n) return;
            var right = left + 1;
            var smallest = right < n && _comparer.Compare(_items[right], _items[left]) < 0 ? right : left;
            if (_comparer.Compare(_items[smallest], _items[i]) >= 0) return;
            (_items[i], _items[smallest]) = (_items[smallest], _items[i]);
            i = smallest;
        }
    }

    /// <summary>Checks the heap property for every parent/child pair (unit tests).</summary>
    public bool IsValidHeap()
    {
        for (var i = 1; i < _items.Count; i++)
            if (_comparer.Compare(_items[i], _items[(i - 1) / 2]) < 0) return false;
        return true;
    }
}
