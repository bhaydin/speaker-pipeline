using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Discovery;

/// <summary>
/// Configuration bound from the "Discovery" section.
/// </summary>
public sealed class DiscoveryOptions
{
    public const string SectionName = "Discovery";

    public string AgentName { get; set; } = "discovery-agent";
    public string AgentVersion { get; set; } = "v1";

    // --- Chat provider (extraction runs on a small/cheap model) --------------

    public string Provider { get; set; } = "foundry";
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Foundry deployment used for extraction — a small model (e.g. gpt-5-nano).</summary>
    public string ModelName { get; set; } = "<placeholder>";

    public string? ManagedIdentityClientId { get; set; }

    // --- Sources -------------------------------------------------------------

    /// <summary>
    /// Pages to monitor: known Sessionize CFP pages, official event pages, and
    /// organizer/community pages. Each is fetched, extracted, and reconciled.
    /// </summary>
    public List<WatchTarget> Watchlist { get; set; } = [];

    /// <summary>Max source pages processed per run — keeps a run bounded and token-frugal.</summary>
    public int MaxTargetsPerRun { get; set; } = 25;
}

/// <summary>A single page the discovery agent monitors.</summary>
public sealed class WatchTarget
{
    public string Url { get; set; } = string.Empty;

    /// <summary>Which source this page belongs to (drives SourceSeenOn on the event).</summary>
    public SourceSeenOn Source { get; set; } = SourceSeenOn.Sessionize;
}
