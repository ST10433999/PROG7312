using System.Collections;
using SmartX.Shared.Telemetry;

namespace SmartX.Shared.Collections;

/// <summary>
/// A custom fixed-capacity circular buffer that keeps the most recent <c>N</c> packets for one node.
/// <para>
/// This is the data structure behind every Pulse Board tile. It is preferred over
/// <see cref="List{T}"/> for the hot path because:
/// <list type="bullet">
///   <item>appending is O(1) and never reallocates once the backing array is full,</item>
///   <item>the rolling mean and variance are maintained incrementally (Welford-style running sums),
///         so a z-score for a new reading costs O(1) rather than O(N),</item>
///   <item>memory per node is bounded – 2 000 nodes × 60 slots is a known, fixed footprint.</item>
/// </list>
/// It implements <see cref="IReadOnlyCollection{T}"/> so it can be enumerated oldest → newest and
/// converted to a <see cref="List{T}"/> when a snapshot is required.
/// </para>
/// </summary>
public sealed class TelemetryRingBuffer<T> : IReadOnlyCollection<TelemetryPacket<T>> where T : struct
{
    private readonly TelemetryPacket<T>[] _slots;
    private int _head;      // index of the next write
    private int _count;
    private double _sum;
    private double _sumSquares;

    public TelemetryRingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _slots = new TelemetryPacket<T>[capacity];
    }

    public int Capacity => _slots.Length;
    public int Count => _count;
    public bool IsFull => _count == _slots.Length;

    /// <summary>The most recently written packet, or <c>null</c> when the buffer is empty.</summary>
    public TelemetryPacket<T>? Latest => _count == 0 ? null : _slots[(_head - 1 + _slots.Length) % _slots.Length];

    public double Mean => _count == 0 ? 0 : _sum / _count;

    public double StdDev
    {
        get
        {
            if (_count < 2) return 0;
            var mean = Mean;
            var variance = (_sumSquares / _count) - (mean * mean);
            return variance <= 0 ? 0 : Math.Sqrt(variance);
        }
    }

    /// <summary>Appends a packet, overwriting the oldest one when full. Running statistics are updated in O(1).</summary>
    public void Push(TelemetryPacket<T> packet)
    {
        var incoming = TelemetryOps<T>.ToDouble(packet.Value);

        if (IsFull)
        {
            var evicted = TelemetryOps<T>.ToDouble(_slots[_head].Value);
            _sum -= evicted;
            _sumSquares -= evicted * evicted;
        }
        else
        {
            _count++;
        }

        _slots[_head] = packet;
        _head = (_head + 1) % _slots.Length;
        _sum += incoming;
        _sumSquares += incoming * incoming;
    }

    /// <summary>Standard score of a candidate value against the current window (0 when the window is flat).</summary>
    public double ZScore(T candidate)
    {
        var sd = StdDev;
        if (sd == 0 || _count < 10) return 0;
        return (TelemetryOps<T>.ToDouble(candidate) - Mean) / sd;
    }

    /// <summary>Copies the window (oldest → newest) into a plain <see cref="List{T}"/> of raw values.</summary>
    public List<double> ToValueList()
    {
        var list = new List<double>(_count);
        foreach (var p in this) list.Add(TelemetryOps<T>.ToDouble(p.Value));
        return list;
    }

    public IEnumerator<TelemetryPacket<T>> GetEnumerator()
    {
        var start = IsFull ? _head : 0;
        for (var i = 0; i < _count; i++)
            yield return _slots[(start + i) % _slots.Length];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
