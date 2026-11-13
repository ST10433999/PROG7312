namespace SmartX.Api.Services;

/// <summary>
/// Feedback fix (Part 2 → Part 3) — a counter whose value halves every <c>halfLife</c>.
/// <para>
/// Lecturer feedback on Part 2: "counts never decay and are never pruned, so old behaviour counts as much as
/// recent behaviour". This replaces the plain <c>int</c> counts in the predictive engine. The value is stored together
/// with the time it was last updated, and read as <c>value · 0.5^(elapsed / halfLife)</c>, so no background job is
/// needed to age it. A pattern an operator followed this morning outweighs one from last week, and a pattern
/// that stops happening fades below the pruning threshold and is removed.
/// </para>
/// </summary>
public sealed class DecayingCounter
{
    private double _value;
    private DateTimeOffset _updated;

    /// <summary>Every increment ever applied, undecayed. Shown as "seen N×".</summary>
    public int RawCount { get; private set; }

    public DateTimeOffset LastUpdated => _updated;

    public double ValueAt(DateTimeOffset now, TimeSpan halfLife)
    {
        if (_value == 0) return 0;
        var elapsed = Math.Max(0, (now - _updated).TotalSeconds);
        return _value * Math.Pow(0.5, elapsed / Math.Max(1, halfLife.TotalSeconds));
    }

    public void Add(double amount, DateTimeOffset now, TimeSpan halfLife)
    {
        _value = ValueAt(now, halfLife) + amount;
        _updated = now;
        if (amount > 0) RawCount++;
    }

    /// <summary>Scales the current value (e.g. ×0.5 when an operator dismisses a suggestion).</summary>
    public void Multiply(double factor, DateTimeOffset now, TimeSpan halfLife)
    {
        _value = ValueAt(now, halfLife) * factor;
        _updated = now;
    }
}
