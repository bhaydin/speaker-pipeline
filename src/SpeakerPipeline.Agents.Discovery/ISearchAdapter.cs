namespace SpeakerPipeline.Agents.Discovery;

/// <summary>
/// One result returned by a search provider. Title/snippet/URL are candidate
/// signals ONLY — never authoritative. Every hit must be fetched and re-verified
/// against the live page before anything is trusted.
/// </summary>
public sealed record SearchHit(string Url, string? Title, string? Snippet, string Query);

/// <summary>
/// Provider-neutral targeted search. The concrete adapter (Google Programmable
/// Search first) is swappable for Bing / SerpAPI / Brave / Tavily / Exa without
/// touching the discovery pipeline. Fragility is accepted: a failing query
/// returns an empty list, never throws the run down.
/// </summary>
public interface ISearchAdapter
{
    Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int maxResults, CancellationToken ct = default);
}
