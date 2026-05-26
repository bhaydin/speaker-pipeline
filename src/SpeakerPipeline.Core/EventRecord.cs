namespace SpeakerPipeline.Core;

/// <summary>
/// A speaking opportunity tracked by the pipeline. Mirrors the Events table
/// defined in docs/architecture-table-storage.md.
/// </summary>
public sealed record EventRecord
{
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required EventType EventType { get; init; }
    public required EventCategory Category { get; init; }
    public required Priority Priority { get; init; }

    public IReadOnlyList<Lane> FocusFit { get; init; } = [];
    public string? StatusDetail { get; init; }
    public DateTimeOffset? EventDateStart { get; init; }
    public DateTimeOffset? EventDateEnd { get; init; }
    public DateTimeOffset? CfpDeadline { get; init; }
    public string? CfpUrl { get; init; }
    public string? EventUrl { get; init; }
    public string? Location { get; init; }
    public EventFormat? Format { get; init; }
    public TravelBurden? TravelBurden { get; init; }
    public string? NextAction { get; init; }
    public string? Notes { get; init; }
    public SourceSeenOn? SourceSeenOn { get; init; }
    public DateTimeOffset? LastVerifiedUtc { get; init; }
    public bool DoNotResurface { get; init; }
    public string? DiscoveredByAgent { get; init; }
    public string? DecidedByAgent { get; init; }
    public int SchemaVersion { get; init; } = 1;
}
