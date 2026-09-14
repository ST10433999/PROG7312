using System.Numerics;
using System.Reflection;

namespace SmartX.Shared.Telemetry;

/// <summary>
/// Resolves arithmetic and comparison for a payload type <b>once</b>, at type-initialisation time,
/// and caches strongly-typed delegates. Calling <c>TelemetryOps&lt;float&gt;.Add(a, b)</c> is therefore a
/// direct delegate invocation on <c>float</c> values – no boxing, no per-call reflection, no
/// <c>dynamic</c>.
/// <list type="bullet">
///   <item>Numeric types (<c>int</c>, <c>float</c>, <c>double</c>, <c>long</c>…) bind to C# generic math (<see cref="INumber{T}"/>).</item>
///   <item><c>bool</c> binds to logical OR (aggregate) and XOR (delta).</item>
///   <item>Any other struct can still be compared through <see cref="Comparer{T}.Default"/> but not added.</item>
/// </list>
/// </summary>
public static class TelemetryOps<T> where T : struct
{
    public static readonly Func<T, T, T> Add;
    public static readonly Func<T, T, T> Subtract;
    public static readonly Func<T, T, int> Compare;
    public static readonly Func<T, double> ToDouble;
    public static readonly bool IsNumeric;

    static TelemetryOps()
    {
        Compare = Comparer<T>.Default.Compare;

        if (typeof(T) == typeof(bool))
        {
            // Same runtime type, so this cast is a no-op identity conversion – still zero boxing.
            Add = (Func<T, T, T>)(object)new Func<bool, bool, bool>((a, b) => a || b);
            Subtract = (Func<T, T, T>)(object)new Func<bool, bool, bool>((a, b) => a ^ b);
            ToDouble = (Func<T, double>)(object)new Func<bool, double>(b => b ? 1d : 0d);
            IsNumeric = false;
            return;
        }

        IsNumeric = typeof(INumber<>).MakeGenericType(typeof(T)).IsAssignableFrom(typeof(T));
        if (IsNumeric)
        {
            Add = Bind<Func<T, T, T>>(nameof(AddNumeric));
            Subtract = Bind<Func<T, T, T>>(nameof(SubtractNumeric));
            ToDouble = Bind<Func<T, double>>(nameof(ToDoubleNumeric));
            return;
        }

        Add = static (_, _) => throw new NotSupportedException($"{typeof(T).Name} cannot be aggregated.");
        Subtract = static (_, _) => throw new NotSupportedException($"{typeof(T).Name} cannot be subtracted.");
        ToDouble = static _ => double.NaN;
    }

    private static TDelegate Bind<TDelegate>(string method) where TDelegate : Delegate =>
        (TDelegate)typeof(TelemetryOps<T>)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(T))
            .CreateDelegate(typeof(TDelegate));

    private static TNum AddNumeric<TNum>(TNum a, TNum b) where TNum : INumber<TNum> => a + b;
    private static TNum SubtractNumeric<TNum>(TNum a, TNum b) where TNum : INumber<TNum> => a - b;
    private static double ToDoubleNumeric<TNum>(TNum a) where TNum : INumber<TNum> => double.CreateChecked(a);
}
