namespace SpeakerPipeline.Telegram;

/// <summary>
/// Configuration bound from the "Telegram" section. The bot token and webhook
/// secret are Key Vault references in deployed environments — never committed.
/// </summary>
public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public bool Enabled { get; set; }

    /// <summary>Bot token from BotFather. Key Vault reference in deployment.</summary>
    public string? BotToken { get; set; }

    /// <summary>
    /// The one chat allowed to drive the pipeline (Brian's DM with the bot).
    /// Inbound messages from any other chat are ignored — this is the bot's
    /// authorization boundary, since anyone can find a public bot.
    /// </summary>
    public long ChatId { get; set; }

    /// <summary>
    /// Shared secret echoed by Telegram in the <c>X-Telegram-Bot-Api-Secret-Token</c>
    /// header (set at <c>setWebhook</c> time). The webhook rejects requests that
    /// don't present it, so the anonymous endpoint isn't actually open.
    /// </summary>
    public string? WebhookSecret { get; set; }

    /// <summary>True when the outbound lane can send (token + destination chat).</summary>
    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(BotToken)
        && ChatId != 0;
}
