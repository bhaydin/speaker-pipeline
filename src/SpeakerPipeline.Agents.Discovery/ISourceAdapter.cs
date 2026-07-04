using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Discovery;

/// <summary>
/// A raw page pulled from a discovery source, normalized to text ready for
/// extraction.
/// </summary>
public sealed record SourcePage(string Url, SourceSeenOn Source, string Content);

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
