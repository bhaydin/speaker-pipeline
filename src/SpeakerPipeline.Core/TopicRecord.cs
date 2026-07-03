namespace SpeakerPipeline.Core;

/// <summary>
/// An idea in the topic funnel — the entry point for "AI memories become a
/// talk pipeline." Injected from any AI surface via the MCP server. Backs the
/// Topics table (PartitionKey "topic", RowKey topicId slug).
/// See docs/BUILD_PLAN.md §3.4.
/// </summary>
public sealed record TopicRecord
{
    public required string TopicId { get; init; }
    public required string Title { get; init; }
    public required TopicStage Stage { get; init; }
    public required TopicSource Source { get; init; }

    public string? OneLiner { get; init; }

    /// <summary>Portfolio lane this idea feeds, if known yet.</summary>
    public Lane? Lane { get; init; }

    public EffortClass? EffortClass { get; init; }
    public string? Notes { get; init; }
    public IReadOnlyList<string> RelatedContentUrls { get; init; } = [];

    public DateTimeOffset? CreatedUtc { get; init; }
    public DateTimeOffset? UpdatedUtc { get; init; }
    public int SchemaVersion { get; init; } = 1;
}
