using SpeakerPipeline.Api.Auth;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Endpoints;

/// <summary>
/// The NotificationLog surface — the dedupe ledger and digest feed. Deferred
/// from Milestone 1 (nothing wrote to it until the Notifier existed); the
/// Notifier is its first writer. See docs/BUILD_PLAN.md §3.6 / M2.
/// </summary>
public static class NotificationsEndpoints
{
    public static IEndpointRouteBuilder MapNotificationsApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v1/notifications")
            .WithTags("Notifications")
            .RequireAuthorization(AuthExtensions.PolicyName);

        // All notifications sent in a period ("yyyy-MM") — powers dedup and the digest.
        group.MapGet("{period}", GetForPeriod).WithName("GetNotificationsForPeriod");

        // Record a sent notification (idempotent on Period + NotificationId).
        group.MapPost("", PostNotification).WithName("LogNotification");

        return routes;
    }

    private static async Task<IResult> GetForPeriod(INotificationLogRepository log, string period, CancellationToken ct)
        => Results.Ok(await log.GetForPeriodAsync(period, ct));

    private static async Task<IResult> PostNotification(NotificationLogRecord record, INotificationLogRepository log, CancellationToken ct)
    {
        await log.UpsertAsync(record, ct);
        return Results.Ok(record);
    }
}
