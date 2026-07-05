using System.Text;
using System.Text.RegularExpressions;

namespace SpeakerPipeline.Telegram;

/// <summary>
/// Small text helpers shared by the lane and the router. Telegram's HTML parse
/// mode accepts only a handful of tags, so dynamic text is HTML-escaped and the
/// rich HTML of an email digest is flattened to plain text first.
/// </summary>
internal static partial class TelegramText
{
    /// <summary>Escapes the three characters Telegram's HTML parser is sensitive to.</summary>
    public static string Escape(string? s) =>
        (s ?? string.Empty)
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>
    /// Flattens an HTML fragment to readable plain text: block tags become line
    /// breaks, all other tags are dropped, entities are decoded, and runs of
    /// blank lines are collapsed. The result is then safe to <see cref="Escape"/>.
    /// </summary>
    public static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var withBreaks = BlockTag().Replace(html, "\n");
        var text = AnyTag().Replace(withBreaks, string.Empty);
        text = System.Net.WebUtility.HtmlDecode(text);
        text = ManyBlankLines().Replace(text, "\n\n");
        return text.Trim();
    }

    /// <summary>Turns arbitrary text into a URL/RowKey-safe slug.</summary>
    public static string Slugify(string text)
    {
        var lowered = (text ?? string.Empty).Trim().ToLowerInvariant();
        var slug = NonSlugChar().Replace(lowered, "-").Trim('-');
        slug = MultiDash().Replace(slug, "-");
        return slug.Length > 80 ? slug[..80].Trim('-') : slug;
    }

    [GeneratedRegex(@"</(p|div|h[1-6]|li|tr)>|<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTag();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyBlankLines();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugChar();

    [GeneratedRegex("-{2,}")]
    private static partial Regex MultiDash();
}
