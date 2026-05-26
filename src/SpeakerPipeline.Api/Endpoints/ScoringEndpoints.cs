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

    private static async Task<IResult> GetCandidates(IEventRepository events, CancellationToken ct)
    {
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
    ///   Skip      → Skip + DoNotResurface=true
    /// </summary>
    private static async Task<IResult> PostDecision(ScoringDecision decision, IEventRepository events, CancellationToken ct)
    {
        var existing = await events.GetAsync(decision.EventSlug, ct);
        if (existing is null)
        {
            return Results.Problem($"Unknown event '{decision.EventSlug}'.", statusCode: StatusCodes.Status404NotFound);
        }

        var (category, priority) = MapRecommendation(decision);
        var doNotResurface = existing.DoNotResurface || decision.Recommendation == Recommendation.Skip;

        var updated = existing with
        {
            Category = category,
            Priority = priority,
            NextAction = ShortRationale(decision),
            DecidedByAgent = decision.DecidedByAgent ?? existing.DecidedByAgent,
            DoNotResurface = doNotResurface,
        };

        await events.UpsertAsync(updated, ct);
        return Results.Ok(decision);
    }

    private static (EventCategory category, Priority priority) MapRecommendation(ScoringDecision d) =>
        d.Recommendation switch
        {
            Recommendation.SubmitNow => (EventCategory.SubmitNow, PriorityFromFit(d.FitScore)),
            Recommendation.Outreach  => (EventCategory.Outreach,  Priority.Medium),
            Recommendation.Monitor   => (EventCategory.Monitor,   Priority.Low),
            Recommendation.Pass      => (EventCategory.Pass,      Priority.NA),
            Recommendation.Skip      => (EventCategory.Skip,      Priority.NA),
            _                        => (EventCategory.Monitor,   Priority.NA),
        };

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
