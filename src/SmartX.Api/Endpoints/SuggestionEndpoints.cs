using SmartX.Api.Services;
using SmartX.Shared.Commands;

namespace SmartX.Api.Endpoints;

// Part 2 — /api/suggestions – the predictive engine and operator-search logging.
public static class SuggestionEndpoints
{
    public static RouteGroupBuilder MapSuggestionEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/suggestions").WithTags("Suggestions");

        g.MapGet("/", (PredictiveEngine engine, CommandStreamState stream, int take = 5) =>
            Results.Ok(engine.Suggest(stream, Math.Clamp(take, 1, 20))))
            .WithSummary("Suggested actions and automated insights.");

        g.MapGet("/stats", (PredictiveEngine engine) => Results.Ok(engine.Stats()))
            .WithSummary("What the engine has learned so far.");

        g.MapPost("/search", (SearchLogRequest req, PredictiveEngine engine, CommandStreamState stream) =>
        {
            var errors = SensorEndpoints.ValidateModel(req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            // A search only teaches the engine when it pins down exactly one device.
            var matches = stream.SearchDevices(req.Term);
            if (matches.Count == 1) engine.ObserveInspection(matches[0].MacAddress, matches[0].Name);
            return Results.Ok(new { matches = matches.Count, learned = matches.Count == 1 });
        }).WithSummary("The dashboard reports operator searches so the engine can learn which devices are looked up after which alerts.");

        // Part 3 — operator feedback (lecturer feedback on Part 2).
        g.MapPost("/feedback", (SuggestionFeedbackRequest req, PredictiveEngine engine) =>
        {
            var errors = SensorEndpoints.ValidateModel(req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            return engine.Feedback(req.RuleKey, req.Verdict) is { } result
                ? Results.Ok(result)
                : Results.BadRequest(new Microsoft.AspNetCore.Mvc.ProblemDetails { Title = "Unknown suggestion rule key.", Status = 400 });
        }).WithSummary("Accept or dismiss a suggestion. Accepting reinforces the rule; dismissing halves its weight and snoozes it for 30 minutes.");

        return api;
    }
}
