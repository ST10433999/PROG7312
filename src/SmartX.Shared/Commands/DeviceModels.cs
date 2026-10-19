using System.Text.Json.Serialization;

namespace SmartX.Shared.Commands;

// -------------------------------------------------------------------------------------------------
// Part 2 — Real-Time Command Stream & History. Operational devices (actuators).
// Shared by the API and the Blazor client, so the same DataAnnotations validate on both sides.
// -------------------------------------------------------------------------------------------------

/// <summary>Kinds of actuator a Smart-X operational device can be.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeviceKind { WaterPump, IrrigationValve, SmartRelay, HeatingPad, VentilationFan, GrowLight }

/// <summary>A controllable actuator managed by the command stream (distinct from the Part 1 sensors).</summary>
public sealed class SmartDevice
{
    public Guid Id { get; set; }
    /// <summary>MAC address – the key of the <c>Dictionary&lt;string, SmartDevice&gt;</c> (O(1) lookup).</summary>
    public string MacAddress { get; set; } = "";
    public string Name { get; set; } = "";
    public DeviceKind Kind { get; set; }
    public string Location { get; set; } = "";
    /// <summary>Operational category used by the dashboard filter (Irrigation, Power, Climate, Lighting).</summary>
    public string Category { get; set; } = "";
    /// <summary>True while the device's MAC is in the active-fault HashSet.</summary>
    public bool InAlertState { get; set; }
    public string CurrentState { get; set; } = "OFF";
    public DateTimeOffset? LastCommandAt { get; set; }
}
