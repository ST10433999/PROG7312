using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartX.Shared.Commands;
using SmartX.Shared.History;
using SmartX.Shared.Mesh;
using SmartX.Shared.Models;
using SmartX.Shared.Topology;

namespace SmartX.Client.Services;

/// <summary>
/// Typed, fully asynchronous client for the Smart-X ingestion API. Every call is <c>async</c> and
/// takes a <see cref="CancellationToken"/> so the UI thread never blocks and polling can be cancelled
/// when a page is disposed.
/// </summary>
public sealed class GatewayApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public string BaseAddress => http.BaseAddress?.ToString() ?? "";

    // ---------------------------------------------------------------- health
    public Task<HealthInfo?> GetHealthAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<HealthInfo>("api/health", Json, ct);

    // ---------------------------------------------------------------- sensors
    public Task<PagedResult<SensorProfile>?> GetSensorsAsync(string? search = null, string? category = null, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var q = $"api/sensors?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(search)) q += $"&search={Uri.EscapeDataString(search)}";
        if (!string.IsNullOrWhiteSpace(category)) q += $"&category={category}";
        return http.GetFromJsonAsync<PagedResult<SensorProfile>>(q, Json, ct);
    }

    public Task<SensorProfile?> GetSensorAsync(Guid id, CancellationToken ct = default) =>
        http.GetFromJsonAsync<SensorProfile>($"api/sensors/{id}", Json, ct);

    public async Task<ApiResult<SensorProfile>> RegisterSensorAsync(SensorRegistrationRequest req, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync("api/sensors", req, Json, ct);
        return await ApiResult<SensorProfile>.FromAsync(resp, ct);
    }

    public async Task<bool> DeleteSensorAsync(Guid id, CancellationToken ct = default) =>
        (await http.DeleteAsync($"api/sensors/{id}", ct)).IsSuccessStatusCode;

    // ---------------------------------------------------------------- attachments
    public async Task<UploadResult?> UploadAttachmentsAsync(Guid sensorId, MultipartFormDataContent content, CancellationToken ct = default)
    {
        var resp = await http.PostAsync($"api/sensors/{sensorId}/attachments", content, ct);
        if (!resp.IsSuccessStatusCode) return new UploadResult { Rejected = { new RejectedFile { FileName = "(request)", Reason = await resp.Content.ReadAsStringAsync(ct) } } };
        return await resp.Content.ReadFromJsonAsync<UploadResult>(Json, ct);
    }

    public string AttachmentUrl(Guid sensorId, Guid attachmentId) => $"{BaseAddress}api/sensors/{sensorId}/attachments/{attachmentId}";

    public async Task<bool> DeleteAttachmentAsync(Guid sensorId, Guid attachmentId, CancellationToken ct = default) =>
        (await http.DeleteAsync($"api/sensors/{sensorId}/attachments/{attachmentId}", ct)).IsSuccessStatusCode;

    // ---------------------------------------------------------------- telemetry
    public async Task<ApiResult<TelemetryIngestResult>> IngestAsync(TelemetryIngestRequest req, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync("api/telemetry", req, Json, ct);
        return await ApiResult<TelemetryIngestResult>.FromAsync(resp, ct);
    }

    public Task<RecentWindow?> GetRecentAsync(string mac, CancellationToken ct = default) =>
        http.GetFromJsonAsync<RecentWindow>($"api/telemetry/{Uri.EscapeDataString(mac)}/recent", Json, ct);

    public Task<List<HistoryPoint>?> GetHistoryAsync(string mac, CancellationToken ct = default) =>
        http.GetFromJsonAsync<List<HistoryPoint>>($"api/telemetry/{Uri.EscapeDataString(mac)}/history", Json, ct);

    public Task<BatchOverview?> GetBatchesAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<BatchOverview>("api/telemetry/batches", Json, ct);

    public Task<AggregateDemoResult?> GetAggregateDemoAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<AggregateDemoResult>("api/telemetry/aggregate-demo", Json, ct);

    // ---------------------------------------------------------------- pulse & triage
    public Task<FleetPulse?> GetPulseAsync(int max = 120, CancellationToken ct = default) =>
        http.GetFromJsonAsync<FleetPulse>($"api/pulse?max={max}", Json, ct);

    public Task<List<TriageItem>?> GetTriageAsync(bool includeAcknowledged = false, int take = 40, CancellationToken ct = default) =>
        http.GetFromJsonAsync<List<TriageItem>>($"api/triage?includeAcknowledged={includeAcknowledged}&take={take}", Json, ct);

    public async Task<ApiResult<TriageItem>> ReportIssueAsync(ReportIssueRequest req, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync("api/triage", req, Json, ct);
        return await ApiResult<TriageItem>.FromAsync(resp, ct);
    }

    public async Task<TriageItem?> AcknowledgeAsync(Guid id, string by, CancellationToken ct = default)
    {
        var resp = await http.PostAsync($"api/triage/{id}/acknowledge?by={Uri.EscapeDataString(by)}", null, ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<TriageItem>(Json, ct) : null;
    }

    // ---------------------------------------------------------------- topology
    public Task<DeploymentNode?> GetTopologyAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<DeploymentNode>("api/topology", Json, ct);

    public Task<TopologyValidationResult?> ValidateTopologyAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<TopologyValidationResult>("api/topology/validate", Json, ct);

    public async Task<TopologyValidationResult?> ValidatePlacementAsync(NodePlacementRequest req, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync("api/topology/validate-placement", req, Json, ct);
        return await resp.Content.ReadFromJsonAsync<TopologyValidationResult>(Json, ct);
    }

    // ---------------------------------------------------------------- PART 2 — Command Stream
    public Task<CommandStreamSnapshot?> GetCommandStreamAsync(int take = 50, CancellationToken ct = default) =>
        http.GetFromJsonAsync<CommandStreamSnapshot>($"api/commands/stream?take={take}", Json, ct);

    public async Task<ApiResult<CommandEnvelope>> IssueCommandAsync(IssueCommandRequest req, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync("api/commands", req, Json, ct);
        return await ApiResult<CommandEnvelope>.FromAsync(resp, ct);
    }

    public async Task<ApiResult<CommandEnvelope>> IssueBulkCommandAsync(BulkCommandRequest req, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync("api/commands/bulk", req, Json, ct);
        return await ApiResult<CommandEnvelope>.FromAsync(resp, ct);
    }

    public async Task<CommandBatch?> UndoLastCommandAsync(CancellationToken ct = default)
    {
        var resp = await http.PostAsync("api/commands/undo", null, ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<CommandBatch>(Json, ct) : null;
    }

    public Task<List<CommandRecord>?> GetCommandHistoryAsync(string? mac = null, int take = 100, CancellationToken ct = default)
    {
        var q = $"api/commands/history?take={take}";
        if (!string.IsNullOrWhiteSpace(mac)) q += $"&mac={Uri.EscapeDataString(mac)}";
        return http.GetFromJsonAsync<List<CommandRecord>>(q, Json, ct);
    }

    public Task<List<SmartDevice>?> SearchDevicesAsync(string? query = null, bool? inAlert = null, CancellationToken ct = default)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(query)) parts.Add($"q={Uri.EscapeDataString(query)}");
        if (inAlert is not null) parts.Add($"inAlert={(inAlert.Value ? "true" : "false")}");
        var url = parts.Count == 0 ? "api/devices" : $"api/devices?{string.Join("&", parts)}";
        return http.GetFromJsonAsync<List<SmartDevice>>(url, Json, ct);
    }

    /// <summary>Reports an operator search so the predictive engine can learn from it.</summary>
    public async Task LogSearchAsync(string term, CancellationToken ct = default) =>
        await http.PostAsJsonAsync("api/suggestions/search", new SearchLogRequest { Term = term }, Json, ct);

    public async Task<RaiseAlertResult?> RaiseCriticalAlertAsync(CriticalAlert alert, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync("api/alerts/critical", alert, Json, ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<RaiseAlertResult>(Json, ct) : null;
    }

    public async Task<bool> AcknowledgeAlertAsync(Guid id, CancellationToken ct = default) =>
        (await http.PostAsync($"api/alerts/{id}/acknowledge", null, ct)).IsSuccessStatusCode;

    public async Task<bool> ClearFaultAsync(string mac, CancellationToken ct = default) =>
        (await http.PostAsync($"api/devices/{Uri.EscapeDataString(mac)}/clear-fault", null, ct)).IsSuccessStatusCode;

    /// <summary>Publishes one reading exactly as an ESP32 would.</summary>
    public async Task<ApiResult<StreamIngestResult>> StreamTelemetryAsync(TelemetryIngestRequest req, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync("api/stream/telemetry", req, Json, ct);
        return await ApiResult<StreamIngestResult>.FromAsync(resp, ct);
    }

    public async Task<ApiResult<StreamBatchResult>> StreamTelemetryBatchAsync(List<TelemetryIngestRequest> batch, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync("api/stream/telemetry/batch", batch, Json, ct);
        return await ApiResult<StreamBatchResult>.FromAsync(resp, ct);
    }

    public Task<List<TimelinePoint>?> GetTimelineAsync(string mac, int take = 120, CancellationToken ct = default) =>
        http.GetFromJsonAsync<List<TimelinePoint>>($"api/stream/timeline?mac={Uri.EscapeDataString(mac)}&take={take}", Json, ct);

    public Task<List<TimelineSensor>?> GetTimelineSensorsAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<List<TimelineSensor>>("api/stream/timeline/sensors", Json, ct);

    public Task<List<SuggestedAction>?> GetSuggestionsAsync(int take = 5, CancellationToken ct = default) =>
        http.GetFromJsonAsync<List<SuggestedAction>>($"api/suggestions?take={take}", Json, ct);

    /// <summary>Part 3 feedback fix — tell the predictive engine whether a suggestion was useful.</summary>
    public async Task<ApiResult<SuggestionFeedbackResult>> SendSuggestionFeedbackAsync(string ruleKey, SuggestionVerdict verdict, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync("api/suggestions/feedback", new SuggestionFeedbackRequest { RuleKey = ruleKey, Verdict = verdict }, Json, ct);
        return await ApiResult<SuggestionFeedbackResult>.FromAsync(resp, ct);
    }

    // ---------------------------------------------------------------- PART 3 — Network Topology & Mesh Routing
    public Task<MeshSnapshot?> GetMeshAsync(SpanningTreeAlgorithm algorithm = SpanningTreeAlgorithm.Prim, CancellationToken ct = default) =>
        http.GetFromJsonAsync<MeshSnapshot>($"api/mesh?algorithm={algorithm}", Json, ct);

    public async Task<ApiResult<RouteResult>> GetRouteAsync(string mac, CancellationToken ct = default) =>
        await ApiResult<RouteResult>.FromAsync(await http.GetAsync($"api/mesh/route/{Uri.EscapeDataString(mac)}", ct), ct);

    public async Task<ApiResult<ReachabilityResult>> GetReachabilityAsync(string? subnet = null, string? root = null, CancellationToken ct = default)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(subnet)) parts.Add($"subnet={Uri.EscapeDataString(subnet)}");
        if (!string.IsNullOrWhiteSpace(root)) parts.Add($"root={Uri.EscapeDataString(root)}");
        var url = parts.Count == 0 ? "api/mesh/reachability" : $"api/mesh/reachability?{string.Join("&", parts)}";
        return await ApiResult<ReachabilityResult>.FromAsync(await http.GetAsync(url, ct), ct);
    }

    public Task<SpanningTreeReport?> GetSpanningTreeReportAsync(SpanningTreeAlgorithm algorithm, int steps = 12, CancellationToken ct = default) =>
        http.GetFromJsonAsync<SpanningTreeReport>($"api/mesh/spanning-tree?algorithm={algorithm}&steps={steps}", Json, ct);

    public async Task<ApiResult<NodeStateChange>> FailNodeAsync(string mac, CancellationToken ct = default) =>
        await ApiResult<NodeStateChange>.FromAsync(await http.PostAsync($"api/mesh/nodes/{Uri.EscapeDataString(mac)}/fail", null, ct), ct);

    public async Task<ApiResult<NodeStateChange>> RestoreNodeAsync(string mac, CancellationToken ct = default) =>
        await ApiResult<NodeStateChange>.FromAsync(await http.PostAsync($"api/mesh/nodes/{Uri.EscapeDataString(mac)}/restore", null, ct), ct);

    public async Task<ApiResult<NodeStateChange>> FailBusiestAsync(CancellationToken ct = default) =>
        await ApiResult<NodeStateChange>.FromAsync(await http.PostAsync("api/mesh/fail-busiest", null, ct), ct);

    public async Task<ApiResult<RestoreAllResult>> RestoreAllAsync(CancellationToken ct = default) =>
        await ApiResult<RestoreAllResult>.FromAsync(await http.PostAsync("api/mesh/restore-all", null, ct), ct);

    /// <summary>AVL range query for one sensor's history. Times are sent as ISO 8601 with their offset.</summary>
    public async Task<ApiResult<HistoryQueryResult>> GetHistoryRangeAsync(string mac, DateTimeOffset from, DateTimeOffset to,
        int skip = 0, int take = 25, bool newestFirst = true, int bucketMinutes = 60, bool compare = true, CancellationToken ct = default)
    {
        var url = $"api/history/{Uri.EscapeDataString(mac)}?from={Uri.EscapeDataString(from.ToString("o"))}&to={Uri.EscapeDataString(to.ToString("o"))}" +
                  $"&skip={skip}&take={take}&order={(newestFirst ? "desc" : "asc")}&bucketMinutes={bucketMinutes}&compare={(compare ? "true" : "false")}";
        return await ApiResult<HistoryQueryResult>.FromAsync(await http.GetAsync(url, ct), ct);
    }

    public Task<HistoryIndexStats?> GetHistoryStatsAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<HistoryIndexStats>("api/history/stats", Json, ct);

    public Task<HistoryBenchmarkResult?> RunHistoryBenchmarkAsync(int points, int queries = 200, double windowHours = 2, CancellationToken ct = default) =>
        http.GetFromJsonAsync<HistoryBenchmarkResult>(
            $"api/history/benchmark?points={points}&queries={queries}&windowHours={windowHours.ToString(System.Globalization.CultureInfo.InvariantCulture)}", Json, ct);
}

