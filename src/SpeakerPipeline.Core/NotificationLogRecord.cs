namespace SpeakerPipeline.Core;

/// <summary>
/// A record of one outbound notification — dedupe ledger and digest feed.
/// Prevents re-pinging the same CFP. Backs the NotificationLog table
/// (PartitionKey "yyyy-MM", RowKey notificationId). See docs/BUILD_PLAN.md §3.6.
///
/// NOTE (Milestone 1): the entity, repository, and table exist, but the API
/// endpoints are intentionally deferred to Milestone 2, where the Notifier —
/// the first writer — is built. See ADR 0001 and the BUILD_PLAN M2 section.
/// </summary>
public sealed record NotificationLogRecord
{
    /// <summary>Partition key: the "yyyy-MM" the notification was sent in.</summary>
    public required string Period { get; init; }

    public required string NotificationId { get; init; }
    public required NotificationChannel Channel { get; init; }
    public required NotificationUrgency Urgency { get; init; }
    public required DateTimeOffset SentUtc { get; init; }

    /// <summary>Idempotency key — collapses repeat pings for the same trigger.</summary>
    public required string DedupeKey { get; init; }

    /// <summary>Slug of the Event or Topic this notification refers to.</summary>
    public string? EntityRef { get; init; }

    public string? Summary { get; init; }
    public int SchemaVersion { get; init; } = 1;
}
