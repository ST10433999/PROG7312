using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Services;
using SmartX.Shared.Models;

namespace SmartX.Api.Endpoints;

public static class AttachmentEndpoints
{
    public static RouteGroupBuilder MapAttachmentEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/sensors/{id:guid}/attachments").WithTags("Attachments");

        g.MapGet("/", (Guid id, GatewayState state) =>
            state.Sensors.Values.FirstOrDefault(s => s.Id == id) is { } s ? Results.Ok(s.Attachments) : Results.NotFound());

        // Multipart streaming upload – accepts several files in one request.
        g.MapPost("/", async (Guid id, HttpRequest request, GatewayState state, AttachmentService files, CancellationToken ct) =>
        {
            var sensor = state.Sensors.Values.FirstOrDefault(s => s.Id == id);
            if (sensor is null) return Results.NotFound();
            if (!request.HasFormContentType) return Results.BadRequest(new ProblemDetails { Title = "Expected multipart/form-data." });

            var form = await request.ReadFormAsync(ct);
            if (form.Files.Count == 0) return Results.BadRequest(new ProblemDetails { Title = "No files in request." });
            if (form.Files.Count > AttachmentService.MaxFilesPerRequest)
                return Results.BadRequest(new ProblemDetails { Title = $"At most {AttachmentService.MaxFilesPerRequest} files per request.", Status = 400 });

            var saved = new List<AttachmentInfo>();
            var rejected = new List<object>();
            foreach (var file in form.Files)
            {
                var problem = AttachmentService.Validate(file.FileName, file.ContentType, file.Length);
                if (problem is not null) { rejected.Add(new { file.FileName, reason = problem }); continue; }
                try
                {
                    await using var stream = file.OpenReadStream();
                    saved.Add(await files.SaveAsync(sensor, file.FileName, file.ContentType, stream, ct));
                }
                catch (InvalidOperationException ex) { rejected.Add(new { file.FileName, reason = ex.Message }); }
            }
            return Results.Ok(new { saved, rejected });
        })
        .DisableAntiforgery()
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(AttachmentService.MaxRequestBytes))
        .WithSummary("Attach config files, photos or logs to a sensor (streamed, AES-256 encrypted at rest).");

        g.MapGet("/{attachmentId:guid}", (Guid id, Guid attachmentId, GatewayState state, AttachmentService files) =>
        {
            var sensor = state.Sensors.Values.FirstOrDefault(s => s.Id == id);
            var info = sensor?.Attachments.FirstOrDefault(a => a.Id == attachmentId);
            if (sensor is null || info is null) return Results.NotFound();
            var stream = files.Open(sensor, info);
            return stream is null ? Results.NotFound() : Results.Stream(stream, info.ContentType, info.FileName);
        }).WithSummary("Download (decrypts on the fly).");

        g.MapDelete("/{attachmentId:guid}", (Guid id, Guid attachmentId, GatewayState state, AttachmentService files) =>
        {
            var sensor = state.Sensors.Values.FirstOrDefault(s => s.Id == id);
            var info = sensor?.Attachments.FirstOrDefault(a => a.Id == attachmentId);
            if (sensor is null || info is null) return Results.NotFound();
            files.Delete(sensor, info);
            return Results.NoContent();
        });

        return api;
    }
}
