using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Discovery;

/// <summary>
/// Canonicalizes candidate URLs so the same page discovered by different queries
/// (or with different tracking parameters) dedupes to one entry, and identifies
/// which source a URL belongs to.
/// </summary>
internal static class UrlCanonicalizer
{
    /// <summary>
    /// Scheme + host lowercased, query and fragment dropped, trailing slash
    /// removed. Falls back to the trimmed input when the URL doesn't parse.
    /// </summary>
    public static string Canonicalize(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri))
        {
            return url?.Trim() ?? string.Empty;
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length == 0)
        {
            path = "/";
        }

        return $"{scheme}://{host}{port}{path}";
    }

    public static bool IsSessionize(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        return host is "sessionize.com" || host.EndsWith(".sessionize.com", StringComparison.Ordinal);
    }

    /// <summary>Maps a URL to the tracker's source enum (Sessionize vs a direct/official page).</summary>
    public static SourceSeenOn InferSource(string? url) =>
        IsSessionize(url) ? SourceSeenOn.Sessionize : SourceSeenOn.Direct;
}
