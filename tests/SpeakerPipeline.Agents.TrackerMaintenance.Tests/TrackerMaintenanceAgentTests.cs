using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Agents.TrackerMaintenance;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.TrackerMaintenance.Tests;

public class TrackerMaintenanceAgentTests
{
    // --- DeriveCategory: the transition matrix -------------------------------

    [Theory]
    // Accepted wins over anything else present.
    [InlineData(EventCategory.SubmitNow, new[] { SubmissionStatus.Accepted }, EventCategory.Accepted)]
    [InlineData(EventCategory.Submitted, new[] { SubmissionStatus.Rejected, SubmissionStatus.Accepted }, EventCategory.Accepted)]
    [InlineData(EventCategory.Monitor, new[] { SubmissionStatus.InReview, SubmissionStatus.Accepted }, EventCategory.Accepted)]
    // In-flight → Submitted.
    [InlineData(EventCategory.SubmitNow, new[] { SubmissionStatus.Submitted }, EventCategory.Submitted)]
    [InlineData(EventCategory.Monitor, new[] { SubmissionStatus.InReview }, EventCategory.Submitted)]
    [InlineData(EventCategory.Outreach, new[] { SubmissionStatus.Rejected, SubmissionStatus.InReview }, EventCategory.Submitted)]
    // All terminal-negative → Pass.
    [InlineData(EventCategory.SubmitNow, new[] { SubmissionStatus.Rejected }, EventCategory.Pass)]
    [InlineData(EventCategory.Submitted, new[] { SubmissionStatus.Withdrawn }, EventCategory.Pass)]
    [InlineData(EventCategory.Monitor, new[] { SubmissionStatus.Rejected, SubmissionStatus.Withdrawn }, EventCategory.Pass)]
    public void DeriveCategory_maps_submission_status_to_category(
        EventCategory current, SubmissionStatus[] statuses, EventCategory expected)
        => Assert.Equal(expected, TrackerMaintenanceAgent.DeriveCategory(current, statuses));

    [Theory]
    [InlineData(EventCategory.SubmitNow)]
    [InlineData(EventCategory.Monitor)]
    [InlineData(EventCategory.Accepted)]
    public void DeriveCategory_no_submissions_returns_null(EventCategory current)
        => Assert.Null(TrackerMaintenanceAgent.DeriveCategory(current, []));

    [Theory]
    // Terminal/manual categories are never overridden, even with live submissions.
    [InlineData(EventCategory.Delivered, SubmissionStatus.Accepted)]
    [InlineData(EventCategory.Delivered, SubmissionStatus.Rejected)]
    [InlineData(EventCategory.Skip, SubmissionStatus.Accepted)]
    [InlineData(EventCategory.Skip, SubmissionStatus.Submitted)]
    public void DeriveCategory_terminal_categories_are_left_alone(EventCategory current, SubmissionStatus status)
        => Assert.Null(TrackerMaintenanceAgent.DeriveCategory(current, [status]));

    [Fact]
    public void DeriveCategory_is_idempotent_when_already_correct()
    {
        // Already Accepted with an Accepted submission → still Accepted (no churn).
        Assert.Equal(EventCategory.Accepted,
            TrackerMaintenanceAgent.DeriveCategory(EventCategory.Accepted, [SubmissionStatus.Accepted]));
    }

    // --- RunAsync: end-to-end over a fake API --------------------------------

