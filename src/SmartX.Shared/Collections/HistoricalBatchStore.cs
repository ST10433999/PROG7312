using SmartX.Shared.Telemetry;

namespace SmartX.Shared.Collections;

/// <summary>
/// Stores sequential historical batches of raw telemetry as a <b>jagged array</b>
/// (<c>TelemetryPacket&lt;T&gt;[][]</c>) – one inner array per ingestion batch, each with its own
/// length because ESP32 nodes burst at different rates. A rectangular
/// <c>[batches, readings]</c> array would waste memory padding short batches to the longest one.
/// <para>
/// A 2-D <b>multi-dimensional</b> array (<c>double[,]</c>) is used separately for the hourly
/// aggregate grid, where every cell is guaranteed to exist (24 hours × N metrics).
/// </para>
/// <para>
/// Raw batches are transferred into an optimised <see cref="List{T}"/> via <see cref="Flatten"/> when the
/// pipeline needs random access, sorting or LINQ; the jagged array is kept as the immutable "landing zone".
/// </para>
/// </summary>
public sealed class HistoricalBatchStore<T> where T : struct
{
    private TelemetryPacket<T>[][] _batches;
    private int _batchCount;

    public HistoricalBatchStore(int initialBatchCapacity = 32)
    {
        _batches = new TelemetryPacket<T>[initialBatchCapacity][];
    }

    public int BatchCount => _batchCount;

    /// <summary>Total number of packets across all batches.</summary>
    public int TotalPackets
    {
        get
        {
            var n = 0;
            for (var i = 0; i < _batchCount; i++) n += _batches[i].Length;
            return n;
        }
    }

    /// <summary>Lengths of each inner array – demonstrates the ragged shape.</summary>
    public int[] BatchLengths
    {
        get
        {
            var lengths = new int[_batchCount];
            for (var i = 0; i < _batchCount; i++) lengths[i] = _batches[i].Length;
            return lengths;
        }
    }

    /// <summary>Appends one raw batch (the array is stored as-is, no copy).</summary>
    public void AddBatch(TelemetryPacket<T>[] batch)
    {
        if (_batchCount == _batches.Length)
            Array.Resize(ref _batches, _batches.Length * 2);
        _batches[_batchCount++] = batch;
    }

    /// <summary>Direct indexed access: <c>store[batch][reading]</c>.</summary>
    public TelemetryPacket<T>[] this[int batchIndex] =>
        batchIndex < _batchCount ? _batches[batchIndex] : throw new IndexOutOfRangeException();

    /// <summary>
    /// Transfers every raw packet into a single pre-sized <see cref="List{T}"/> ordered by timestamp.
    /// Pre-sizing avoids the repeated doubling reallocation a naive <c>new List()</c> would incur.
    /// </summary>
    public List<TelemetryPacket<T>> Flatten()
    {
        var list = new List<TelemetryPacket<T>>(TotalPackets);
        for (var b = 0; b < _batchCount; b++)
        {
            var batch = _batches[b];
            for (var i = 0; i < batch.Length; i++) list.Add(batch[i]);
        }
        list.Sort(static (x, y) => x.Timestamp.CompareTo(y.Timestamp));
        return list;
    }

    /// <summary>Flattens only the packets for one node into a list.</summary>
    public List<TelemetryPacket<T>> FlattenFor(string macAddress)
    {
        var list = new List<TelemetryPacket<T>>();
        for (var b = 0; b < _batchCount; b++)
            foreach (var p in _batches[b])
                if (string.Equals(p.MacAddress, macAddress, StringComparison.OrdinalIgnoreCase))
                    list.Add(p);
        list.Sort(static (x, y) => x.Timestamp.CompareTo(y.Timestamp));
        return list;
    }

    /// <summary>
    /// Builds a rectangular <c>[24, 3]</c> multi-dimensional grid of hourly min / mean / max values.
    /// </summary>
    public double[,] HourlyGrid()
    {
        var grid = new double[24, 3];
        var counts = new int[24];
        for (var h = 0; h < 24; h++) { grid[h, 0] = double.MaxValue; grid[h, 2] = double.MinValue; }

        for (var b = 0; b < _batchCount; b++)
            foreach (var p in _batches[b])
            {
                var h = p.Timestamp.Hour;
                var v = TelemetryOps<T>.ToDouble(p.Value);
                if (v < grid[h, 0]) grid[h, 0] = v;
                if (v > grid[h, 2]) grid[h, 2] = v;
                grid[h, 1] += v;
                counts[h]++;
            }

        for (var h = 0; h < 24; h++)
        {
            if (counts[h] == 0) { grid[h, 0] = 0; grid[h, 2] = 0; continue; }
            grid[h, 1] /= counts[h];
        }
        return grid;
    }
}
