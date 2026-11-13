using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using SmartX.Api.Endpoints;
using SmartX.Api.History;
using SmartX.Api.Mesh;
using SmartX.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ---- services -------------------------------------------------------------
builder.Services.AddOpenApi();
builder.Services.AddSingleton<GatewayState>();
builder.Services.AddSingleton<AttachmentService>();
builder.Services.AddHostedService<TelemetrySimulator>();

// Part 2 — Real-Time Command Stream & History
builder.Services.AddSingleton<CommandStreamState>();
builder.Services.AddSingleton<PredictiveEngine>();
builder.Services.AddHostedService<CommandWorker>();

// Part 3 — Network Topology & Mesh Routing, historical telemetry index
builder.Services.AddSingleton(sp => sp.GetRequiredService<GatewayState>().HistoryIndex);
builder.Services.AddSingleton<MeshNetworkService>();
builder.Services.AddHostedService<MeshMonitor>();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = AttachmentService.MaxRequestBytes;   // up to 10 × 25 MB per request
    o.ValueLengthLimit = int.MaxValue;
});
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = AttachmentService.MaxRequestBytes);

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                     ?? new[] { "http://localhost:5100", "https://localhost:7100", "http://localhost:8080" };
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders("Content-Disposition")));

var app = builder.Build();

// ---- seed -----------------------------------------------------------------
var state = app.Services.GetRequiredService<GatewayState>();
MockDataSeeder.Seed(state, app.Configuration.GetValue("Seed:SensorCount", 240), app.Logger);

var commandStream = app.Services.GetRequiredService<CommandStreamState>();
DeviceRegistrySeeder.Seed(commandStream, app.Configuration.GetValue("Seed:DeviceCount", 24), app.Logger);

// Feedback fix: learned behaviour decays with a configurable half-life.
app.Services.GetRequiredService<PredictiveEngine>().HalfLife =
    TimeSpan.FromHours(Math.Max(0.05, app.Configuration.GetValue("Predictive:HalfLifeHours", 6.0)));

state.HistoryIndex.MaxPointsPerSensor = app.Configuration.GetValue("History:MaxPointsPerSensor", 10_000);
HistoryArchiveSeeder.Seed(state, state.HistoryIndex,
    app.Configuration.GetValue("History:ArchiveDays", 7),
    app.Configuration.GetValue("History:ArchiveIntervalMinutes", 5), app.Logger);

// ---- pipeline -------------------------------------------------------------
app.UseCors();
app.MapOpenApi();                                  // /openapi/v1.json

app.MapGet("/", () => Results.Redirect("/api/health"));
app.MapGet("/api/health", (GatewayState s, CommandStreamState cs, MeshNetworkService mesh) =>
{
    var meshSummary = mesh.Summary();
    return Results.Ok(new
    {
        status = "ok",
        service = "Smart-X Ingestion Gateway",
        runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        sensors = s.Sensors.Count,
        packetsIngested = s.TotalPackets,
        devices = cs.DeviceCount,
        telemetryQueueDepth = cs.TelemetryQueueDepth,
        pendingCriticalAlerts = cs.PendingCriticalCount,
        activeFaults = cs.ActiveFaultCount,
        meshNodes = meshSummary.Vertices,
        meshOnline = meshSummary.Online,
        meshDown = meshSummary.Down,
        meshIsolated = meshSummary.Isolated,
        historyReadings = s.HistoryIndex.Stats().TotalReadings,
        utc = DateTimeOffset.UtcNow
    });
}).WithTags("Gateway");

var api = app.MapGroup("/api");
api.MapSensorEndpoints();
api.MapAttachmentEndpoints();
api.MapTelemetryEndpoints();
api.MapPulseEndpoints();
api.MapCommandStreamEndpoints();                   // Part 2
api.MapMeshEndpoints();                            // Part 3
api.MapHistoryEndpoints();                         // Part 3

app.Run();
