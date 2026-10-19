using SmartX.Shared.Commands;

namespace SmartX.Api.Services;

// -------------------------------------------------------------------------------------------------
// Part 2 — seeds the operational device registry at API start-up so the Command Stream dashboard
// has something to issue commands against from the first poll. Mirrors the Part 1 MockDataSeeder.
// -------------------------------------------------------------------------------------------------

public static class DeviceRegistrySeeder
{
    private static readonly (DeviceKind Kind, string Category, string NamePrefix, string Location)[] Templates =
    {
        (DeviceKind.WaterPump,       "Irrigation",   "Pump",              "Facility A → Zone 1 → Sub-Zone A"),
        (DeviceKind.IrrigationValve, "Irrigation",   "Valve",             "Facility A → Zone 1 → Sub-Zone B"),
        (DeviceKind.SmartRelay,      "Power",        "Relay",             "Facility A → Zone 2 → Sub-Zone A"),
        (DeviceKind.HeatingPad,      "Climate",      "Heating Pad",       "Facility A → Zone 2 → Sub-Zone B"),
        (DeviceKind.VentilationFan,  "Climate",      "Ventilation Fan",   "Facility A → Zone 3 → Sub-Zone A"),
        (DeviceKind.GrowLight,       "Lighting",     "Grow Light Array",  "Facility A → Zone 3 → Sub-Zone B"),
    };

    /// <summary>Seeds <paramref name="count"/> operational devices into the state.</summary>
    public static void Seed(CommandStreamState state, int count, ILogger logger)
    {
        var random = new Random(13);
        for (var i = 0; i < count; i++)
        {
            var tmpl = Templates[i % Templates.Length];
            var mac = GenerateMac(random);
            state.RegisterDevice(new SmartDevice
            {
                Id = Guid.NewGuid(),
                MacAddress = mac,
                Name = $"{tmpl.NamePrefix} {i + 1:00}",
                Kind = tmpl.Kind,
                Location = tmpl.Location,
                Category = tmpl.Category,
                CurrentState = InitialState(tmpl.Kind),
                InAlertState = false
            });
        }
        logger.LogInformation("Seeded {Count} operational devices into the Command Stream registry.", count);
    }

    /// <summary>
    /// Start-up state of a running facility: valves irrigating, fans and grow lights on, the rest idle off.
    /// This way a bulk shut-down visibly changes state and Undo visibly restores it.
    /// </summary>
    private static string InitialState(DeviceKind kind) => kind switch
    {
        DeviceKind.IrrigationValve or DeviceKind.VentilationFan or DeviceKind.GrowLight => "ON",
        _ => "OFF"
    };

    private static string GenerateMac(Random r)
    {
        var bytes = new byte[6];
        r.NextBytes(bytes);
        bytes[0] = 0xB2;    // locally-administered OUI so these don't collide with Part 1 sensor MACs
        return string.Join(':', bytes.Select(b => b.ToString("X2")));
    }
}
