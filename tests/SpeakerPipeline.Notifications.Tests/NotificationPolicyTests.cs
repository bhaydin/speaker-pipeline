using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Notifications.Tests;

public class NotificationPolicyTests
{
    [Theory]
    // Healthy no-ops are quiet for manual and timer invocations.
    [InlineData(0, false, true, false)]
    [InlineData(0, false, false, false)]
    // Scheduled runs with changes always notify.
    [InlineData(3, true, true, true)]
    // Scheduled + empty: suppressed only when the setting is on.
    [InlineData(0, true, true, false)]
    [InlineData(0, true, false, false)]
    public void ShouldNotify_respects_manual_vs_scheduled_and_the_setting(
        int changeCount, bool isScheduled, bool suppressEmptyScheduledRuns, bool expected)
        => Assert.Equal(expected, NotificationPolicy.ShouldNotify(changeCount, isScheduled, suppressEmptyScheduledRuns));

    [Fact]
    public void Degraded_zero_change_runs_still_notify()
        => Assert.True(NotificationPolicy.ShouldNotify(0, isScheduled: true, suppressEmptyScheduledRuns: true, issueCount: 1));
}
