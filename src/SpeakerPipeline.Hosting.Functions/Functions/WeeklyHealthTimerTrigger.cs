using Microsoft.Azure.Functions.Worker;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Hosting.Functions.Functions;

public sealed class WeeklyHealthTimerTrigger(PipelineHealth health, INotifier notifier, TimeProvider time)
{
    [Function(nameof(WeeklyHealthTimerTrigger))]
    public async Task Run([TimerTrigger("0 */15 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var local = TimeZoneInfo.ConvertTime(time.GetUtcNow(), PipelineHealth.Zone);
        if (local.DayOfWeek == DayOfWeek.Monday && local.Hour == 8)
            await notifier.NotifyAsync(await health.BuildAsync(ct), ct);
        _ = timer;
    }
}
