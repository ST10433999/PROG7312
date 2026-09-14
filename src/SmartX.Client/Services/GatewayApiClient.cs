using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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
}

// ---------------------------------------------------------------------------
// Client-side DTOs for anonymous API payloads
// ---------------------------------------------------------------------------
public sealed class HealthInfo { public string Status { get; set; } = ""; public string Service { get; set; } = ""; public string Runtime { get; set; } = ""; public int Sensors { get; set; } public long PacketsIngested { get; set; } public DateTimeOffset Utc { get; set; } }
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
