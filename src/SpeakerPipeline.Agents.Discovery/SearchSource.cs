using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SpeakerPipeline.Agents.Discovery;

/// <summary>
/// Targeted search-discovery as an <see cref="ISourceAdapter"/>: run the
/// configured queries, dedupe the candidate URLs canonically, fetch each live
/// page, and hand it to the same extract → reconcile pipeline the watchlist
/// uses. Search titles/snippets are never trusted — only the re-fetched page is.
///
/// RespectRobots is not currently enforced via robots.txt parsing; this source
/// performs single-page fetches with an identifying User-Agent and avoids crawling.
/// Full robots.txt parsing is a noted follow-up.
/// </summary>
public sealed class SearchSource(
    HttpClient http,
    ISearchAdapter search,
    IOptions<SearchOptions> options,
    ILogger<SearchSource> logger) : ISourceAdapter
{
    private readonly SearchOptions _options = options.Value;

    public async Task<IReadOnlyList<DiscoveryCandidate>> FetchAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled || _options.Queries.Count == 0)
        {
            return [];
        }

        var hits = new List<SearchHit>();
        foreach (var query in _options.Queries.Take(_options.MaxQueriesPerRun))
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                continue;
            }
            hits.AddRange(await search.SearchAsync(query, _options.MaxResultsPerQuery, ct));
        }

        var deduped = Dedupe(hits);
        logger.LogInformation("Search: {Hits} hits -> {Unique} unique candidate URLs", hits.Count, deduped.Count);

        var candidates = new List<DiscoveryCandidate>();
        foreach (var hit in deduped)
        {
            try
            {
                using var resp = await http.GetAsync(hit.Url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    logger.LogWarning("Search candidate degraded: {Url} -> {Status}", hit.Url, (int)resp.StatusCode);
                    continue;
                }

                var html = await resp.Content.ReadAsStringAsync(ct);
                candidates.Add(DiscoveryCandidate.ForPage(new SourcePage(
                    hit.Url,
                    UrlCanonicalizer.InferSource(hit.Url),
                    WatchlistSource.Normalize(html),
                    DiscoveredVia: hit.Query,
                    MinConfidence: _options.MinConfidenceScore)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Search candidate failed to fetch: {Url}", hit.Url);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Collapses hits to one per canonical URL, keeping the first query that
    /// surfaced it (traceability) and normalizing the URL to its canonical form.
    /// </summary>
    internal static IReadOnlyList<SearchHit> Dedupe(IEnumerable<SearchHit> hits)
    {
        var seen = new Dictionary<string, SearchHit>(StringComparer.Ordinal);

        foreach (var hit in hits)
        {
            var canonical = UrlCanonicalizer.Canonicalize(hit.Url);
            if (canonical.Length > 0 && !seen.ContainsKey(canonical))
            {
                seen[canonical] = hit with { Url = canonical };
            }
        }

        return [.. seen.Values];
    }
}
