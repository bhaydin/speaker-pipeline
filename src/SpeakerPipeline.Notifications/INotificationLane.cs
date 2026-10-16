using SpeakerPipeline.Core;

namespace SpeakerPipeline.Notifications;

/// <summary>
/// A delivery channel. Email ships first; Telegram and Teams are added later by
/// registering another lane — callers and the Notifier don't change. A disabled
/// or unconfigured lane no-ops rather than throwing.
/// </summary>
public interface INotificationLane
{
    NotificationChannel Channel { get; }

    /// <summary>True when the lane is configured and will actually send.</summary>
    bool IsEnabled { get; }

    Task SendAsync(Notification notification, CancellationToken ct = default);

    IReadOnlyList<NotificationPart> Prepare(Notification notification) => [new("0", notification)];

    async Task<NotificationDelivery> SendPartAsync(NotificationPart part, CancellationToken ct = default)
    {
        await SendAsync(part.Notification, ct);
        return new NotificationDelivery("Sent");
    }

    Task ReconcileReceiptAsync(NotificationPart part, NotificationDelivery delivery, CancellationToken ct = default)
        => Task.CompletedTask;
}
