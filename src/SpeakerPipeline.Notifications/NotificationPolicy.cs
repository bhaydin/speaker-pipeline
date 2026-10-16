namespace SpeakerPipeline.Notifications;

/// <summary>
/// Healthy no-op discovery and tracker runs remain quiet, including manual runs.
/// Errors and anomalies must remain visible independently of write counts.
/// </summary>
public static class NotificationPolicy
{
    public static bool ShouldNotify(int changeCount, bool isScheduled, bool suppressEmptyScheduledRuns, int issueCount = 0)
        => changeCount > 0 || issueCount > 0;
}
