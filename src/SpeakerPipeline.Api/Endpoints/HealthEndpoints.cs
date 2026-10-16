using SpeakerPipeline.Api.Auth;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/health", () => Results.Ok(new { status = "ok" }))
            .AllowAnonymous()
            .WithTags("Health")
            .WithName("Health");
        routes.MapGet("/health/ready", async (IEventRepository events, ITalkRepository talks,
            ISubmissionRepository submissions, ITopicRepository topics, IBlackoutRepository blackouts,
            INotificationLogRepository notifications, CancellationToken ct) =>
        {
            await foreach (var _ in events.QueryAsync(ct)) { break; }
            await talks.GetAllAsync(ct);
            await submissions.GetForEventAsync("readiness-probe", ct);
            await topics.GetAllAsync(ct);
            await blackouts.GetAllAsync(ct);
            await notifications.GetForPeriodAsync(DateTimeOffset.UtcNow.ToString("yyyy-MM"), ct);
            return Results.Ok(new { status = "ready" });
        }).RequireAuthorization(AuthExtensions.PolicyName).WithTags("Health").WithName("Readiness");
        return routes;
    }
}
