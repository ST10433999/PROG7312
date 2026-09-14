using System.Collections.Concurrent;
using System.Diagnostics;
using SmartX.Shared.Collections;
using SmartX.Shared.Models;
using SmartX.Shared.Telemetry;
using SmartX.Shared.Topology;

namespace SmartX.Api.Services;

/// <summary>
/// In-memory state of the gateway: registered sensors, their live ingestion channels, historical
/// batch stores (jagged arrays), the deployment topology tree and the triage feed.
/// Everything is held in generic collections – <see cref="ConcurrentDictionary{TKey,TValue}"/> for
/// lookup by MAC and <see cref="List{T}"/> for ordered feeds.
/// </summary>
public sealed class GatewayState
{
    public const int WindowSize = 60;

    public ConcurrentDictionary<string, SensorProfile> Sensors { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, NodeChannel> Channels { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HistoricalBatchStore<float> EnvironmentalHistory { get; } = new();
    public HistoricalBatchStore<int> PowerHistory { get; } = new();
    public HistoricalBatchStore<bool> ActuatorHistory { get; } = new();

    public DeploymentNode Topology { get; set; } = new("Facility A", "Facility");

    private readonly List<TriageItem> _triage = new();
    private readonly object _triageLock = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAlertPerNode = new(StringComparer.OrdinalIgnoreCase);

    // Which nodes currently have an unacknowledged system alert of each kind. Kept as sets so the
    // ingestion hot path can decide in O(1) whether anything needs resolving, instead of scanning
    // the whole feed on every packet.
    private readonly ConcurrentDictionary<string, byte> _openDisconnectAlerts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _openAnomalyAlerts = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] DisconnectTitles = { "Sensor disconnect" };
    private static readonly string[] AnomalyTitles = { "Telemetry spike", "Baseline drift" };

    private long _totalPackets;
    private readonly ConcurrentQueue<long> _packetTicks = new();   // for packets/sec

    public long TotalPackets => Interlocked.Read(ref _totalPackets);

    // ------------------------------------------------------------------ sensors
    public SensorProfile Register(SensorRegistrationRequest req)
    {
        var mac = NormaliseMac(req.MacAddress);
        var path = new List<string> { req.Facility.Trim(), req.Zone.Trim() };
        if (!string.IsNullOrWhiteSpace(req.SubZone)) path.Add(req.SubZone.Trim());
        path.Add(req.NodeId.Trim());

        var (payload, unit) = req.Category switch
        {
            SensorCategory.Environmental => (PayloadKind.Float, "%"),
            SensorCategory.PowerConsumption => (PayloadKind.Int, "W"),
            _ => (PayloadKind.Bool, "")
        };

        var profile = new SensorProfile
        {
            Id = Guid.NewGuid(),
            MacAddress = mac,
            Name = req.Name.Trim(),
            DeploymentPath = path,
            Category = req.Category,
            Payload = payload,
            Unit = unit,
            PublishIntervalSeconds = req.PublishIntervalSeconds,
            RegisteredAt = DateTimeOffset.UtcNow
        };

        Sensors[mac] = profile;
        Channels[mac] = NodeChannel.Create(profile, WindowSize);
        lock (Topology) TopologyValidator.EnsurePath(Topology, path, 0, mac);
        return profile;
    }

    public static string NormaliseMac(string mac) => mac.Trim().Replace('-', ':').ToUpperInvariant();

    // ------------------------------------------------------------------ ingestion
    public TelemetryIngestResult Ingest(TelemetryIngestRequest request)
    {
        var mac = NormaliseMac(request.MacAddress);
        if (!Channels.TryGetValue(mac, out var channel))
            throw new KeyNotFoundException($"Sensor {mac} is not registered.");

        // No type coercion on the ingestion path: the packet kind must match the sensor's registered payload.
        if (request.Kind != channel.Profile.Payload)
            throw new ArgumentException($"Sensor {mac} publishes {channel.Profile.Payload} packets; received {request.Kind}.");
        if (request.Kind == PayloadKind.Int && request.Value is { } v && v != Math.Floor(v))
            throw new ArgumentException($"Sensor {mac} publishes integer packets; {v} has a fractional part.");
        if (request.Kind == PayloadKind.Bool && request.State is null)
            throw new ArgumentException($"Sensor {mac} publishes boolean packets; 'state' is required.");

        var result = channel.Ingest(request);
        AfterIngest(channel, result);
        RecordHistory(channel);
        return result;
    }

    public void IngestTyped<T>(TelemetryPacket<T> packet) where T : struct
    {
        if (Channels.TryGetValue(packet.MacAddress, out var ch) && ch is NodeChannel<T> typed)
        {
            AfterIngest(typed, typed.Push(packet));
            RecordHistory(typed);
        }
    }

    // ------------------------------------------------------------------ live → historical batches
    // Live packets accumulate in a raw pending array per payload type and are flushed into the
    // jagged HistoricalBatchStore<T> as one batch every PendingBatchSize packets (or on demand),
    // so the history a user reads back includes readings ingested after start-up.
    public const int PendingBatchSize = 250;
    private readonly List<TelemetryPacket<float>> _pendingFloat = new();
    private readonly List<TelemetryPacket<int>> _pendingInt = new();
    private readonly List<TelemetryPacket<bool>> _pendingBool = new();
    private readonly object _pendingLock = new();

    private void RecordHistory(NodeChannel channel)
    {
        lock (_pendingLock)
        {
            switch (channel)
            {
                case NodeChannel<float> f when f.Window.Latest is { } p: _pendingFloat.Add(p); break;
                case NodeChannel<int> i when i.Window.Latest is { } p: _pendingInt.Add(p); break;
                case NodeChannel<bool> b when b.Window.Latest is { } p: _pendingBool.Add(p); break;
            }
            if (_pendingFloat.Count + _pendingInt.Count + _pendingBool.Count >= PendingBatchSize)
                FlushPendingUnsafe();
        }
    }

    /// <summary>Moves every pending live packet into the jagged batch stores.</summary>
    public void FlushPending() { lock (_pendingLock) FlushPendingUnsafe(); }

    private void FlushPendingUnsafe()
    {
        if (_pendingFloat.Count > 0) { EnvironmentalHistory.AddBatch(_pendingFloat.ToArray()); _pendingFloat.Clear(); }
        if (_pendingInt.Count > 0) { PowerHistory.AddBatch(_pendingInt.ToArray()); _pendingInt.Clear(); }
        if (_pendingBool.Count > 0) { ActuatorHistory.AddBatch(_pendingBool.ToArray()); _pendingBool.Clear(); }
    }

    private void AfterIngest(NodeChannel channel, TelemetryIngestResult result)
    {
        Interlocked.Increment(ref _totalPackets);
        var now = Stopwatch.GetTimestamp();
        _packetTicks.Enqueue(now);
        while (_packetTicks.TryPeek(out var oldest) && Stopwatch.GetElapsedTime(oldest).TotalSeconds > 10)
            _packetTicks.TryDequeue(out _);

        var mac = channel.Profile.MacAddress;

        // A packet arriving at all means the node is no longer silent.
        if (_openDisconnectAlerts.ContainsKey(mac))
            ResolveSystemAlerts(channel, DisconnectTitles, _openDisconnectAlerts, "auto: node recovered");

        if (result.State is NodeState.Spike or NodeState.Drift)
        {
            RaiseSystemAlert(channel, result);
        }
        else if (channel.State == NodeState.Healthy && _openAnomalyAlerts.ContainsKey(mac))
        {
            // The reading is back inside the baseline and the anomaly hold has expired, so the
            // condition has cleared: close the alert rather than leaving it to pile up. Keeping the
            // feed to what is wrong *now* is the whole point of ranking it by severity.
            ResolveSystemAlerts(channel, AnomalyTitles, _openAnomalyAlerts, "auto: reading returned to baseline");
        }
    }

    public double PacketsPerSecond => _packetTicks.Count / 10.0;

    // ------------------------------------------------------------------ triage
    private void RaiseSystemAlert(NodeChannel channel, TelemetryIngestResult result)
    {
        // Cooldown: one alert per node per 30 s so a noisy sensor cannot flood the feed (Wang et al., 2016).
        var now = DateTimeOffset.UtcNow;
        if (_lastAlertPerNode.TryGetValue(channel.Profile.MacAddress, out var last) && (now - last).TotalSeconds < 30)
            return;
        _lastAlertPerNode[channel.Profile.MacAddress] = now;
        _openAnomalyAlerts[channel.Profile.MacAddress] = 1;

        AddTriage(new TriageItem
        {
            Id = Guid.NewGuid(),
            Severity = result.State == NodeState.Spike ? Severity.High : Severity.Warn,
            Source = TriageSource.System,
            MacAddress = channel.Profile.MacAddress,
            Location = channel.Profile.DeploymentPathDisplay,
            Title = result.State == NodeState.Spike ? "Telemetry spike" : "Baseline drift",
            Detail = result.Message,
            CreatedAt = now
        });
    }

    public void RaiseSilentAlert(NodeChannel channel, double seconds)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastAlertPerNode.TryGetValue(channel.Profile.MacAddress, out var last) && (now - last).TotalSeconds < 60)
            return;
        _lastAlertPerNode[channel.Profile.MacAddress] = now;
        _openDisconnectAlerts[channel.Profile.MacAddress] = 1;
        AddTriage(new TriageItem
        {
            Id = Guid.NewGuid(),
            Severity = Severity.Critical,
            Source = TriageSource.System,
            MacAddress = channel.Profile.MacAddress,
            Location = channel.Profile.DeploymentPathDisplay,
            Title = "Sensor disconnect",
            Detail = $"No packet for {seconds:0} s (expected every {channel.Profile.PublishIntervalSeconds} s). Check PoE / mesh hop.",
            CreatedAt = now
        });
    }

    /// <summary>
    /// Closes the open system alerts of the given kind for one node – the condition that raised them
    /// has cleared. Without this the feed only ever grows, which is the alarm-overloading failure mode
    /// the triage ranking exists to avoid.
    /// </summary>
    private void ResolveSystemAlerts(NodeChannel channel, string[] titles,
        ConcurrentDictionary<string, byte> openSet, string reason)
    {
        var mac = channel.Profile.MacAddress;
        openSet.TryRemove(mac, out _);
        lock (_triageLock)
        {
            foreach (var t in _triage.Where(t => t.IsOpen && t.Source == TriageSource.System
                                                 && titles.Contains(t.Title)
                                                 && t.MacAddress.Equals(mac, StringComparison.OrdinalIgnoreCase)))
            {
                t.AcknowledgedAt = DateTimeOffset.UtcNow;
                t.AcknowledgedBy = reason;
            }
        }
    }

    public TriageItem AddTriage(TriageItem item)
    {
        lock (_triageLock)
        {
            _triage.Add(item);
            if (_triage.Count > 500) _triage.RemoveRange(0, _triage.Count - 500);
        }
        return item;
    }

    /// <summary>Open items first, ordered by severity then recency – the "triage feed".</summary>
    public List<TriageItem> TriageFeed(bool includeAcknowledged = false, int take = 50)
    {
        lock (_triageLock)
        {
            return _triage
                .Where(t => includeAcknowledged || t.IsOpen)
                .OrderBy(t => t.IsOpen ? 0 : 1)
                .ThenByDescending(t => t.Severity)
                .ThenByDescending(t => t.CreatedAt)
                .Take(take)
                .ToList();
        }
    }

    public TriageItem? Acknowledge(Guid id, string by)
    {
        lock (_triageLock)
        {
            var item = _triage.FirstOrDefault(t => t.Id == id);
            if (item is null) return null;
            item.AcknowledgedAt = DateTimeOffset.UtcNow;
            item.AcknowledgedBy = by;
            return item;
        }
    }

    public int OpenTriageCount { get { lock (_triageLock) return _triage.Count(t => t.IsOpen); } }

    // ------------------------------------------------------------------ pulse board
    public FleetPulse BuildFleetPulse(int maxNodes = 400)
    {
        var now = DateTimeOffset.UtcNow;
        var pulse = new FleetPulse { GeneratedAt = now, TotalSensors = Channels.Count, PacketsPerSecond = Math.Round(PacketsPerSecond, 1),
            TotalPacketsIngested = TotalPackets, OpenTriageItems = OpenTriageCount };

        foreach (var ch in Channels.Values)
        {
            var wasSilent = ch.State == NodeState.Silent;
            var state = ch.RefreshHeartbeat(now);
            if (wasSilent && state != NodeState.Silent)
                ResolveSystemAlerts(ch, DisconnectTitles, _openDisconnectAlerts, "auto: node recovered");
            switch (state)
            {
                case NodeState.Healthy: pulse.Healthy++; break;
                case NodeState.Drift: pulse.Drift++; break;
                case NodeState.Spike: pulse.Spike++; break;
                case NodeState.Silent: pulse.Silent++; break;
            }
        }

        pulse.Nodes = Channels.Values
            .OrderByDescending(c => c.State == NodeState.Silent ? 3 : c.State == NodeState.Spike ? 2 : c.State == NodeState.Drift ? 1 : 0)
            .ThenBy(c => c.Profile.Name)
            .Take(maxNodes)
            .Select(c => new NodePulse
            {
                SensorId = c.Profile.Id,
                MacAddress = c.Profile.MacAddress,
                Name = c.Profile.Name,
                Location = c.Profile.DeploymentPathDisplay,
                Category = c.Profile.Category,
                Payload = c.Profile.Payload,
                Unit = c.Profile.Unit,
                State = c.State,
                ZScore = Math.Round(c.LastZScore, 2),
                LastValue = c.LastValue,
                LastSeen = c.LastSeen,
                SecondsSinceLastPacket = c.LastSeen is null ? 0 : Math.Round((now - c.LastSeen.Value).TotalSeconds, 1),
                ExpectedIntervalSeconds = c.Profile.PublishIntervalSeconds,
                Sparkline = c.Sparkline()
            })
            .ToList();

        return pulse;
    }
}
