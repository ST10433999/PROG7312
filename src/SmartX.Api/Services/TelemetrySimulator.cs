using SmartX.Shared.Models;
using SmartX.Shared.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// Background service that plays the role of the ESP32 fleet: every tick it publishes a typed packet
/// for each node whose publish interval has elapsed, injects the occasional spike, and lets a few
/// nodes go silent so the Pulse Board and triage feed have something to react to.
/// </summary>
public sealed class TelemetrySimulator(GatewayState state, ILogger<TelemetrySimulator> logger, IConfiguration config) : BackgroundService
{
    private readonly Random _rng = new();
    private readonly Dictionary<string, DateTimeOffset> _nextPublish = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _silenced = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _alertedSilent = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Simulator:Enabled", true))
        {
            logger.LogInformation("Telemetry simulator disabled by configuration.");
            return;
        }

        var tick = TimeSpan.FromMilliseconds(config.GetValue("Simulator:TickMilliseconds", 500));
        var spikeProbability = config.GetValue("Simulator:SpikeProbability", 0.004);
        var silenceProbability = config.GetValue("Simulator:SilenceProbability", 0.0015);

        logger.LogInformation("Telemetry simulator started (tick {Tick} ms).", tick.TotalMilliseconds);
        using var timer = new PeriodicTimer(tick);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var ch in state.Channels.Values)
            {
                var mac = ch.Profile.MacAddress;

                if (_silenced.Contains(mac))
                {
                    // 5 % chance per tick the node recovers
                    if (_rng.NextDouble() < 0.05) { _silenced.Remove(mac); _alertedSilent.Remove(mac); }
                    else
                    {
                        var silence = (now - (ch.LastSeen ?? now)).TotalSeconds;
                        if (silence > ch.Profile.PublishIntervalSeconds * 3 && _alertedSilent.Add(mac))
                            state.RaiseSilentAlert(ch, silence);
                        continue;
                    }
                }

                if (_nextPublish.TryGetValue(mac, out var due) && due > now) continue;
                _nextPublish[mac] = now.AddSeconds(ch.Profile.PublishIntervalSeconds + _rng.NextDouble() * 0.5);

                if (_rng.NextDouble() < silenceProbability) { _silenced.Add(mac); continue; }

                var spike = _rng.NextDouble() < spikeProbability;
                switch (ch)
                {
                    case NodeChannel<float> f:
                        var moisture = (float)(48 + Math.Sin(now.ToUnixTimeSeconds() / 600.0) * 0.8 + _rng.NextDouble() * 3);
                        if (spike) moisture += 25;
                        state.IngestTyped(new TelemetryPacket<float>(mac, moisture, now, "%"));
                        break;
                    case NodeChannel<int> p:
                        var watts = 1200 + _rng.Next(60);
                        if (spike) watts += 900;
                        state.IngestTyped(new TelemetryPacket<int>(mac, watts, now, "W"));
                        break;
                    case NodeChannel<bool> b:
                        var on = _rng.NextDouble() > 0.3;
                        state.IngestTyped(new TelemetryPacket<bool>(mac, on, now));
                        break;
                }
            }
        }
    }
}
