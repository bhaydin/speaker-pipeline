using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SpeakerPipeline.Agents.Discovery;

/// <summary>
/// Fetches the configured watchlist pages and normalizes each to text.
/// Fragility is accepted by design (BUILD_PLAN §9): a page that fails to fetch
/// is logged and skipped — never a hard dependency. One attempt, no retry loop.
/// </summary>
public sealed partial class WatchlistSource(
    HttpClient http,
    IOptions<DiscoveryOptions> options,
    ILogger<WatchlistSource> logger) : ISourceAdapter
{
    private readonly DiscoveryOptions _options = options.Value;

    public async Task<IReadOnlyList<SourcePage>> FetchAsync(CancellationToken ct = default)
    {
        var pages = new List<SourcePage>();

        foreach (var target in _options.Watchlist.Take(_options.MaxTargetsPerRun))
        {
            if (string.IsNullOrWhiteSpace(target.Url))
            {
                continue;
            }

            try
            {
                using var resp = await http.GetAsync(target.Url, ct);
                if (resp.StatusCode != HttpStatusCode.OK)
                {
                    logger.LogWarning("Discovery source degraded: {Url} returned {Status}", target.Url, (int)resp.StatusCode);
                    continue;
                }

                var html = await resp.Content.ReadAsStringAsync(ct);
                pages.Add(new SourcePage(target.Url, target.Source, Normalize(html)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Discovery source degraded: {Url} failed to fetch", target.Url);
            }
        }

        return pages;
    }

    /// <summary>
    /// Strips a page to readable text so extraction spends tokens on content,
    /// not markup: drop script/style, remove tags, decode entities, collapse
    /// whitespace, and cap length.
    /// </summary>
    internal static string Normalize(string html)
    {
        var withoutScripts = ScriptStyle().Replace(html, " ");
        var withoutTags = Tags().Replace(withoutScripts, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        var collapsed = Whitespace().Replace(decoded, " ").Trim();

        const int maxChars = 12_000; // token budget for a small extraction model
        return collapsed.Length > maxChars ? collapsed[..maxChars] : collapsed;
    }

    [GeneratedRegex("<(script|style)[^>]*>.*?</\\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptStyle();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();
}
