using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SpeakerPipeline.Core;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Notifications.Tests;

public class NotificationsTests
{
    // --- DiscoveryDigest -----------------------------------------------------

    [Fact]
    public void DiscoveryDigest_empty_returns_null()
        => Assert.Null(DiscoveryDigest.Build([]));

    [Fact]
    public void DiscoveryDigest_summarizes_new_and_updated()
    {
        var items = new[]
        {
            new DigestItem("Northwoods Tech Summit 2027", IsNew: true, "new"),
            new DigestItem("Great Lakes Cloud Conf 2027", IsNew: true, "new"),
            new DigestItem("Driftless AI Days 2027", IsNew: false, "CfpStatus Open->Closed"),
        };

        var n = DiscoveryDigest.Build(items);

        Assert.NotNull(n);
        Assert.Equal("Speaker pipeline — discovery: 2 new, 1 updated", n!.Subject);
        Assert.Contains("Northwoods Tech Summit 2027", n.HtmlBody);
        Assert.Contains("Driftless AI Days 2027", n.HtmlBody);
        Assert.Equal(NotificationUrgency.Digest, n.Urgency);
    }

    [Fact]
    public void DiscoveryDigest_html_encodes_titles()
    {
        var n = DiscoveryDigest.Build([new DigestItem("Dev & Ops <Summit>", IsNew: true, "new")]);
        Assert.Contains("Dev &amp; Ops &lt;Summit&gt;", n!.HtmlBody);
        Assert.DoesNotContain("<Summit>", n.HtmlBody);
    }

    // --- EmailLane payload ---------------------------------------------------