    [Fact]
    public async Task RunAsync_updates_changed_events_and_is_idempotent()
    {
        var api = new FakeApiClient();
        api.Events["northwoods"] = Event("northwoods", EventCategory.SubmitNow);       // -> Submitted
        api.Events["great-lakes"] = Event("great-lakes", EventCategory.Submitted);     // -> Accepted
        api.Events["driftless"] = Event("driftless", EventCategory.Monitor);           // no subs, untouched
        api.Events["past-event"] = Event("past-event", EventCategory.Delivered);       // terminal, untouched

        api.Submissions["northwoods"] = [Sub("northwoods", SubmissionStatus.Submitted)];
        api.Submissions["great-lakes"] = [Sub("great-lakes", SubmissionStatus.Accepted)];
        api.Submissions["past-event"] = [Sub("past-event", SubmissionStatus.Accepted)];

        var agent = new TrackerMaintenanceAgent(api, Options.Create(new TrackerMaintenanceOptions()), NullLogger<TrackerMaintenanceAgent>.Instance);

        var first = await agent.RunAsync();

        Assert.Equal(2, first.Updates.Count);
        Assert.Equal(EventCategory.Submitted, api.Events["northwoods"].Category);
        Assert.Equal(EventCategory.Accepted, api.Events["great-lakes"].Category);
        Assert.Equal(EventCategory.Monitor, api.Events["driftless"].Category);   // no submissions → untouched
        Assert.Equal(EventCategory.Delivered, api.Events["past-event"].Category); // terminal → untouched

        api.Upserts = 0;
        var second = await agent.RunAsync();

        Assert.Empty(second.Updates);  // nothing changed
        Assert.Equal(0, api.Upserts);  // and nothing written
    }

    // --- FindUrgentDeadlines (B3) --------------------------------------------

    private static readonly DateTimeOffset Now = new(2027, 1, 15, 0, 0, 0, TimeSpan.Zero);

    private static EventRecord Deadlined(string slug, EventCategory category, DateTimeOffset? deadline, bool dnr = false) =>
        Event(slug, category) with { CfpDeadline = deadline, DoNotResurface = dnr };

    [Fact]
    public void FindUrgentDeadlines_flags_submitnow_within_the_window_only()
    {
        var events = new[]
        {
            Deadlined("urgent", EventCategory.SubmitNow, Now.AddDays(3)),        // in window
            Deadlined("on-edge", EventCategory.SubmitNow, Now.AddDays(7)),       // exactly at the cutoff
            Deadlined("far", EventCategory.SubmitNow, Now.AddDays(20)),          // beyond window
            Deadlined("past", EventCategory.SubmitNow, Now.AddDays(-1)),         // already closed
            Deadlined("monitor", EventCategory.Monitor, Now.AddDays(2)),         // not SubmitNow
            Deadlined("nodate", EventCategory.SubmitNow, null),                  // no deadline
            Deadlined("skipped", EventCategory.SubmitNow, Now.AddDays(2), dnr: true), // do-not-resurface
        };

        var urgent = TrackerMaintenanceAgent.FindUrgentDeadlines(events, Now, urgentDays: 7);

        Assert.Equal(["urgent", "on-edge"], urgent.Select(u => u.EventSlug)); // sorted by deadline
        Assert.Equal(3, urgent[0].DaysRemaining);
    }

    [Fact]
    public void FindUrgentDeadlines_is_empty_when_nothing_is_imminent()
    {
        var events = new[] { Deadlined("far", EventCategory.SubmitNow, Now.AddDays(30)) };

        Assert.Empty(TrackerMaintenanceAgent.FindUrgentDeadlines(events, Now, urgentDays: 7));
    }

    // --- ConflictEvaluator (C1) ----------------------------------------------

    private static EventRecord Dated(string slug, EventCategory category, DateTimeOffset start, DateTimeOffset? end = null) =>
        Event(slug, category) with { EventDateStart = start, EventDateEnd = end };

    private static BlackoutRecord Blackout(DateTimeOffset start, DateTimeOffset end, BlackoutHardness hardness) => new()
    {
        BlackoutId = "b", StartDate = start, EndDate = end, Reason = "family", Hardness = hardness,
    };

