using System.Collections;
using System.Runtime.CompilerServices;

namespace SmartX.Shared.Collections;

/// <summary>
/// Part 3 — a self-balancing <b>AVL tree</b> (Adelson-Velsky &amp; Landis, 1962) written from scratch.
/// <para>
/// Every node stores the height of its subtree, so after each insert or delete the tree checks the
/// balance factor (left height − right height) on the way back up and repairs any node where it
/// reaches ±2 with one or two rotations. The height therefore never exceeds ≈1.44·log₂(n), which keeps
/// lookups, inserts, deletes and the start of a range query at O(log n) even when keys arrive in sorted
/// order. Telemetry timestamps always arrive in sorted order, so a plain BST would degrade into a
/// linked list of depth n.
/// </para>
/// <para>
/// The tree is <b>augmented</b> with two extra fields per node:
/// <list type="bullet">
/// <item><c>Size</c>: number of nodes in the subtree. This gives O(log n) range counts, rank and select,
/// and lets a paged query skip whole subtrees.</item>
/// <item><c>Sum/Min/Max</c> of an optional numeric measure. Range aggregates (avg, min, max over 14:00–16:00)
/// then cost O(log n) with no per-reading loop.</item>
/// </list>
/// </para>
/// </summary>
public sealed class AvlTree<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>
    where TKey : IComparable<TKey>
{
    private sealed class Node(TKey key, TValue value, double measure)
    {
        public TKey Key = key;
        public TValue Value = value;
        public Node? Left;
        public Node? Right;
        public int Height = 1;
        public int Size = 1;
        public double Own = measure;
        public double Sum = measure;
        public double Min = measure;
        public double Max = measure;
    }

    private readonly Func<TValue, double>? _measure;
    private Node? _root;

    /// <param name="measure">Optional numeric projection of the value. When supplied, every node keeps the
    /// sum, min and max of its subtree so <see cref="Aggregate"/> runs in O(log n).</param>
    public AvlTree(Func<TValue, double>? measure = null) => _measure = measure;

    /// <summary>Number of entries in the tree. O(1).</summary>
    public int Count => SizeOf(_root);

    /// <summary>Height of the tree (0 when empty). Stays at or below 1.44·log₂(n + 2).</summary>
    public int Height => HeightOf(_root);

    /// <summary>Total single rotations performed since the tree was created (a double rotation counts as 2).</summary>
    public long Rotations { get; private set; }

    /// <summary>The theoretical AVL height bound for <paramref name="n"/> entries: ⌊1.4405·log₂(n + 2) − 0.3277⌋.</summary>
    public static int MaxHeightFor(int n) => n <= 0 ? 0 : (int)Math.Floor(1.4405 * Math.Log2(n + 2) - 0.3277);

    // ================================================================== insert

    /// <summary>Inserts the key, or replaces its value if it already exists. Returns true when a new key was added.</summary>
    public bool AddOrUpdate(TKey key, TValue value)
    {
        var added = false;
        _root = Insert(_root, key, value, ref added);
        return added;
    }

    private Node Insert(Node? node, TKey key, TValue value, ref bool added)
    {
        if (node is null)
        {
            added = true;
            return new Node(key, value, _measure?.Invoke(value) ?? 0);
        }

        var c = key.CompareTo(node.Key);
        if (c < 0) node.Left = Insert(node.Left, key, value, ref added);
        else if (c > 0) node.Right = Insert(node.Right, key, value, ref added);
        else
        {
            node.Value = value;
            node.Own = _measure?.Invoke(value) ?? 0;
            Update(node);
            return node;
        }
        return Rebalance(node);
    }

    // ================================================================== delete

    /// <summary>Removes the key. Returns true when it was present.</summary>
    public bool Remove(TKey key)
    {
        var removed = false;
        _root = Delete(_root, key, ref removed);
        return removed;
    }

    /// <summary>Removes and returns the smallest entry (used for retention, i.e. dropping the oldest reading). O(log n).</summary>
    public bool TryRemoveMin(out TKey key, out TValue value)
    {
        if (_root is null) { key = default!; value = default!; return false; }
        var n = _root;
        while (n.Left is not null) n = n.Left;
        key = n.Key;
        value = n.Value;
        Remove(key);
        return true;
    }

    private Node? Delete(Node? node, TKey key, ref bool removed)
    {
        if (node is null) return null;

        var c = key.CompareTo(node.Key);
        if (c < 0) node.Left = Delete(node.Left, key, ref removed);
        else if (c > 0) node.Right = Delete(node.Right, key, ref removed);
        else
        {
            removed = true;
            if (node.Left is null) return node.Right;
            if (node.Right is null) return node.Left;

            // Two children: copy the in-order successor up, then delete it from the right subtree.
            var successor = node.Right;
            while (successor.Left is not null) successor = successor.Left;
            node.Key = successor.Key;
            node.Value = successor.Value;
            node.Own = successor.Own;
            var ignored = false;
            node.Right = Delete(node.Right, successor.Key, ref ignored);
        }
        return Rebalance(node);
    }

    // ================================================================== balancing

    private static int HeightOf(Node? n) => n?.Height ?? 0;
    private static int SizeOf(Node? n) => n?.Size ?? 0;

    private static void Update(Node n)
    {
        n.Height = 1 + Math.Max(HeightOf(n.Left), HeightOf(n.Right));
        n.Size = 1 + SizeOf(n.Left) + SizeOf(n.Right);
        n.Sum = n.Own;
        n.Min = n.Own;
        n.Max = n.Own;
        if (n.Left is { } l) { n.Sum += l.Sum; n.Min = Math.Min(n.Min, l.Min); n.Max = Math.Max(n.Max, l.Max); }
        if (n.Right is { } r) { n.Sum += r.Sum; n.Min = Math.Min(n.Min, r.Min); n.Max = Math.Max(n.Max, r.Max); }
    }

    private Node Rebalance(Node n)
    {
        Update(n);
        var balance = HeightOf(n.Left) - HeightOf(n.Right);

        if (balance > 1)                                         // left-heavy
        {
            if (HeightOf(n.Left!.Left) < HeightOf(n.Left.Right))
                n.Left = RotateLeft(n.Left);                     // left-right case
            return RotateRight(n);                               // left-left case
        }
        if (balance < -1)                                        // right-heavy
        {
            if (HeightOf(n.Right!.Right) < HeightOf(n.Right.Left))
                n.Right = RotateRight(n.Right);                  // right-left case
            return RotateLeft(n);                                // right-right case
        }
        return n;
    }

    private Node RotateRight(Node y)
    {
        var x = y.Left!;
        y.Left = x.Right;
        x.Right = y;
        Update(y);
        Update(x);
        Rotations++;
        return x;
    }

    private Node RotateLeft(Node x)
    {
        var y = x.Right!;
        x.Right = y.Left;
        y.Left = x;
        Update(x);
        Update(y);
        Rotations++;
        return y;
    }

    // ================================================================== point queries

    public bool TryGetValue(TKey key, out TValue value)
    {
        var n = _root;
        while (n is not null)
        {
            var c = key.CompareTo(n.Key);
            if (c == 0) { value = n.Value; return true; }
            n = c < 0 ? n.Left : n.Right;
        }
        value = default!;
        return false;
    }

    public bool ContainsKey(TKey key) => TryGetValue(key, out _);

    public KeyValuePair<TKey, TValue>? Min()
    {
        var n = _root;
        if (n is null) return null;
        while (n.Left is not null) n = n.Left;
        return new(n.Key, n.Value);
    }

    public KeyValuePair<TKey, TValue>? Max()
    {
        var n = _root;
        if (n is null) return null;
        while (n.Right is not null) n = n.Right;
        return new(n.Key, n.Value);
    }

    /// <summary>Number of keys strictly less than <paramref name="key"/> (its rank). O(log n) thanks to the Size field.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // hot query path: compile fully optimised straight away
    public int CountLessThan(TKey key)
    {
        var count = 0;
        var n = _root;
        while (n is not null)
        {
            if (key.CompareTo(n.Key) <= 0) n = n.Left;
            else { count += SizeOf(n.Left) + 1; n = n.Right; }
        }
        return count;
    }

    /// <summary>Number of keys less than or equal to <paramref name="key"/>. O(log n).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // hot query path: compile fully optimised straight away
    public int CountLessOrEqual(TKey key)
    {
        var count = 0;
        var n = _root;
        while (n is not null)
        {
            if (key.CompareTo(n.Key) < 0) n = n.Left;
            else { count += SizeOf(n.Left) + 1; n = n.Right; }
        }
        return count;
    }

    /// <summary>How many keys fall in [from, to]. Two O(log n) rank walks and no enumeration.</summary>
    public int CountRange(TKey from, TKey to) =>
        from.CompareTo(to) > 0 ? 0 : CountLessOrEqual(to) - CountLessThan(from);

    /// <summary>The entry at zero-based in-order position <paramref name="index"/> (order-statistic select). O(log n).</summary>
    public KeyValuePair<TKey, TValue> ElementAt(int index)
    {
        if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
        var n = _root!;
        while (true)
        {
            var leftSize = SizeOf(n.Left);
            if (index < leftSize) n = n.Left!;
            else if (index == leftSize) return new(n.Key, n.Value);
            else { index -= leftSize + 1; n = n.Right!; }
        }
    }

    // ================================================================== range queries

    /// <summary>
    /// Appends the entries with keys in [<paramref name="from"/>, <paramref name="to"/>] to <paramref name="output"/>,
    /// in ascending or descending order, skipping the first <paramref name="skip"/> matches and stopping after
    /// <paramref name="take"/>.
    /// <para>
    /// Subtrees that lie wholly outside the range are pruned. A subtree that lies wholly inside the range and is
    /// smaller than the remaining skip is jumped over in O(1) using its Size. A page therefore costs
    /// O(log n + take) however deep into the range it starts.
    /// </para>
    /// </summary>
    /// <returns>The number of tree nodes visited, reported to the UI to show the pruning.</returns>
    public int Range(TKey from, TKey to, List<KeyValuePair<TKey, TValue>> output,
        int skip = 0, int take = int.MaxValue, bool descending = false)
    {
        if (from.CompareTo(to) > 0 || take <= 0) return 0;
        var cursor = new RangeCursor { Skip = Math.Max(0, skip), Remaining = take, Output = output };
        Walk(_root, from, to, false, false, descending, cursor);
        return cursor.Visited;
    }

    private sealed class RangeCursor
    {
        public int Skip;
        public int Remaining;
        public int Visited;
        public required List<KeyValuePair<TKey, TValue>> Output;
    }

    /// <param name="aboveFrom">every key in this subtree is already known to be ≥ from</param>
    /// <param name="belowTo">every key in this subtree is already known to be ≤ to</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // hot query path: compile fully optimised straight away
    private static void Walk(Node? n, TKey from, TKey to, bool aboveFrom, bool belowTo, bool descending, RangeCursor cursor)
    {
        if (n is null || cursor.Remaining == 0) return;

        // Whole subtree inside the range and entirely covered by the skip: jump it.
        if (aboveFrom && belowTo && cursor.Skip >= n.Size)
        {
            cursor.Skip -= n.Size;
            cursor.Visited++;
            return;
        }

        cursor.Visited++;
        var geFrom = aboveFrom || n.Key.CompareTo(from) >= 0;
        var leTo = belowTo || n.Key.CompareTo(to) <= 0;

        // Left keys are < n.Key: worth visiting only if n.Key ≥ from, and all ≤ to once n.Key ≤ to.
        // Right keys are > n.Key: worth visiting only if n.Key ≤ to, and all ≥ from once n.Key ≥ from.
        if (!descending)
        {
            if (geFrom) Walk(n.Left, from, to, aboveFrom, leTo, descending, cursor);
            Emit(n, geFrom, leTo, cursor);
            if (leTo) Walk(n.Right, from, to, geFrom, belowTo, descending, cursor);
        }
        else
        {
            if (leTo) Walk(n.Right, from, to, geFrom, belowTo, descending, cursor);
            Emit(n, geFrom, leTo, cursor);
            if (geFrom) Walk(n.Left, from, to, aboveFrom, leTo, descending, cursor);
        }
    }

    private static void Emit(Node n, bool geFrom, bool leTo, RangeCursor cursor)
    {
        if (!geFrom || !leTo || cursor.Remaining == 0) return;
        if (cursor.Skip > 0) { cursor.Skip--; return; }
        cursor.Output.Add(new(n.Key, n.Value));
        cursor.Remaining--;
    }

    /// <summary>Count, sum, min and max of the measure over [from, to]. O(log n) using the subtree aggregates.</summary>
    public RangeAggregate Aggregate(TKey from, TKey to)
    {
        if (_measure is null) throw new InvalidOperationException("Create the tree with a measure to use Aggregate.");
        if (from.CompareTo(to) > 0) return RangeAggregate.Empty;
        var visited = 0;
        var result = Aggregate(_root, from, to, false, false, ref visited);
        return result with { NodesVisited = visited };
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // hot query path: compile fully optimised straight away
    private static RangeAggregate Aggregate(Node? n, TKey from, TKey to, bool aboveFrom, bool belowTo, ref int visited)
    {
        if (n is null) return RangeAggregate.Empty;
        visited++;
        if (aboveFrom && belowTo) return new RangeAggregate(n.Size, n.Sum, n.Min, n.Max, 0);

        var geFrom = aboveFrom || n.Key.CompareTo(from) >= 0;
        var leTo = belowTo || n.Key.CompareTo(to) <= 0;
        if (!geFrom) return Aggregate(n.Right, from, to, aboveFrom, belowTo, ref visited);   // n < from
        if (!leTo) return Aggregate(n.Left, from, to, aboveFrom, belowTo, ref visited);      // n > to

        var left = Aggregate(n.Left, from, to, aboveFrom, true, ref visited);
        var right = Aggregate(n.Right, from, to, true, belowTo, ref visited);
        return left + new RangeAggregate(1, n.Own, n.Own, n.Own, 0) + right;
    }

    // ================================================================== enumeration & checks

    /// <summary>In-order (ascending) enumeration with an explicit stack, so deep trees never risk recursion limits.</summary>
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        var stack = new Stack<Node>();
        var n = _root;
        while (n is not null || stack.Count > 0)
        {
            while (n is not null) { stack.Push(n); n = n.Left; }
            n = stack.Pop();
            yield return new(n.Key, n.Value);
            n = n.Right;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Verifies the BST ordering, the AVL balance rule (|balance| ≤ 1) and the cached height and size of every node.
    /// Used by the unit tests after thousands of random inserts and deletes.
    /// </summary>
    public bool Validate(out string? problem)
    {
        problem = null;
        try
        {
            Check(_root, default, false, default, false);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            problem = ex.Message;
            return false;
        }
    }

    private static (int Height, int Size) Check(Node? n, TKey? lower, bool hasLower, TKey? upper, bool hasUpper)
    {
        if (n is null) return (0, 0);
        if (hasLower && n.Key.CompareTo(lower!) <= 0) throw new InvalidOperationException($"BST order broken at {n.Key}.");
        if (hasUpper && n.Key.CompareTo(upper!) >= 0) throw new InvalidOperationException($"BST order broken at {n.Key}.");
        var (lh, ls) = Check(n.Left, lower, hasLower, n.Key, true);
        var (rh, rs) = Check(n.Right, n.Key, true, upper, hasUpper);
        if (Math.Abs(lh - rh) > 1) throw new InvalidOperationException($"Node {n.Key} is unbalanced ({lh} vs {rh}).");
        if (n.Height != 1 + Math.Max(lh, rh)) throw new InvalidOperationException($"Cached height wrong at {n.Key}.");
        if (n.Size != 1 + ls + rs) throw new InvalidOperationException($"Cached size wrong at {n.Key}.");
        return (n.Height, n.Size);
    }
}

/// <summary>Result of an O(log n) range aggregate over an <see cref="AvlTree{TKey,TValue}"/>.</summary>
public readonly record struct RangeAggregate(int Count, double Sum, double Min, double Max, int NodesVisited)
{
    public static RangeAggregate Empty => new(0, 0, double.PositiveInfinity, double.NegativeInfinity, 0);

    public double Average => Count == 0 ? 0 : Sum / Count;

    public static RangeAggregate operator +(RangeAggregate a, RangeAggregate b) =>
        new(a.Count + b.Count, a.Sum + b.Sum, Math.Min(a.Min, b.Min), Math.Max(a.Max, b.Max), a.NodesVisited + b.NodesVisited);
}