// ---------------------------------------------------------------------------
// Client-side DTOs for anonymous API payloads
// ---------------------------------------------------------------------------
public sealed class HealthInfo { public string Status { get; set; } = ""; public string Service { get; set; } = ""; public string Runtime { get; set; } = ""; public int Sensors { get; set; } public long PacketsIngested { get; set; } public int Devices { get; set; } public int TelemetryQueueDepth { get; set; } public int PendingCriticalAlerts { get; set; } public int ActiveFaults { get; set; } public int MeshNodes { get; set; } public int MeshOnline { get; set; } public int MeshDown { get; set; } public int MeshIsolated { get; set; } public long HistoryReadings { get; set; } public DateTimeOffset Utc { get; set; } }
public sealed class RestoreAllResult { public HealingEvent? Healing { get; set; } }
public sealed class RaiseAlertResult { public bool Accepted { get; set; } public bool DuplicateSuppressed { get; set; } public Guid? AlertId { get; set; } public int PendingCriticalAlerts { get; set; } }
public sealed class PagedResult<T> { public int Total { get; set; } public int Page { get; set; } public int PageSize { get; set; } public List<T> Items { get; set; } = new(); }
public sealed class UploadResult { public List<AttachmentInfo> Saved { get; set; } = new(); public List<RejectedFile> Rejected { get; set; } = new(); }
public sealed class RejectedFile { public string FileName { get; set; } = ""; public string Reason { get; set; } = ""; }
public sealed class RecentWindow { public string MacAddress { get; set; } = ""; public string Name { get; set; } = ""; public PayloadKind Payload { get; set; } public string Unit { get; set; } = ""; public NodeState State { get; set; } public double ZScore { get; set; } public double Mean { get; set; } public double StdDev { get; set; } public int Window { get; set; } public DateTimeOffset? LastSeen { get; set; } public List<double> Values { get; set; } = new(); }
public sealed class HistoryPoint { public DateTimeOffset Timestamp { get; set; } public double Value { get; set; } }
public sealed class BatchOverview { public BatchStoreStats Environmental { get; set; } = new(); public BatchStoreStats Power { get; set; } = new(); public BatchStoreStats Actuator { get; set; } = new(); }

/// <summary>Wraps a success payload or a ProblemDetails-style error dictionary so pages can show field-level feedback.</summary>
public sealed class ApiResult<T>
{
    public bool Success { get; init; }
    public T? Value { get; init; }
    public string? Error { get; init; }
    public Dictionary<string, string[]> FieldErrors { get; init; } = new();

    public static async Task<ApiResult<T>> FromAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
            return new ApiResult<T> { Success = true, Value = await resp.Content.ReadFromJsonAsync<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } }, ct) };

        var text = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var fields = new Dictionary<string, string[]>();
            if (root.TryGetProperty("errors", out var errors))
                foreach (var e in errors.EnumerateObject())
                    fields[e.Name] = e.Value.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            var title = root.TryGetProperty("detail", out var d) ? d.GetString() : root.TryGetProperty("title", out var t) ? t.GetString() : text;
            return new ApiResult<T> { Success = false, Error = title, FieldErrors = fields };
        }
        catch { return new ApiResult<T> { Success = false, Error = $"{(int)resp.StatusCode} {resp.ReasonPhrase}: {text}" }; }
    }
}
