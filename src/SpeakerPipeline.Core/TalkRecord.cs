namespace SpeakerPipeline.Core;

/// <summary>
/// A canonical talk in the speaker's repertoire. Mirrors the Talks table
/// defined in docs/architecture-table-storage.md.
/// </summary>
public sealed record TalkRecord
{
    public required string Slug { get; init; }
    public required string CanonicalTitle { get; init; }
    public required Lane Lane { get; init; }

    public string? AbstractShort { get; init; }
    public string? AbstractLong { get; init; }
    public string? BioVariant { get; init; }
    public int? LengthMinutes { get; init; }
    public TalkFormat? Format { get; init; }
    public string? DeckUrl { get; init; }
    public DateTimeOffset? LastDeliveredUtc { get; init; }
    public int DeliveryCount { get; init; }
    public int? ReusabilityScore { get; init; }
    public bool Retired { get; init; }
    public int SchemaVersion { get; init; } = 1;
}
