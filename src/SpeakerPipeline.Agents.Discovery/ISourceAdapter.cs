using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Discovery;

/// <summary>
/// A raw page pulled from a discovery source, normalized to text ready for
/// extraction.
/// </summary>
/// <param name="Url">URL of the fetched page (ideally canonical).</param>
/// <param name="Source">Which source this page belongs to.</param>
/// <param name="Content">Normalized page text.</param>
/// <param name="DiscoveredVia">
/// The search query that surfaced this page, for traceability. Null for
/// deliberately-watched pages.
/// </param>
/// <param name="MinConfidence">
/// Optional per-page extraction-confidence floor (0–10). Search sources set a
/// higher bar than the watchlist because search results are noisier; null means
/// use the agent default.
/// </param>
public sealed record SourcePage(
    string Url,
    SourceSeenOn Source,
    string Content,
    string? DiscoveredVia = null,
    int? MinConfidence = null);

/// <summary>
/// A candidate the discovery agent should reconcile into the tracker. Carries
/// the reconcile routing (URL, source, discovering query) plus <em>either</em> a
/// raw <see cref="SourcePage"/> that still needs LLM extraction, <em>or</em> a
/// pre-extracted <see cref="ExtractedEvent"/> from a structured feed that
/// bypasses the model entirely. Exactly one of <see cref="Page"/> and
/// <see cref="Extracted"/> is set.
/// </summary>
public sealed record DiscoveryCandidate
{
    /// <summary>URL of the candidate (ideally canonical). Used for logging/provenance.</summary>
    public required string Url { get; init; }

    /// <summary>Which source this candidate belongs to (drives SourceSeenOn on the event).</summary>
    public required SourceSeenOn Source { get; init; }

    /// <summary>The search query that surfaced this candidate, for traceability. Null for watched/structured sources.</summary>
    public string? DiscoveredVia { get; init; }

    /// <summary>Raw page needing LLM extraction (watchlist, targeted search). Null for structured feeds.</summary>
    public SourcePage? Page { get; init; }

    /// <summary>Structured event from a feed that maps straight to the tracker — no LLM call. Null for text pages.</summary>
    public ExtractedEvent? Extracted { get; init; }

    /// <summary>Wraps a raw page that still needs extraction.</summary>
    public static DiscoveryCandidate ForPage(SourcePage page) => new()
    {
        Url = page.Url,
        Source = page.Source,
        DiscoveredVia = page.DiscoveredVia,
        Page = page,
    };

    /// <summary>Wraps an already-extracted event from a structured feed.</summary>
    public static DiscoveryCandidate ForExtracted(
        string url, SourceSeenOn source, ExtractedEvent extracted, string? discoveredVia = null) => new()
    {
        Url = url,
        Source = source,
        DiscoveredVia = discoveredVia,
        Extracted = extracted,
    };
}

/// <summary>
/// Supplies candidates for the discovery agent to reconcile. The watchlist and
/// search adapters return raw pages for extraction; structured-feed adapters
/// (e.g. confs.tech) return pre-extracted candidates. All flow through the same
/// reconciliation and eval core without touching it.
/// </summary>
public interface ISourceAdapter
{
    Task<IReadOnlyList<DiscoveryCandidate>> FetchAsync(CancellationToken ct = default);
}
