using SmartX.Shared.Models;
using SmartX.Shared.Telemetry;
using SmartX.Shared.Topology;

namespace SmartX.Api.Services;

/// <summary>
/// Seeds the gateway with a realistic fleet and 24 hours of historical telemetry so the data
/// structures can be exercised under load without physical ESP32 hardware.
/// </summary>
public static class MockDataSeeder
{
    private static readonly string[] Facilities = { "Facility A" };
    private static readonly string[] Zones = { "Zone 1", "Zone 2", "Zone 3", "Zone 4" };
    private static readonly string[] SubZones = { "Sub-Zone A", "Sub-Zone B", "Sub-Zone C" };

    public static void Seed(GatewayState state, int sensorCount, ILogger logger)
    {
        var rng = new Random(2026);

        // Topology constraints per tier – the recursive validator enforces these.
        state.Topology = new DeploymentNode("Facility A", "Facility",
            new TierConfig { PowerBudgetWatts = 60_000, MaxSensors = 5_000, MinPublishIntervalSeconds = 1 },
            new DeploymentNode("Zone 1", "Zone", new TierConfig { AllowedCategories = { SensorCategory.Environmental, SensorCategory.Actuator }, MaxSensors = 800 }),
            new DeploymentNode("Zone 2", "Zone", new TierConfig { PowerBudgetWatts = 15_000 }),
            new DeploymentNode("Zone 3", "Zone", new TierConfig { MinPublishIntervalSeconds = 5 }),
            new DeploymentNode("Zone 4", "Zone"));

        // ---- register sensors -------------------------------------------------
        for (var i = 0; i < sensorCount; i++)
        {
            var category = (i % 10) switch { < 5 => SensorCategory.Environmental, < 8 => SensorCategory.PowerConsumption, _ => SensorCategory.Actuator };
            var zone = Zones[i % Zones.Length];
            // Zone 1 forbids power meters – route those to Zone 2 so the seeded topology is valid.
            if (category == SensorCategory.PowerConsumption && zone == "Zone 1") zone = "Zone 2";

            state.Register(new SensorRegistrationRequest
            {
                MacAddress = $"A1:2F:{(i >> 8) & 0xFF:X2}:{i & 0xFF:X2}:{rng.Next(256):X2}:{rng.Next(256):X2}",
                Name = category switch
                {
                    SensorCategory.Environmental => $"Soil probe {i:000}",
                    SensorCategory.PowerConsumption => $"Smart meter {i:000}",
                    _ => $"Valve relay {i:000}"
                },
                Facility = Facilities[0],
                Zone = zone,
                SubZone = SubZones[i % SubZones.Length],
                NodeId = $"Rack {(i % 12) + 1}",
                Category = category,
                PublishIntervalSeconds = category == SensorCategory.Actuator ? 10 : 5
            });
        }

        // ---- historical batches (jagged arrays) --------------------------------
        var env = state.Sensors.Values.Where(s => s.Payload == PayloadKind.Float).ToArray();
        var pwr = state.Sensors.Values.Where(s => s.Payload == PayloadKind.Int).ToArray();
        var act = state.Sensors.Values.Where(s => s.Payload == PayloadKind.Bool).ToArray();
        var start = DateTimeOffset.UtcNow.AddHours(-24);

        for (var hour = 0; hour < 24; hour++)
        {
            // each batch has a *different* length – exactly why a jagged array is used
            var envBatch = new TelemetryPacket<float>[rng.Next(env.Length / 2, env.Length * 2)];
            for (var k = 0; k < envBatch.Length; k++)
            {
                var s = env[rng.Next(env.Length)];
                envBatch[k] = new TelemetryPacket<float>(s.MacAddress, (float)(45 + 12 * Math.Sin(hour / 24.0 * Math.PI * 2) + rng.NextDouble() * 4), start.AddHours(hour).AddSeconds(rng.Next(3600)), "%");
            }
            state.EnvironmentalHistory.AddBatch(envBatch);

            var pwrBatch = new TelemetryPacket<int>[rng.Next(pwr.Length / 2, pwr.Length * 2)];
            for (var k = 0; k < pwrBatch.Length; k++)
            {
                var s = pwr[rng.Next(pwr.Length)];
                pwrBatch[k] = new TelemetryPacket<int>(s.MacAddress, 800 + (hour is >= 7 and <= 21 ? 600 : 0) + rng.Next(150), start.AddHours(hour).AddSeconds(rng.Next(3600)), "W");
            }
            state.PowerHistory.AddBatch(pwrBatch);

            var actBatch = new TelemetryPacket<bool>[rng.Next(act.Length / 2, act.Length + 1)];
            for (var k = 0; k < actBatch.Length; k++)
            {
                var s = act[rng.Next(act.Length)];
                actBatch[k] = new TelemetryPacket<bool>(s.MacAddress, rng.NextDouble() > 0.5, start.AddHours(hour).AddSeconds(rng.Next(3600)));
            }
            state.ActuatorHistory.AddBatch(actBatch);
        }

        // ---- warm every live window with 30 baseline samples ---------------------
        var now = DateTimeOffset.UtcNow;
        foreach (var ch in state.Channels.Values)
        {
            for (var k = 30; k > 0; k--)
            {
                var ts = now.AddSeconds(-k * ch.Profile.PublishIntervalSeconds);
                switch (ch)
                {
                    case NodeChannel<float> f: f.Push(new TelemetryPacket<float>(f.Profile.MacAddress, (float)(48 + Math.Sin(ts.ToUnixTimeSeconds() / 600.0) * 0.8 + rng.NextDouble() * 3), ts, "%")); break;
                    case NodeChannel<int> p: p.Push(new TelemetryPacket<int>(p.Profile.MacAddress, 1200 + rng.Next(60), ts, "W")); break;
                    case NodeChannel<bool> b: b.Push(new TelemetryPacket<bool>(b.Profile.MacAddress, rng.NextDouble() > 0.3, ts)); break;
                }
            }
        }

        logger.LogInformation("Seeded {Sensors} sensors, {Env}/{Pwr}/{Act} historical packets in {Batches} jagged batches.",
            sensorCount, state.EnvironmentalHistory.TotalPackets, state.PowerHistory.TotalPackets, state.ActuatorHistory.TotalPackets,
            state.EnvironmentalHistory.BatchCount);
    }
}
