using Microsoft.Extensions.Logging;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Notifications;

public interface INotifier
{
    Task NotifyAsync(Notification notification, CancellationToken ct = default);
}

/// <summary>
/// Routes a notification to every enabled lane, honoring the dedupe key against
/// the period's NotificationLog, and records what was delivered. If nothing
/// delivered, nothing is logged — so a transient failure can retry next run.
/// </summary>
public sealed class Notifier(
    IEnumerable<INotificationLane> lanes,
    ISpeakerPipelineApiClient api,
    ILogger<Notifier> logger,
    DeliveryCoordinator? coordinator = null,
    NotificationOutbox? outbox = null) : INotifier
{
    private readonly IReadOnlyList<INotificationLane> _lanes = [.. lanes];

    public async Task NotifyAsync(Notification notification, CancellationToken ct = default)
    {
        var enabled = _lanes.Where(l => l.IsEnabled).ToArray();
        if (enabled.Length == 0)
        {
            logger.LogInformation("Notify skipped: no enabled lanes.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var period = now.ToString("yyyy-MM");
        var deliveryKey = notification.DeliveryKey ?? notification.DedupeKey
            ?? OperationState.Key(notification.Subject + notification.HtmlBody + System.Text.Json.JsonSerializer.Serialize(notification.Groups));

        IReadOnlyList<NotificationLogRecord> alreadySent = [];
        if (!string.IsNullOrWhiteSpace(notification.DedupeKey))
        {
            alreadySent = await api.GetNotificationsAsync(period, ct);
            if (coordinator is null && alreadySent.Any(s => string.Equals(s.DedupeKey, notification.DedupeKey, StringComparison.Ordinal)))
            {
                logger.LogInformation("Notify skipped: dedupe key '{Key}' already sent in {Period}.", notification.DedupeKey, period);
                return;
            }
        }

        var delivered = new List<NotificationChannel>();
        foreach (var lane in enabled)
        {
            if (alreadySent.Any(s => s.Channel == lane.Channel && s.DedupeKey == notification.DedupeKey)) continue;
            try
            {
                var allSent = true;
                foreach (var part in lane.Prepare(notification))
                {
                    var result = outbox is not null
                        ? await outbox.DeliverAsync($"{deliveryKey}:{lane.Channel}:{part.Id}", lane, part, ct)
                        : coordinator is null
                        ? await lane.SendPartAsync(part, ct)
                        : await coordinator.DeliverAsync($"{deliveryKey}:{lane.Channel}:{part.Id}",
                            () => lane.SendPartAsync(part, ct), ct);
                    allSent &= result.Status == "Sent";
                    if (result.Status is "Failed" or "Uncertain") logger.LogError("Notification delivery {Channel} {Part} ended {Status}.", lane.Channel, part.Id, result.Status);
                }
                if (allSent) delivered.Add(lane.Channel);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Notify: lane {Channel} failed.", lane.Channel);
            }
        }

        if (delivered.Count == 0)
        {
            return;
        }

        foreach (var channel in delivered)
        {
            try
            {
                await api.LogNotificationAsync(new NotificationLogRecord
                {
                    Period = period,
                    NotificationId = coordinator is null ? Guid.NewGuid().ToString("n")
                        : OperationState.Key($"{deliveryKey}:{channel}"),
                    Channel = channel,
                    Urgency = notification.Urgency,
                    SentUtc = now,
                    DedupeKey = notification.DedupeKey ?? string.Empty,
                    EntityRef = notification.EntityRef,
                    Summary = notification.Subject,
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Notify: failed to log notification for {Channel}.", channel);
            }
        }
    }
}