    [Fact]
    public void EmailLane_payload_carries_subject_html_body_and_recipient()
    {
        var n = new Notification { Subject = "Subj", HtmlBody = "<p>Hi</p>" };

        var recipient = "recipient@example.com";
        var json = EmailLane.BuildSendMailPayload(n, recipient);
        using var doc = JsonDocument.Parse(json);
        var message = doc.RootElement.GetProperty("message");

        Assert.Equal("Subj", message.GetProperty("subject").GetString());
        Assert.Equal("HTML", message.GetProperty("body").GetProperty("contentType").GetString());
        Assert.Equal("<p>Hi</p>", message.GetProperty("body").GetProperty("content").GetString());
        Assert.Equal(recipient,
            message.GetProperty("toRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.True(doc.RootElement.GetProperty("saveToSentItems").GetBoolean());
    }

    // --- Notifier ------------------------------------------------------------

    private static Notification Sample(string? dedupe = null) =>
        new() { Subject = "s", HtmlBody = "<p>b</p>", DedupeKey = dedupe };

    [Fact]
    public async Task Notifier_sends_via_enabled_lane_and_logs()
    {
        var lane = new FakeLane();
        var api = new FakeApi();
        var notifier = new Notifier([lane], api, NullLogger<Notifier>.Instance);

        await notifier.NotifyAsync(Sample());

        Assert.Single(lane.Sent);
        Assert.Single(api.Log);
        Assert.Equal(NotificationChannel.Email, api.Log[0].Channel);
    }

    [Fact]
    public async Task Notifier_with_no_enabled_lanes_does_nothing()
    {
        var lane = new FakeLane(enabled: false);
        var api = new FakeApi();
        var notifier = new Notifier([lane], api, NullLogger<Notifier>.Instance);

        await notifier.NotifyAsync(Sample());

        Assert.Empty(lane.Sent);
        Assert.Empty(api.Log);
    }

    [Fact]
    public async Task Notifier_skips_when_dedupe_key_already_sent_this_period()
    {
        var lane = new FakeLane();
        var api = new FakeApi();
        var period = DateTimeOffset.UtcNow.ToString("yyyy-MM");
        api.Log.Add(new NotificationLogRecord
        {
            Period = period, NotificationId = "1", Channel = NotificationChannel.Email,
            Urgency = NotificationUrgency.Urgent, SentUtc = DateTimeOffset.UtcNow, DedupeKey = "cfp-x-closing",
        });
        var notifier = new Notifier([lane], api, NullLogger<Notifier>.Instance);

        await notifier.NotifyAsync(Sample(dedupe: "cfp-x-closing"));

        Assert.Empty(lane.Sent);       // not re-sent
        Assert.Single(api.Log);        // no new log entry
    }

    [Fact]
    public async Task Notifier_does_not_log_when_all_lanes_fail()
    {
        var lane = new FakeLane(throws: true);
        var api = new FakeApi();
        var notifier = new Notifier([lane], api, NullLogger<Notifier>.Instance);

        await notifier.NotifyAsync(Sample()); // must not throw

        Assert.Empty(api.Log); // nothing recorded, so a retry can happen next run
    }

    // --- fakes ---------------------------------------------------------------

    private sealed class FakeLane(bool enabled = true, bool throws = false) : INotificationLane
    {
        public NotificationChannel Channel => NotificationChannel.Email;
        public bool IsEnabled => enabled;
        public List<Notification> Sent { get; } = [];

        public Task SendAsync(Notification notification, CancellationToken ct = default)
        {
            if (throws)
            {
                throw new InvalidOperationException("send failed");
            }
            Sent.Add(notification);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeApi : ISpeakerPipelineApiClient
    {
        public List<NotificationLogRecord> Log { get; } = [];

        public Task<IReadOnlyList<NotificationLogRecord>> GetNotificationsAsync(string period, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NotificationLogRecord>>([.. Log.Where(l => l.Period == period)]);

        public Task<NotificationLogRecord> LogNotificationAsync(NotificationLogRecord record, CancellationToken ct = default)
        {
            Log.Add(record);
            return Task.FromResult(record);
        }

        // Unused by the Notifier — stubbed.
        public Task<IReadOnlyList<EventRecord>> GetEventsAsync(IReadOnlyList<EventCategory>? categories = null, TimeSpan? deadlineWindow = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EventRecord>>([]);
        public Task<EventRecord?> GetEventAsync(string slug, CancellationToken ct = default) => Task.FromResult<EventRecord?>(null);
        public Task<EventRecord> UpsertEventAsync(EventRecord record, CancellationToken ct = default) => Task.FromResult(record);
        public Task<IReadOnlyList<SubmissionRecord>> GetSubmissionsForEventAsync(string eventSlug, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SubmissionRecord>>([]);
        public Task<SubmissionRecord> UpsertSubmissionAsync(SubmissionRecord record, CancellationToken ct = default) => Task.FromResult(record);
        public Task<IReadOnlyList<TalkRecord>> GetTalksAsync(Lane? lane = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TalkRecord>>([]);
        public Task<TalkRecord?> GetTalkAsync(string slug, CancellationToken ct = default) => Task.FromResult<TalkRecord?>(null);
        public Task<TalkRecord> UpsertTalkAsync(TalkRecord record, CancellationToken ct = default) => Task.FromResult(record);
        public Task<IReadOnlyList<EventRecord>> GetScoringCandidatesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EventRecord>>([]);
        public Task PostScoringDecisionAsync(ScoringDecision decision, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TopicRecord>> GetTopicsAsync(TopicStage? stage = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TopicRecord>>([]);
        public Task<TopicRecord?> GetTopicAsync(string topicId, CancellationToken ct = default) => Task.FromResult<TopicRecord?>(null);
        public Task<TopicRecord> UpsertTopicAsync(TopicRecord record, CancellationToken ct = default) => Task.FromResult(record);
        public Task<IReadOnlyList<BlackoutRecord>> GetBlackoutsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BlackoutRecord>>([]);
        public Task<BlackoutRecord> UpsertBlackoutAsync(BlackoutRecord record, CancellationToken ct = default) => Task.FromResult(record);
        public Task<EventRecord> ApplyPipelineActionAsync(string slug, PipelineActionRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
