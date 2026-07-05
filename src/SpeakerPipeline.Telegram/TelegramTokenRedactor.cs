using System.Text.RegularExpressions;

namespace SpeakerPipeline.Telegram;

/// <summary>
/// Redacts the Telegram bot token from any string (a URL, a span name). The Bot
/// API authenticates only via the token in the URL path
/// (<c>…/bot&lt;token&gt;/sendMessage</c>), so it would otherwise land in HTTP
/// traces. Used by the host's OpenTelemetry HTTP-client enrichment.
/// </summary>
public static partial class TelegramTokenRedactor
{
    /// <summary>Replaces the <c>bot&lt;id&gt;:&lt;secret&gt;</c> URL segment with a placeholder.</summary>
    public static string Redact(string value) =>
        string.IsNullOrEmpty(value) ? value : BotToken().Replace(value, "bot<redacted>");

    // Telegram tokens are "<bot-id>:<secret>", reached as the "bot<id>:<secret>"
    // URL segment. Match that segment and drop the token.
    [GeneratedRegex(@"bot\d+:[A-Za-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex BotToken();
}
