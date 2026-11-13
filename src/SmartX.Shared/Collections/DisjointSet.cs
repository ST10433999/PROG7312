namespace SmartX.Shared.Collections;

/// <summary>
/// Part 3 — a <b>disjoint-set (union–find)</b> forest, used by Kruskal's algorithm to tell in near O(1) whether
/// two mesh nodes are already connected by the spanning tree being built. If they are, adding the radio link
/// would close a cycle.
/// <para>
/// Two optimisations keep the trees flat. <b>Union by rank</b> hangs the shorter tree under the taller one.
/// <b>Path compression</b> points every node visited by <see cref="Find"/> straight at the root. Together they
/// make each operation O(α(n)). α is the inverse Ackermann function, which is below 5 for any n that fits
/// in memory.
/// </para>
/// </summary>
public sealed class DisjointSet
{
    private readonly int[] _parent;
    private readonly byte[] _rank;

    public DisjointSet(int size)
    {
        _parent = new int[size];
        _rank = new byte[size];
        for (var i = 0; i < size; i++) _parent[i] = i;
        Sets = size;
    }

    /// <summary>Number of separate sets remaining.</summary>
    public int Sets { get; private set; }

    public int Find(int x)
    {
        var root = x;
        while (_parent[root] != root) root = _parent[root];
        while (_parent[x] != root)                // path compression
        {
            var next = _parent[x];
            _parent[x] = root;
            x = next;
        }
        return root;
    }

    /// <summary>Merges the sets containing a and b. Returns false if they were already the same set (the edge would form a cycle).</summary>
    public bool Union(int a, int b)
    {
        var ra = Find(a);
        var rb = Find(b);
        if (ra == rb) return false;
        if (_rank[ra] < _rank[rb]) (ra, rb) = (rb, ra);
        _parent[rb] = ra;
        if (_rank[ra] == _rank[rb]) _rank[ra]++;
        Sets--;
        return true;
    }

    public bool Connected(int a, int b) => Find(a) == Find(b);
}
