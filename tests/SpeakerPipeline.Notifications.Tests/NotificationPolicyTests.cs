using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Notifications.Tests;

public class NotificationPolicyTests
{
    [Theory]
    // Manual runs always notify, empty or not, regardless of the setting.
    [InlineData(0, false, true, true)]
    [InlineData(0, false, false, true)]
    // Scheduled runs with changes always notify.
    [InlineData(3, true, true, true)]
    // Scheduled + empty: suppressed only when the setting is on.
    [InlineData(0, true, true, false)]
    [InlineData(0, true, false, true)]
    public void ShouldNotify_respects_manual_vs_scheduled_and_the_setting(
        int changeCount, bool isScheduled, bool suppressEmptyScheduledRuns, bool expected)
        => Assert.Equal(expected, NotificationPolicy.ShouldNotify(changeCount, isScheduled, suppressEmptyScheduledRuns));

    [Fact]
    public void Default_setting_false_means_empty_scheduled_runs_still_notify()
        => Assert.True(NotificationPolicy.ShouldNotify(0, isScheduled: true, suppressEmptyScheduledRuns: false));
}
