using SmartX.Shared.Collections;
using SmartX.Shared.Models;
using SmartX.Shared.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// Per-node ingestion channel. The non-generic base lets the gateway keep every node in one
/// dictionary, while the generic subclass keeps the ring buffer strongly typed (float / int / bool)
/// so packets are never boxed.
/// </summary>
public abstract class NodeChannel
{
    public SensorProfile Profile { get; }
    public long PacketsIngested { get; protected set; }
    public NodeState State { get; protected set; } = NodeState.Unknown;
    public double LastZScore { get; protected set; }
    public DateTimeOffset? LastSeen { get; protected set; }
    public DateTimeOffset? LastAnomalyAt { get; protected set; }
    public bool WasSilent { get; protected set; }

    /// <summary>How long a Spike/Drift keeps colouring the tile after the offending packet, so it cannot be missed between polls.</summary>
    public static readonly TimeSpan AnomalyHold = TimeSpan.FromSeconds(15);
    public double? LastValue { get; protected set; }

    protected NodeChannel(SensorProfile profile) => Profile = profile;

    public abstract TelemetryIngestResult Ingest(TelemetryIngestRequest request);
    public abstract List<double> Sparkline();
    public abstract int WindowSize { get; }
    public abstract double WindowMean { get; }
    public abstract double WindowStdDev { get; }

    /// <summary>Re-evaluates the heartbeat. A node is Silent after 3 missed publish intervals.</summary>
    public NodeState RefreshHeartbeat(DateTimeOffset now)
    {
        if (LastSeen is null) return State = NodeState.Unknown;
        var silence = (now - LastSeen.Value).TotalSeconds;
        if (silence > Profile.PublishIntervalSeconds * 3)
        {
            State = NodeState.Silent;
            WasSilent = true;
        }
        else if (State == NodeState.Silent)
            State = NodeState.Healthy;      // came back
        return State;
    }

    public static NodeChannel Create(SensorProfile profile, int windowSize) => profile.Payload switch
    {
        PayloadKind.Float => new NodeChannel<float>(profile, windowSize),
        PayloadKind.Int => new NodeChannel<int>(profile, windowSize),
        PayloadKind.Bool => new NodeChannel<bool>(profile, windowSize),
        _ => throw new ArgumentOutOfRangeException(nameof(profile.Payload))
    };
}

public sealed class NodeChannel<T> : NodeChannel where T : struct
{
    public const double DriftThreshold = 2.5;
    public const double SpikeThreshold = 3.5;

    public TelemetryRingBuffer<T> Window { get; }

    public NodeChannel(SensorProfile profile, int windowSize) : base(profile)
        => Window = new TelemetryRingBuffer<T>(windowSize);

    public override int WindowSize => Window.Count;
    public override double WindowMean => Window.Mean;
    public override double WindowStdDev => Window.StdDev;

    public override TelemetryIngestResult Ingest(TelemetryIngestRequest request)
    {
        var value = Convert(request);
        var packet = new TelemetryPacket<T>(Profile.MacAddress, value, request.Timestamp ?? DateTimeOffset.UtcNow, Profile.Unit);
        return Push(packet);
    }

    /// <summary>Strongly typed push used by the seeder / simulator – no DTO involved.</summary>
    public TelemetryIngestResult Push(TelemetryPacket<T> packet)
    {
        // z-score is computed BEFORE the packet enters the window so a spike doesn't dilute its own baseline.
        // Boolean actuators toggle by design – a state change is not a statistical anomaly.
        var z = TelemetryOps<T>.IsNumeric ? Window.ZScore(packet.Value) : 0;
        var state = Classify(z);
        lock (Window) Window.Push(packet);

        PacketsIngested++;
        LastSeen = packet.Timestamp;
        LastValue = TelemetryOps<T>.ToDouble(packet.Value);
        LastZScore = z;

        if (state != NodeState.Healthy) LastAnomalyAt = packet.Timestamp;
        // Hold a recent anomaly on the tile so a single spike survives a couple of healthy packets.
        var held = State is NodeState.Spike or NodeState.Drift && LastAnomalyAt is { } t && packet.Timestamp - t < AnomalyHold;
        State = state == NodeState.Healthy && held ? State : state;

        return new TelemetryIngestResult
        {
            MacAddress = Profile.MacAddress,
            Kind = Profile.Payload,
            PayloadType = packet.PayloadType,
            State = state,
            ZScore = Math.Round(z, 2),
            WindowMean = Math.Round(Window.Mean, 3),
            WindowStdDev = Math.Round(Window.StdDev, 3),
            WindowSize = Window.Count,
            Message = state switch
            {
                NodeState.Spike => $"Spike: {LastValue}{Profile.Unit} is {z:+0.0;-0.0}σ from the {Window.Count}-sample baseline.",
                NodeState.Drift => $"Drift: {LastValue}{Profile.Unit} is {z:+0.0;-0.0}σ from baseline.",
                _ => $"Accepted {packet.PayloadType} packet ({LastValue}{Profile.Unit})."
            }
        };
    }

    private static NodeState Classify(double z) => Math.Abs(z) switch
    {
        >= SpikeThreshold => NodeState.Spike,
        >= DriftThreshold => NodeState.Drift,
        _ => NodeState.Healthy
    };

    public override List<double> Sparkline()
    {
        lock (Window) return Window.ToValueList();
    }

    private static T Convert(TelemetryIngestRequest r)
    {
        if (typeof(T) == typeof(bool))
        {
            var b = r.State ?? (r.Value is { } v && v != 0);
            return (T)(object)b;   // one-off conversion on the DTO boundary only, not on the hot path
        }
        var d = r.Value ?? throw new ArgumentException("Numeric payload requires 'value'.");
        if (typeof(T) == typeof(int)) return (T)(object)(int)Math.Round(d);
        if (typeof(T) == typeof(float)) return (T)(object)(float)d;
        throw new NotSupportedException(typeof(T).Name);
    }
}
