using Microsoft.Azure.Functions.Worker;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Hosting.Functions.Functions;

public sealed class NotificationRetryTimerTrigger(NotificationOutbox outbox, IEnumerable<INotificationLane> lanes)
{
    [Function(nameof(NotificationRetryTimerTrigger))]
    public Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
        => outbox.RetryAsync(lanes, ct);
}
