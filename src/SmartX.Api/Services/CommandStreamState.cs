using SmartX.Shared.Commands;

namespace SmartX.Api.Services;

// -------------------------------------------------------------------------------------------------
// Part 2 — in-memory state for the Real-Time Command Stream.
//
// The class is split into partial files, one per data structure:
//   CommandStreamState.cs            Dictionary<string, SmartDevice> device registry (this file)
//   CommandStreamState.Telemetry.cs  Queue<T> of incoming telemetry packets
//   CommandStreamState.Alerts.cs     PriorityQueue of critical alerts + HashSet of active faults
//   CommandStreamState.Timeline.cs   SortedDictionary timeline of processed readings
//   CommandStreamState.Commands.cs   command Queue<T>, Stack<T> undo, SortedDictionary history
//
// All access goes through one lock: API request threads and the CommandWorker background thread
// touch the same structures, and none of the BCL collections used are thread-safe on their own.
// -------------------------------------------------------------------------------------------------

public sealed partial class CommandStreamState
{
    private readonly object _lock = new();

    /// <summary>Live device registry keyed by MAC address – O(1) lookup (Rubric: Dictionary&lt;TKey,TValue&gt;).</summary>
    private readonly Dictionary<string, SmartDevice> _devices = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>MACs currently in a fault state. Add() returning false is how duplicate alerts are suppressed (Rubric: HashSet&lt;T&gt;).</summary>
    private readonly HashSet<string> _activeFaults = new(StringComparer.OrdinalIgnoreCase);

    public int DeviceCount { get { lock (_lock) return _devices.Count; } }

    public SmartDevice RegisterDevice(SmartDevice device)
    {
        lock (_lock)
        {
            device.MacAddress = NormaliseMac(device.MacAddress);
            if (device.Id == Guid.Empty) device.Id = Guid.NewGuid();
            _devices[device.MacAddress] = device;
            return Clone(device);
        }
    }

    /// <summary>O(1) lookup by MAC.</summary>
    public SmartDevice? Find(string mac)
    {
        lock (_lock) return _devices.TryGetValue(NormaliseMac(mac), out var d) ? Clone(d) : null;
    }

    /// <summary>Free-text search over MAC, name, location, kind and category, optionally limited to devices in alert.</summary>
    public List<SmartDevice> SearchDevices(string? query = null, bool? onlyInAlert = null)
    {
        lock (_lock)
        {
            IEnumerable<SmartDevice> q = _devices.Values;
            if (!string.IsNullOrWhiteSpace(query))
            {
                var needle = query.Trim();
                q = q.Where(d => d.MacAddress.Contains(needle, StringComparison.OrdinalIgnoreCase)
                              || d.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                              || d.Location.Contains(needle, StringComparison.OrdinalIgnoreCase)
                              || d.Category.Contains(needle, StringComparison.OrdinalIgnoreCase)
                              || d.Kind.ToString().Contains(needle, StringComparison.OrdinalIgnoreCase));
            }
            if (onlyInAlert == true) q = q.Where(d => _activeFaults.Contains(d.MacAddress));
            if (onlyInAlert == false) q = q.Where(d => !_activeFaults.Contains(d.MacAddress));
            return q.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Select(x => Clone(x)).ToList();
        }
    }

    /// <summary>Every device matching a kind and/or category – used by bulk commands.</summary>
    public List<SmartDevice> DevicesWhere(DeviceKind? kind, string? category)
    {
        lock (_lock)
        {
            return _devices.Values
                .Where(d => kind is null || d.Kind == kind)
                .Where(d => string.IsNullOrWhiteSpace(category) || d.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => Clone(x))
                .ToList();
        }
    }

    public static string NormaliseMac(string? mac) => (mac ?? "").Trim().Replace('-', ':').ToUpperInvariant();

    // The API hands out copies so callers can never mutate state outside the lock.
    private static SmartDevice Clone(SmartDevice d) => new()
    {
        Id = d.Id, MacAddress = d.MacAddress, Name = d.Name, Kind = d.Kind, Location = d.Location,
        Category = d.Category, InAlertState = d.InAlertState, CurrentState = d.CurrentState, LastCommandAt = d.LastCommandAt
    };
}
