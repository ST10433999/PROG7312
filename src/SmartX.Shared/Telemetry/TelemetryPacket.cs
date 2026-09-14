namespace SmartX.Shared.Telemetry;

/// <summary>
/// Generic, strongly-typed wrapper for a single telemetry reading published by an ESP32 node.
/// <para>
/// <typeparamref name="T"/> is constrained to <c>struct</c>, so a <see cref="TelemetryPacket{T}"/> for
/// <c>float</c> (soil moisture), <c>int</c> (wattage) or <c>bool</c> (valve state) stores the value
/// inline. The JIT generates a specialised copy of this type per value type, which means no
/// boxing/unboxing ever happens on the hot ingestion path.
/// </para>
/// <para>
/// Operators are overloaded so that sensor values can be aggregated or compared directly in code:
/// <c>var total = meter1 + meter2;</c> or <c>if (reading &gt; baseline) …</c>.
/// The arithmetic itself is resolved once per <typeparamref name="T"/> by <see cref="TelemetryOps{T}"/>.
/// </para>
/// </summary>
public readonly record struct TelemetryPacket<T> : IComparable<TelemetryPacket<T>>
    where T : struct
{
    public string MacAddress { get; init; }
    public T Value { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string Unit { get; init; }

    public TelemetryPacket(string macAddress, T value, DateTimeOffset timestamp, string unit = "")
    {
        MacAddress = macAddress;
        Value = value;
        Timestamp = timestamp;
        Unit = unit;
    }

    public TelemetryPacket(string macAddress, T value, string unit = "")
        : this(macAddress, value, DateTimeOffset.UtcNow, unit) { }

    /// <summary>The CLR type name of the payload, e.g. "Single", "Int32", "Boolean".</summary>
    public string PayloadType => typeof(T).Name;

    // ---------------------------------------------------------------------
    // Operator overloading
    // ---------------------------------------------------------------------

    /// <summary>Aggregates two readings: <c>Meter3 = Meter1 + Meter2</c>. For <c>bool</c> this is a logical OR.</summary>
    public static TelemetryPacket<T> operator +(TelemetryPacket<T> a, TelemetryPacket<T> b) =>
        new(Combine(a.MacAddress, b.MacAddress), TelemetryOps<T>.Add(a.Value, b.Value), Later(a, b), a.Unit);

    /// <summary>Delta between two readings: <c>delta = now - previous</c>. For <c>bool</c> this is XOR (state changed).</summary>
    public static TelemetryPacket<T> operator -(TelemetryPacket<T> a, TelemetryPacket<T> b) =>
        new(Combine(a.MacAddress, b.MacAddress), TelemetryOps<T>.Subtract(a.Value, b.Value), Later(a, b), a.Unit);

    public static bool operator >(TelemetryPacket<T> a, TelemetryPacket<T> b) => TelemetryOps<T>.Compare(a.Value, b.Value) > 0;
    public static bool operator <(TelemetryPacket<T> a, TelemetryPacket<T> b) => TelemetryOps<T>.Compare(a.Value, b.Value) < 0;
    public static bool operator >=(TelemetryPacket<T> a, TelemetryPacket<T> b) => TelemetryOps<T>.Compare(a.Value, b.Value) >= 0;
    public static bool operator <=(TelemetryPacket<T> a, TelemetryPacket<T> b) => TelemetryOps<T>.Compare(a.Value, b.Value) <= 0;

    /// <summary>Compare a packet directly against a raw threshold value: <c>if (packet &gt; 2500) …</c>.</summary>
    public static bool operator >(TelemetryPacket<T> a, T threshold) => TelemetryOps<T>.Compare(a.Value, threshold) > 0;
    public static bool operator <(TelemetryPacket<T> a, T threshold) => TelemetryOps<T>.Compare(a.Value, threshold) < 0;

    /// <summary>Unwrap implicitly so a packet can be used wherever a plain <typeparamref name="T"/> is expected.</summary>
    public static implicit operator T(TelemetryPacket<T> packet) => packet.Value;

    public int CompareTo(TelemetryPacket<T> other) => TelemetryOps<T>.Compare(Value, other.Value);

    public override string ToString() => $"{MacAddress} {Value}{Unit} @ {Timestamp:HH:mm:ss}";

    private static string Combine(string a, string b) => a == b ? a : $"{a}+{b}";
    private static DateTimeOffset Later(TelemetryPacket<T> a, TelemetryPacket<T> b) =>
        a.Timestamp > b.Timestamp ? a.Timestamp : b.Timestamp;
}
