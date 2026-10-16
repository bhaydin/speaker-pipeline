using System.Text.RegularExpressions;

namespace SpeakerPipeline.Core;

/// <summary>Completed delivery identity is compared independently of edition year.</summary>
public static class CatalogRepeatPolicy
{
    public static bool IsRepeat(EventRecord ev, string talkId, CatalogSnapshot catalog)
    {
        var name = NormalizeName(ev.Name);
        var slug = NormalizeName(ev.Slug);
        var source = EventIdentity(ev.EventUrl);
        return catalog.Deliveries.Any(d => d.TalkId == talkId && d.Status == "completed" &&
            (NormalizeName(d.EventName) == name || source is not null && source == EventIdentity(d.EventUrl) ||
             !string.IsNullOrWhiteSpace(d.EventSeriesId) && (NormalizeName(d.EventSeriesId) == name || NormalizeName(d.EventSeriesId) == slug)));
    }

    private static string? EventIdentity(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var host = uri.IdnHost.Replace("www.", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        var path = Regex.Replace(uri.AbsolutePath.ToLowerInvariant().TrimEnd('/'), @"(?<!\d)20\d{2}(?!\d)", "");
        return host + path;
    }
    private static string NormalizeName(string value) => Regex.Replace(Regex.Replace(value.ToLowerInvariant(), @"\b20\d{2}\b", ""), @"[^\p{L}\p{N}]", "");
}
