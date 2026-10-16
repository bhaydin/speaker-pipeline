using System.Security.Claims;
using SpeakerPipeline.Api.Auth;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Endpoints;

public static class OpportunitiesEndpoints
{
    public static IEndpointRouteBuilder MapOpportunitiesApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v1/opportunities").WithTags("Opportunities").RequireAuthorization(AuthExtensions.PolicyName);
        group.AddEndpointFilter(async (context, next) =>
        {
            if (!context.HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetValue<bool>("Storage:OpportunitiesEnabled"))
                return Results.Problem("Opportunity authority has not been enabled.", statusCode: 503);
            try { return await next(context); }
            catch (OpportunityConflictException ex) { return Results.Json(new { error = ex.Message, current = ex.Current }, statusCode: ex.StaleScoringInput ? 409 : 412); }
            catch (KeyNotFoundException ex) { return Results.Problem(ex.Message, statusCode: 404); }
            catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
            catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 503); }
        });
        group.MapGet("", async (IServiceProvider services, CancellationToken ct) => Results.Ok(await services.GetRequiredService<IOpportunityRepository>().GetAllAsync(ct)));
        group.MapGet("feedback", async (IServiceProvider services, CancellationToken ct, int limit = 20) => Results.Ok(await services.GetRequiredService<IOpportunityRepository>().GetFeedbackAsync(limit, ct)));
        group.MapGet("{slug}", async (string slug, IServiceProvider services, HttpContext context, CancellationToken ct) =>
        {
            var record = await services.GetRequiredService<IOpportunityRepository>().GetAsync(slug, ct);
            if (record is null) return Results.NotFound();
            context.Response.Headers.ETag = record.ETag;
            return Results.Ok(record);
        });
        group.MapGet("{slug}/history", async (string slug, IServiceProvider services, CancellationToken ct) => Results.Ok(await services.GetRequiredService<IOpportunityRepository>().GetHistoryAsync(slug, ct)));
        group.MapPost("{slug}/actions", async (string slug, OpportunityActionRequest request, HttpContext context, IServiceProvider services, CancellationToken ct) =>
        {
            var etag = context.Request.Headers.IfMatch.ToString();
            if (string.IsNullOrWhiteSpace(etag) || etag == "*") return Results.Problem("An exact If-Match header is required.", statusCode: 428);
            if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) return Results.Problem("An idempotency key is required.", statusCode: 428);
            var headerKey = context.Request.Headers["Idempotency-Key"].ToString();
            if (!string.IsNullOrEmpty(headerKey) && headerKey != request.IdempotencyKey)
                return Results.BadRequest(new { error = "Idempotency-Key must match the request body." });
            var actor = context.User.FindFirstValue("oid") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "authenticated-operator";
            var record = await services.GetRequiredService<IOpportunityRepository>().ApplyActionAsync(slug, request, etag, actor, ct);
            context.Response.Headers.ETag = record.ETag;
            return Results.Ok(record);
        });
        group.MapPost("{slug}/decisions", async (string slug, OpportunityScoringRequest request, HttpContext context, IServiceProvider services, CancellationToken ct) =>
        {
            if (slug != request.Decision.EventSlug) return Results.BadRequest(new { error = "URL slug must match the scored event." });
            ScoringDecision.ValidateScore(request.Decision.FitScore, nameof(request.Decision.FitScore));
            ScoringDecision.ValidateScore(request.Decision.EffortScore, nameof(request.Decision.EffortScore));
            ScoringDecision.ValidateScore(request.Decision.ConfidenceScore, nameof(request.Decision.ConfidenceScore));
            if (!Enum.IsDefined(request.Decision.Recommendation) || string.IsNullOrWhiteSpace(request.Decision.Rationale))
                return Results.BadRequest(new { error = "A defined verdict and rationale are required." });
            var catalog = await ValidateCatalogAsync(request, services, ct);
            var record = await services.GetRequiredService<IOpportunityRepository>().ApplyDecisionAsync(request, catalog, ct);
            context.Response.Headers.ETag = record.ETag;
            return Results.Ok(record);
        });
        group.MapPost("{slug}/reconcile-submissions", async (string slug, HttpContext context, IServiceProvider services, CancellationToken ct) =>
        {
            var etag = context.Request.Headers.IfMatch.ToString();
            if (string.IsNullOrWhiteSpace(etag) || etag == "*") return Results.Problem("An exact If-Match header is required.", statusCode: 428);
            var submissions = await services.GetRequiredService<ISubmissionRepository>().GetForEventAsync(slug, ct);
            var record = await services.GetRequiredService<IOpportunityRepository>().ReconcileSubmissionsAsync(slug, submissions, etag, ct);
            context.Response.Headers.ETag = record.ETag;
            return Results.Ok(record);
        });
        return routes;
    }

    private static async Task<CatalogSnapshot?> ValidateCatalogAsync(OpportunityScoringRequest request, IServiceProvider services, CancellationToken ct)
    {
        var decision = request.Decision;
        var actionable = decision.Recommendation is Recommendation.SubmitNow or Recommendation.Outreach;
        var catalogEnabled = services.GetRequiredService<IConfiguration>().GetValue<bool>("Catalog:Enabled");
        if (string.IsNullOrWhiteSpace(decision.CatalogGenerationId))
        {
            if (decision.MatchedTalks.Count > 0 || actionable && catalogEnabled)
                throw new ArgumentException("A published catalog generation is required for catalog matches.");
            return null;
        }
        var catalog = await services.GetRequiredService<ICatalogRepository>().GetGenerationAsync(decision.CatalogGenerationId, ct)
            ?? throw new ArgumentException("The referenced catalog generation was not published.");
        if (actionable && decision.MatchedTalks.Count == 0) throw new ArgumentException("Actionable catalog decisions require a matched talk.");
        if (decision.InputRevision is { } input && input != request.ScoringInputRevision.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new ArgumentException("Decision input revision must match the scoring request.");
        foreach (var match in decision.MatchedTalks)
        {
            var talk = catalog.Talks.SingleOrDefault(t => t.TalkId == match.TalkId);
            if (talk is null || !talk.Eligible || talk.Status.Equals("retired", StringComparison.OrdinalIgnoreCase) ||
                talk.CurrentVersionId != match.VersionId || talk.Title != match.Title ||
                !catalog.Versions.Any(v => v.TalkId == match.TalkId && v.VersionId == match.VersionId) || string.IsNullOrWhiteSpace(match.Rationale))
                throw new ArgumentException("Matched talks must reference an eligible talk and its current version in the recorded catalog generation.");
        }
        return catalog;
    }
}
