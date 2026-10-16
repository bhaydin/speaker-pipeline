using SpeakerPipeline.Api.Auth;
using SpeakerPipeline.Api.Validation;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Endpoints;

public static class ScoringEndpoints
{
    /// <summary>
    /// Categories the scoring agent should look at: things that need a
    /// fresh decision (Monitor / SubmitNow), plus outreach-style entries
    /// that may shift to SubmitNow when an organizer responds.
    /// </summary>
    private static readonly EventCategory[] CandidateCategories =
    [
        EventCategory.Monitor,
        EventCategory.SubmitNow,
        EventCategory.Outreach,
    ];

    public static IEndpointRouteBuilder MapScoringApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v1/scoring")
            .WithTags("Scoring")
            .RequireAuthorization(AuthExtensions.PolicyName);

        group.MapGet("candidates", GetCandidates).WithName("GetScoringCandidates");

        group.MapPost("decisions", PostDecision)
            .AddEndpointFilter<ValidationFilter<ScoringDecision>>()
            .WithName("PostScoringDecision");

        return routes;
    }

    private static async Task<IResult> GetCandidates(IEventRepository events, IConfiguration config, IServiceProvider services, CancellationToken ct)
    {
        if (config.GetValue<bool>("Storage:OpportunitiesEnabled"))
        {
            var opportunities = await services.GetRequiredService<IOpportunityRepository>().GetAllAsync(ct);
            return Results.Ok(opportunities.Where(o => !o.DoNotResurface && o.RecordedSubmissionStatus is not (SubmissionStatus.Submitted or SubmissionStatus.InReview or SubmissionStatus.Accepted) && o.Status is OpportunityStatus.New or OpportunityStatus.Monitor or OpportunityStatus.Outreach or OpportunityStatus.SubmitNow or OpportunityStatus.Drafting or OpportunityStatus.NotPursuing)
                .Select(OpportunityMapping.Project).OrderBy(e => e.CfpDeadline ?? DateTimeOffset.MaxValue).ToArray());
        }
        var byCategory = await events.GetByCategoryAsync(CandidateCategories, ct);
        var filtered = byCategory
            .Where(e => !e.DoNotResurface)
            .OrderBy(e => e.CfpDeadline ?? DateTimeOffset.MaxValue)
            .ToArray();
        return Results.Ok(filtered);
    }

    /// <summary>
    /// Records a scoring decision against an event. Updates Category,
    /// Priority, NextAction, and DecidedByAgent on the corresponding
    /// EventRecord. The mapping from Recommendation → EventCategory is:
    ///   SubmitNow → SubmitNow
    ///   Outreach  → Outreach
    ///   Monitor   → Monitor
    ///   Pass      → Pass
    ///   Skip      → Pass, without changing explicit suppression
    /// </summary>
    private static async Task<IResult> PostDecision(ScoringDecision decision, IEventRepository events, IConfiguration config, CancellationToken ct)
    {
        if (config.GetValue<bool>("Storage:OpportunitiesEnabled"))
            return Results.Problem("Use the conditional opportunity decision endpoint after authority cutover.", statusCode: 409);
        var existing = await events.GetAsync(decision.EventSlug, ct);
        if (existing is null)
        {
            return Results.Problem($"Unknown event '{decision.EventSlug}'.", statusCode: StatusCodes.Status404NotFound);
        }

        try
        {
            OpportunityDecisionPolicy.Validate(new OpportunityRecord { Event = existing }, new OpportunityScoringRequest
            {
                Decision = decision, ExpectedETag = "legacy", ScoringInputRevision = existing.ScoringInputRevision, IdempotencyKey = "legacy-validation",
            }, DateTimeOffset.UtcNow);
        }
        catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
        var (category, priority) = MapRecommendation(decision);

        var updated = existing with
        {
            Category = category,
            Priority = priority,
            NextAction = ShortRationale(decision),
            DecidedByAgent = decision.DecidedByAgent ?? existing.DecidedByAgent,
            DoNotResurface = existing.DoNotResurface,
        };

        await events.UpsertAsync(updated, ct);
        return Results.Ok(decision);
    }

    private static (EventCategory category, Priority priority) MapRecommendation(ScoringDecision d) =>
        (ScoringMap.ToCategory(d.Recommendation), d.Recommendation switch
        {
            Recommendation.SubmitNow => PriorityFromFit(d.FitScore),
            Recommendation.Outreach  => Priority.Medium,
            Recommendation.Monitor   => Priority.Low,
            _                        => Priority.NA,
        });

    private static Priority PriorityFromFit(int fit) => fit switch
    {
        >= 9 => Priority.High,
        >= 7 => Priority.MediumHigh,
        >= 5 => Priority.Medium,
        _    => Priority.Low,
    };

    private static string ShortRationale(ScoringDecision d)
    {
        const int max = 240;
        return d.Rationale.Length <= max
            ? d.Rationale
            : string.Concat(d.Rationale.AsSpan(0, max - 1), "…");
    }
}
