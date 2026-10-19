using SmartX.Api.Services;

namespace SmartX.Api.Endpoints;

// Part 2 — /api/devices – the device Dictionary and the active-fault HashSet.
public static class DeviceEndpoints
{
    public static RouteGroupBuilder MapDeviceEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/devices").WithTags("Operational Devices");

        g.MapGet("/", (CommandStreamState stream, string? q, bool? inAlert) => Results.Ok(stream.SearchDevices(q, inAlert)))
            .WithSummary("Search the device registry (MAC / name / location / kind / category).");

        g.MapGet("/faults", (CommandStreamState stream) => Results.Ok(stream.Faults()))
            .WithSummary("Every MAC in the active-fault HashSet.");

        g.MapGet("/{mac}", (string mac, CommandStreamState stream) =>
            stream.Find(mac) is { } device ? Results.Ok(device) : Results.NotFound())
            .WithSummary("O(1) Dictionary lookup by MAC.");

        g.MapPost("/{mac}/clear-fault", (string mac, CommandStreamState stream) =>
            Results.Ok(new { cleared = stream.ClearFault(mac), activeFaults = stream.ActiveFaultCount }))
            .WithSummary("Remove a MAC from the active-fault HashSet once the issue is resolved.");

        return api;
    }
}
