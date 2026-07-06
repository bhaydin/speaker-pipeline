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

    public async Task<IReadOnlyList<DiscoveryCandidate>> FetchAsync(CancellationToken ct = default)
    {
        var candidates = new List<DiscoveryCandidate>();

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
                candidates.Add(DiscoveryCandidate.ForPage(new SourcePage(target.Url, target.Source, Normalize(html))));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Discovery source degraded: {Url} failed to fetch", target.Url);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Strips a page to readable text so extraction spends tokens on content,
    /// not markup: prefer the main/article region when a page marks one (nav,
    /// footers, and cookie banners never carry CFP facts), then drop
    /// script/style, remove tags, decode entities, collapse whitespace, and cap
    /// length. The cap is generous on a nano-class model so long CFP pages keep
    /// their deadline/venue detail instead of being truncated mid-page.
    /// </summary>
    internal static string Normalize(string html)
    {
        var region = MainRegion(html) ?? html;
        var withoutScripts = ScriptStyle().Replace(region, " ");
        var withoutTags = Tags().Replace(withoutScripts, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        var collapsed = Whitespace().Replace(decoded, " ").Trim();

        const int maxChars = 24_000; // token budget for a small extraction model
        return collapsed.Length > maxChars ? collapsed[..maxChars] : collapsed;
    }

    /// <summary>
    /// Returns the inner HTML of the first <c>&lt;main&gt;</c> or
    /// <c>&lt;article&gt;</c> element, or null when the page marks neither. Keeps
    /// the whole-page fallback intact for sites without semantic landmarks.
    /// </summary>
    private static string? MainRegion(string html)
    {
        var main = MainOrArticle().Match(html);
        return main.Success ? main.Groups["body"].Value : null;
    }

    [GeneratedRegex("<(script|style)[^>]*>.*?</\\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptStyle();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("<(?<tag>main|article)[^>]*>(?<body>.*?)</\\k<tag>>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex MainOrArticle();
}
