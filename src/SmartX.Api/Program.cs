using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using SmartX.Api.Endpoints;
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

// ---- pipeline -------------------------------------------------------------
app.UseCors();
app.MapOpenApi();                                  // /openapi/v1.json

app.MapGet("/", () => Results.Redirect("/api/health"));
app.MapGet("/api/health", (GatewayState s, CommandStreamState cs) => Results.Ok(new
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
    utc = DateTimeOffset.UtcNow
})).WithTags("Gateway");

var api = app.MapGroup("/api");
api.MapSensorEndpoints();
api.MapAttachmentEndpoints();
api.MapTelemetryEndpoints();
api.MapPulseEndpoints();
api.MapCommandStreamEndpoints();                   // Part 2

app.Run();
