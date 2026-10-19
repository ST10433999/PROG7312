using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Services;
using SmartX.Shared.Commands;

namespace SmartX.Api.Endpoints;

// Part 2 — /api/commands – manual and bulk overrides, undo, history and the dashboard snapshot.
public static class CommandEndpoints
{
    public static RouteGroupBuilder MapCommandEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/commands").WithTags("Command Stream");

        g.MapGet("/stream", (CommandStreamState stream, PredictiveEngine engine, int take = 50) =>
            Results.Ok(new CommandStreamSnapshot
            {
                TelemetryQueueDepth = stream.TelemetryQueueDepth,
                CommandQueueDepth = stream.CommandQueueDepth,
                PendingCriticalAlerts = stream.PendingCriticalCount,
                UndoStackDepth = stream.UndoDepth,
                NextUndoLabel = stream.NextUndoLabel,
                ActiveFaultCount = stream.ActiveFaultCount,
                DuplicatesSuppressed = stream.DuplicatesSuppressed,
                DeviceCount = stream.DeviceCount,
                HistoryCount = stream.HistoryCount,
                TelemetryProcessed = stream.TelemetryProcessed,
                CriticalProcessed = stream.CriticalProcessed,
                TelemetryRejected = stream.TelemetryRejected,
                Devices = stream.SearchDevices(),
                RecentHistory = stream.History(null, Math.Clamp(take, 1, 200)),
                PendingAlerts = stream.PendingAlerts(10),
                RecentAlerts = stream.RecentAlerts(10),
                Faults = stream.Faults(),
                Suggestions = engine.Suggest(stream, 5),
                GeneratedAt = DateTimeOffset.UtcNow
            }))
            .WithSummary("Everything the Command Stream dashboard renders, in one call.");

        g.MapPost("/", Results<Accepted<CommandEnvelope>, ValidationProblem, NotFound<ProblemDetails>>
            (IssueCommandRequest req, CommandStreamState stream) =>
        {
            var errors = SensorEndpoints.ValidateModel(req);
            if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

            var device = stream.Find(req.MacAddress);
            if (device is null)
                return TypedResults.NotFound(new ProblemDetails { Title = $"Device {CommandStreamState.NormaliseMac(req.MacAddress)} is not registered.", Status = 404 });

            var envelope = stream.EnqueueCommand(new CommandEnvelope
            {
                TargetMacs = new List<string> { device.MacAddress },
                Label = $"{req.Command} {device.Name}",
                Command = req.Command,
                Priority = req.Priority,
                Reason = string.IsNullOrWhiteSpace(req.Reason) ? "Manual override" : req.Reason.Trim()
            });
            return TypedResults.Accepted($"/api/commands/history?mac={Uri.EscapeDataString(device.MacAddress)}", envelope);
        }).WithSummary("Queue a manual override for one device.");

        g.MapPost("/bulk", Results<Accepted<CommandEnvelope>, ValidationProblem, NotFound<ProblemDetails>>
            (BulkCommandRequest req, CommandStreamState stream) =>
        {
            var errors = SensorEndpoints.ValidateModel(req);
            if (req.Kind is null && string.IsNullOrWhiteSpace(req.Category))
                errors["target"] = new[] { "Specify a device kind and/or a category." };
            if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

            var targets = stream.DevicesWhere(req.Kind, req.Category);
            if (targets.Count == 0)
                return TypedResults.NotFound(new ProblemDetails { Title = "No devices match that kind/category.", Status = 404 });

            var what = req.Kind?.ToString() ?? req.Category ?? "devices";
            var envelope = stream.EnqueueCommand(new CommandEnvelope
            {
                TargetMacs = targets.Select(d => d.MacAddress).ToList(),
                Label = $"{req.Command} all {what} ({targets.Count})",
                Command = req.Command,
                Priority = 2,
                Reason = string.IsNullOrWhiteSpace(req.Reason) ? "Bulk override" : req.Reason.Trim()
            });
            return TypedResults.Accepted("/api/commands/history", envelope);
        }).WithSummary("Queue one override for every device of a kind/category – undone as a single Stack entry.");

        g.MapPost("/undo", Results<Ok<CommandBatch>, NotFound<ProblemDetails>> (CommandStreamState stream) =>
        {
            var reverse = stream.Undo();
            return reverse is null
                ? TypedResults.NotFound(new ProblemDetails { Title = "Nothing to undo.", Status = 404 })
                : TypedResults.Ok(reverse);
        }).WithSummary("Pop the last operator action off the Stack and restore every device it changed.");

        g.MapGet("/history", (CommandStreamState stream, string? mac, int take = 100) =>
            Results.Ok(stream.History(mac, take)))
            .WithSummary("Executed commands, newest first, from the SortedDictionary.");

        return api;
    }
}
