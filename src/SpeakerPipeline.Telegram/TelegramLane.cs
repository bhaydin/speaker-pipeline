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
    ILogger<TelegramLane> logger,
    TelegramInteractions? interactions = null,
    TimeProvider? timeProvider = null,
    IOptions<PipelineNotificationOptions>? pipelineOptions = null) : INotificationLane
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

        foreach (var part in Prepare(notification)) await SendPartAsync(part, ct);
        logger.LogInformation("Telegram sent: '{Subject}' -> chat {ChatId}", notification.Subject, _options.ChatId);
    }

    public IReadOnlyList<NotificationPart> Prepare(Notification notification) => TelegramMessageFormatter.Parts(
        pipelineOptions?.Value.OpportunitiesEnabled == true ? notification : notification with
        { Groups = notification.Groups.Select(g => g with { Items = g.Items.Select(i => i with { Actions = false }).ToArray() }).ToArray() });

    public async Task<NotificationDelivery> SendPartAsync(NotificationPart part, CancellationToken ct = default)
    {
        var key = OperationState.Key((part.Notification.DeliveryKey ?? part.Notification.DedupeKey ?? part.Notification.Subject) + part.Id + part.Text);
        var keyboard = interactions is null ? null : await interactions.PrepareAsync(key, part, ct);
        var text = part.Text ?? Render(part.Notification);
        if (pipelineOptions?.Value.OpportunitiesEnabled != true)
            text = System.Text.RegularExpressions.Regex.Replace(text, @"<i>Item \d+: Awaiting action</i>\s*", "");
        var sent = await client.SendAsync(_options.ChatId, text, keyboard,
            PipelineHealth.IsQuiet((timeProvider ?? TimeProvider.System).GetUtcNow()), ct);
        if (interactions is not null)
        {
            try { await interactions.BindAsync(key, sent, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Telegram message was sent but interaction receipt could not be saved ({Type}).", ex.GetType().Name);
            }
        }
        return new("Sent", [sent.MessageId.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
    }

    public async Task ReconcileReceiptAsync(NotificationPart part, NotificationDelivery delivery, CancellationToken ct = default)
    {
        if (interactions is null || !long.TryParse(delivery.MessageIds?.FirstOrDefault(), out var messageId)) return;
        var key = OperationState.Key((part.Notification.DeliveryKey ?? part.Notification.DedupeKey ?? part.Notification.Subject) + part.Id + part.Text);
        await interactions.BindAsync(key, new TelegramSentMessage(messageId, new TelegramChat { Id = _options.ChatId }), ct);
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
