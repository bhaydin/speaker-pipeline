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
/// Supplies candidate pages for the discovery agent to extract from. The
/// watchlist adapter returns configured pages; future adapters (targeted
/// search, aggregators) implement the same contract without touching the
/// extraction, reconciliation, or eval core.
/// </summary>
public interface ISourceAdapter
{
    Task<IReadOnlyList<SourcePage>> FetchAsync(CancellationToken ct = default);
}
