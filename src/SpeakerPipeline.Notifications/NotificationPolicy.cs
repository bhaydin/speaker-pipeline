namespace SpeakerPipeline.Notifications;

/// <summary>
/// Decides whether a run should send its notification. Manual runs always confirm
/// (you asked for it explicitly); scheduled runs can be configured to stay quiet
/// when they changed nothing.
/// </summary>
public static class NotificationPolicy
{
    public static bool ShouldNotify(int changeCount, bool isScheduled, bool suppressEmptyScheduledRuns)
        => changeCount > 0
           || !isScheduled
           || !suppressEmptyScheduledRuns;
}
