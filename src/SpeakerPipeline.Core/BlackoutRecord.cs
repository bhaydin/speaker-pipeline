namespace SpeakerPipeline.Core;

/// <summary>
/// A date range Brian is unavailable — the portable system of record for
/// family/blackout dates. The Conflict Checker treats this table as truth; the
/// Concurrency calendar is only a read-only signal. Backs the Blackouts table
/// (PartitionKey "blackout", RowKey blackoutId). See docs/BUILD_PLAN.md §3.5.
/// </summary>
public sealed record BlackoutRecord
{
    public required string BlackoutId { get; init; }
    public required DateTimeOffset StartDate { get; init; }
    public required DateTimeOffset EndDate { get; init; }
    public required string Reason { get; init; }
    public required BlackoutHardness Hardness { get; init; }

    /// <summary>Where the blackout came from (e.g. manual, telegram, concurrency-import).</summary>
    public string? Source { get; init; }

    public DateTimeOffset? CreatedUtc { get; init; }
    public DateTimeOffset? UpdatedUtc { get; init; }
    public int SchemaVersion { get; init; } = 1;
}
