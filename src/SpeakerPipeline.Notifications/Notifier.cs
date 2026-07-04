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
    ILogger<Notifier> logger) : INotifier
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

        if (!string.IsNullOrWhiteSpace(notification.DedupeKey))
        {
            var alreadySent = await api.GetNotificationsAsync(period, ct);
            if (alreadySent.Any(s => string.Equals(s.DedupeKey, notification.DedupeKey, StringComparison.Ordinal)))
            {
                logger.LogInformation("Notify skipped: dedupe key '{Key}' already sent in {Period}.", notification.DedupeKey, period);
                return;
            }
        }

        var delivered = new List<NotificationChannel>();
        foreach (var lane in enabled)
        {
            try
            {
                await lane.SendAsync(notification, ct);
                delivered.Add(lane.Channel);
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
            await api.LogNotificationAsync(new NotificationLogRecord
            {
                Period = period,
                NotificationId = Guid.NewGuid().ToString("n"),
                Channel = channel,
                Urgency = notification.Urgency,
                SentUtc = now,
                DedupeKey = notification.DedupeKey ?? string.Empty,
                EntityRef = notification.EntityRef,
                Summary = notification.Subject,
            }, ct);
        }
    }
}
