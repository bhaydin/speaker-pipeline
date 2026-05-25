namespace SpeakerPipeline.Core;

/// <summary>
/// A specific submission of a talk to an event. Mirrors the Submissions table
/// defined in docs/architecture-table-storage.md.
/// </summary>
public sealed record SubmissionRecord
{
    /// <summary>Slug of the parent event (matches <see cref="EventRecord.Slug"/>).</summary>
    public required string EventSlug { get; init; }

    /// <summary>Composite identifier within the event partition: {talk-slug}-{yyyy-MM-dd}.</summary>
    public required string SubmissionId { get; init; }

    public required string EventName { get; init; }
    public required string TalkSlug { get; init; }
    public required string TalkTitleUsed { get; init; }
    public required string AbstractUsed { get; init; }
    public required DateTimeOffset SubmittedOnUtc { get; init; }
    public required SubmissionStatus Status { get; init; }

    public DateTimeOffset? DecisionReceivedUtc { get; init; }
    public string? ContactPerson { get; init; }
    public string? ContactEmail { get; init; }
    public DateTimeOffset? FollowUpNeededBy { get; init; }
    public string? Notes { get; init; }
    public int SchemaVersion { get; init; } = 1;
}