    [Fact]
    public void Evaluate_flags_family_when_event_overlaps_a_hard_blackout()
    {
        var ev = Dated("conf", EventCategory.SubmitNow, Now.AddDays(10), Now.AddDays(12));
        var blackouts = new[] { Blackout(Now.AddDays(11), Now.AddDays(14), BlackoutHardness.Hard) };

        var (family, prep) = ConflictEvaluator.Evaluate(ev, blackouts, [ev], prepWindowDays: 28, prepThreshold: 2);

        Assert.True(family);
        Assert.False(prep);
    }

    [Fact]
    public void Evaluate_ignores_soft_blackouts_and_non_overlapping_ranges()
    {
        var ev = Dated("conf", EventCategory.SubmitNow, Now.AddDays(10), Now.AddDays(12));
        var blackouts = new[]
        {
            Blackout(Now.AddDays(11), Now.AddDays(14), BlackoutHardness.Soft),  // soft: ignored
            Blackout(Now.AddDays(40), Now.AddDays(45), BlackoutHardness.Hard),  // no overlap
        };

        var (family, _) = ConflictEvaluator.Evaluate(ev, blackouts, [ev], 28, 2);

        Assert.False(family);
    }

    [Fact]
    public void Evaluate_flags_prep_when_enough_committed_engagements_are_nearby()
    {
        var ev = Dated("conf", EventCategory.SubmitNow, Now.AddDays(30));
        var all = new[]
        {
            ev,
            Dated("committed-a", EventCategory.Accepted, Now.AddDays(20)),   // within ±28d
            Dated("committed-b", EventCategory.Delivered, Now.AddDays(50)),  // within ±28d
            Dated("committed-far", EventCategory.Accepted, Now.AddDays(90)), // outside window
            Dated("monitor-near", EventCategory.Monitor, Now.AddDays(28)),   // not committed
        };

        var (_, prep) = ConflictEvaluator.Evaluate(ev, [], all, prepWindowDays: 28, prepThreshold: 2);

        Assert.True(prep); // committed-a + committed-b = 2
    }

    [Fact]
    public void Evaluate_below_threshold_does_not_flag_prep()
    {
        var ev = Dated("conf", EventCategory.SubmitNow, Now.AddDays(30));
        var all = new[] { ev, Dated("committed-a", EventCategory.Accepted, Now.AddDays(20)) };

        var (_, prep) = ConflictEvaluator.Evaluate(ev, [], all, 28, 2);

        Assert.False(prep); // only 1 nearby
    }

    [Fact]
    public void Evaluate_undated_event_has_no_conflicts()
    {
        var ev = Event("no-date", EventCategory.SubmitNow); // no EventDateStart
        var blackouts = new[] { Blackout(Now, Now.AddDays(100), BlackoutHardness.Hard) };

        var (family, prep) = ConflictEvaluator.Evaluate(ev, blackouts, [ev], 28, 2);

        Assert.False(family);
        Assert.False(prep);
    }

    // --- RunAsync: conflict flag persistence ---------------------------------

    [Fact]
    public async Task RunAsync_persists_conflict_flags_and_is_idempotent()
    {
        var api = new FakeApiClient();
        api.Events["conf"] = Dated("conf", EventCategory.SubmitNow, Now.AddDays(10), Now.AddDays(12));
        api.Blackouts.Add(Blackout(Now.AddDays(11), Now.AddDays(13), BlackoutHardness.Hard));

        var agent = new TrackerMaintenanceAgent(api, Options.Create(new TrackerMaintenanceOptions()), NullLogger<TrackerMaintenanceAgent>.Instance);

        var first = await agent.RunAsync();

        var change = Assert.Single(first.ConflictChanges);
        Assert.Equal("conf", change.EventSlug);
        Assert.True(change.Family);
        Assert.True(api.Events["conf"].FamilyConflictFlag); // persisted

        api.Upserts = 0;
        var second = await agent.RunAsync();

        Assert.Empty(second.ConflictChanges); // flag already set → no re-write
        Assert.Equal(0, api.Upserts);
    }

    // --- helpers -------------------------------------------------------------

