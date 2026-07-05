using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Telegram;

/// <summary>
/// Outbound notification lane over the Telegram Bot API. Registered alongside the
/// Email lane, so a digest fans out to both once configured. No-ops when the bot
/// isn't configured.
/// </summary>
public sealed class TelegramLane(
    TelegramClient client,
    IOptions<TelegramOptions> options,
    ILogger<TelegramLane> logger) : INotificationLane
{
    private readonly TelegramOptions _options = options.Value;

    public NotificationChannel Channel => NotificationChannel.Telegram;

    public bool IsEnabled => _options.IsConfigured;

    public async Task SendAsync(Notification notification, CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            logger.LogWarning("Telegram lane skipped: Telegram is not fully configured.");
            return;
        }

        await client.SendMessageAsync(_options.ChatId, Render(notification), ct);
        logger.LogInformation("Telegram sent: '{Subject}' -> chat {ChatId}", notification.Subject, _options.ChatId);
    }

    /// <summary>Bold subject, then the digest body flattened from HTML to text.</summary>
    internal static string Render(Notification n)
    {
        var body = TelegramText.StripHtml(n.HtmlBody);
        var subject = TelegramText.Escape(n.Subject);
        return string.IsNullOrEmpty(body)
            ? $"<b>{subject}</b>"
            : $"<b>{subject}</b>\n\n{TelegramText.Escape(body)}";
    }
}