    private static EventRecord Event(string slug, EventCategory category) => new()
    {
        Slug = slug,
        Name = slug,
        EventType = EventType.Conference,
        Category = category,
        Priority = Priority.Medium,
    };

    private static SubmissionRecord Sub(string eventSlug, SubmissionStatus status) => new()
    {
        EventSlug = eventSlug,
        SubmissionId = $"{eventSlug}-sub",
        EventName = eventSlug,
        TalkSlug = "some-talk",
        TalkTitleUsed = "Some Talk",
        AbstractUsed = "An abstract.",
        SubmittedOnUtc = DateTimeOffset.UnixEpoch,
        Status = status,
    };

    private sealed class FakeApiClient : ISpeakerPipelineApiClient
    {
        public Dictionary<string, EventRecord> Events { get; } = new();
        public Dictionary<string, List<SubmissionRecord>> Submissions { get; } = new();
        public List<BlackoutRecord> Blackouts { get; } = [];
        public int Upserts { get; set; }

        public Task<IReadOnlyList<EventRecord>> GetEventsAsync(IReadOnlyList<EventCategory>? categories = null, TimeSpan? deadlineWindow = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EventRecord>>([.. Events.Values]);

        public Task<IReadOnlyList<SubmissionRecord>> GetSubmissionsForEventAsync(string eventSlug, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SubmissionRecord>>(
                Submissions.TryGetValue(eventSlug, out var subs) ? subs : []);

        public Task<EventRecord> UpsertEventAsync(EventRecord record, CancellationToken ct = default)
        {
            Events[record.Slug] = record;
            Upserts++;
            return Task.FromResult(record);
        }

        // Unused by the tracker — stubbed.
        public Task<EventRecord?> GetEventAsync(string slug, CancellationToken ct = default) => Task.FromResult<EventRecord?>(Events.GetValueOrDefault(slug));
        public Task<SubmissionRecord> UpsertSubmissionAsync(SubmissionRecord record, CancellationToken ct = default) => Task.FromResult(record);
        public Task<IReadOnlyList<TalkRecord>> GetTalksAsync(Lane? lane = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TalkRecord>>([]);
        public Task<TalkRecord?> GetTalkAsync(string slug, CancellationToken ct = default) => Task.FromResult<TalkRecord?>(null);
        public Task<TalkRecord> UpsertTalkAsync(TalkRecord record, CancellationToken ct = default) => Task.FromResult(record);
        public Task<IReadOnlyList<EventRecord>> GetScoringCandidatesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EventRecord>>([]);
        public Task PostScoringDecisionAsync(ScoringDecision decision, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PipelineContext> GetPipelineContextAsync(CancellationToken ct = default) => Task.FromResult(PipelineContext.Empty);
        public Task<IReadOnlyList<TopicRecord>> GetTopicsAsync(TopicStage? stage = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TopicRecord>>([]);
        public Task<TopicRecord?> GetTopicAsync(string topicId, CancellationToken ct = default) => Task.FromResult<TopicRecord?>(null);
        public Task<TopicRecord> UpsertTopicAsync(TopicRecord record, CancellationToken ct = default) => Task.FromResult(record);
        public Task<IReadOnlyList<BlackoutRecord>> GetBlackoutsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BlackoutRecord>>([.. Blackouts]);
        public Task<BlackoutRecord> UpsertBlackoutAsync(BlackoutRecord record, CancellationToken ct = default) => Task.FromResult(record);
        public Task<EventRecord> ApplyPipelineActionAsync(string slug, PipelineActionRequest request, CancellationToken ct = default) => Task.FromResult(Events[slug]);
        public Task<IReadOnlyList<NotificationLogRecord>> GetNotificationsAsync(string period, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NotificationLogRecord>>([]);
        public Task<NotificationLogRecord> LogNotificationAsync(NotificationLogRecord record, CancellationToken ct = default) => Task.FromResult(record);
    }
}
