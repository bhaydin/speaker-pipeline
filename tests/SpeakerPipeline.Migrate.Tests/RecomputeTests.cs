using SpeakerPipeline.Core;
using SpeakerPipeline.Migrate;

namespace SpeakerPipeline.Migrate.Tests;

public sealed class RecomputeTests
{
    private static readonly DateTimeOffset Clock = DateTimeOffset.Parse("2027-03-01T12:00:00Z");
    private static readonly Uri Api = new("https://production.invalid/");

    [Fact]
    public void Removed_commitment_repairs_only_affected_flags_and_prompts()
    {
        var removed = Event("fictional", EventCategory.Accepted, Clock.AddDays(7));
        var committed = Event("real-commitment", EventCategory.Accepted, Clock.AddDays(9));
        var candidate = Event("nearby", EventCategory.Monitor, Clock.AddDays(8)) with { PrepConflictFlag = true };
        var far = Event("far", EventCategory.Monitor, Clock.AddDays(200));
        var before = Snapshot([removed, committed, candidate, far]);
        var after = Snapshot([committed, candidate, far]);
        var (report, repairs) = RecomputePreview.Build(before, after, Api, Clock, 28, 2);
        var change = Assert.Single(repairs.Plan.Changes);
        Assert.Equal("nearby", change.RowKey);
        Assert.Equal(MaintenanceChangeKind.SetProperties, change.Kind);
        Assert.Equal(new MaintenanceValue(MaintenanceValueType.Boolean, "false"), Assert.Single(change.PropertiesToSet).Value);
        Assert.DoesNotContain(report.ScoringChanges, s => s.EventSlug == "far");
        Assert.Contains(report.ScoringChanges, s => s.EventSlug == "nearby" && s.ReviewStatus == "requires-explicit-operator-review");
        Assert.Contains(report.ScoringChanges, s => s.EventSlug == "real-commitment" && s.ReviewStatus == "protected-do-not-rescore");
        Assert.Equal(2, report.BeforeContext.Committed.Count);
        Assert.Single(report.AfterContext.Committed);
        repairs.Validate(Api, after);
        Assert.Equal(report.Checksum, report.CalculateChecksum());
    }

    [Fact]
    public void Global_monthly_load_change_marks_even_undated_candidate_as_affected()
    {
        var removed = Event("fictional", EventCategory.SubmitNow, Clock.AddDays(10));
        var candidate = Event("undated", EventCategory.Monitor, null);
        var talk = Talk() with { ReusabilityScore = 1 };
        var (report, repairs) = RecomputePreview.Build(Snapshot([removed, candidate], talk), Snapshot([candidate], talk), Api, Clock, 28, 2);
        Assert.Equal(1, report.BeforeContext.NewTopicPrepsThisMonth);
        Assert.Equal(0, report.AfterContext.NewTopicPrepsThisMonth);
        Assert.Equal("undated", Assert.Single(report.ScoringChanges).EventSlug);
        Assert.Empty(repairs.Plan.Changes);
    }

    [Fact]
    public void Deliberate_and_ambiguous_decisions_are_preserved_without_scoring()
    {
        var removed = Event("fictional", EventCategory.Accepted, Clock.AddDays(7));
        var skip = Event("skip", EventCategory.Skip, Clock.AddDays(8)) with { DoNotResurface = true };
        var manual = Event("manual-intent", EventCategory.SubmitNow, Clock.AddDays(8)) with
        { StatusDetail = "Intent via Telegram", DecidedByAgent = "scoring-agent-v1", Priority = Priority.High };
        var submitted = Event("submitted", EventCategory.Monitor, Clock.AddDays(8));
        var submission = new SubmissionRecord
        {
            EventSlug = submitted.Slug, SubmissionId = "submitted-1", EventName = submitted.Name,
            TalkSlug = Talk().Slug, TalkTitleUsed = "Submitted title", AbstractUsed = "Abstract",
            SubmittedOnUtc = Clock, Status = SubmissionStatus.InReview,
        };
        var after = Snapshot([skip, manual, submitted], submissions: [submission]);
        var (report, repairs) = RecomputePreview.Build(Snapshot([removed, skip, manual, submitted], submissions: [submission]), after, Api, Clock, 28, 2);
        Assert.Contains(report.ScoringChanges, s => s.EventSlug == skip.Slug && s.ReviewStatus == "protected-do-not-rescore");
        Assert.Contains(report.ScoringChanges, s => s.EventSlug == submitted.Slug && s.ReviewStatus == "protected-do-not-rescore");
        Assert.Contains(report.ScoringChanges, s => s.EventSlug == manual.Slug && s.ReviewStatus == "requires-explicit-operator-review");
        Assert.Empty(repairs.Plan.Changes);
        Assert.Equal(after.Snapshot.Checksum, repairs.SnapshotChecksum);
    }

    [Fact]
    public void Existing_unrelated_flag_drift_is_review_only()
    {
        var candidate = Event("stale", EventCategory.Monitor, Clock.AddDays(8)) with { PrepConflictFlag = true };
        var snapshot = Snapshot([candidate]);
        var (report, repairs) = RecomputePreview.Build(snapshot, snapshot, Api, Clock, 28, 2);
        Assert.Equal("retain-review-preexisting-drift", Assert.Single(report.ConflictChanges).Disposition);
        Assert.Empty(repairs.Plan.Changes);
        Assert.Empty(report.ScoringChanges);
    }

    [Fact]
    public void Placeholder_deck_removal_does_not_trigger_rescoring()
    {
        var item = Event("candidate", EventCategory.Monitor, Clock.AddDays(8));
        var before = Snapshot([item], Talk() with { DeckUrl = "<placeholder-sharepoint-deck-url>" });
        var after = Snapshot([item], Talk() with { DeckUrl = null });
        var (report, repairs) = RecomputePreview.Build(before, after, Api, Clock, 28, 2);
        Assert.Empty(report.ScoringChanges);
        Assert.Empty(repairs.Plan.Changes);
    }

    [Fact]
    public void Recompute_rejects_changed_retained_decisions_and_added_records()
    {
        var item = Event("candidate", EventCategory.Monitor, Clock.AddDays(8));
        var before = Snapshot([item]);
        Assert.Throws<ArgumentException>(() => RecomputePreview.Build(before, Snapshot([item with { Category = EventCategory.SubmitNow }]), Api, Clock, 28, 2));
        Assert.Throws<ArgumentException>(() => RecomputePreview.Build(before, Snapshot([item, Event("added", EventCategory.Monitor, Clock)]), Api, Clock, 28, 2));
    }

    [Fact]
    public void Recompute_cli_requires_fixed_utc_clock_and_deployed_conflict_settings()
    {
        var args = new[] { "recompute", "--api", Api.AbsoluteUri, "--source", "before.json", "--snapshot", "after.json", "--output", "report.json", "--repairs-output", "repairs.json" };
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(args));
        var parsed = CliOptions.Parse([.. args, "--at", "2027-03-01T12:00:00Z", "--prep-window-days", "28", "--prep-threshold", "2"]);
        Assert.Equal(Clock, parsed.At);
        Assert.False(parsed.Apply);
        Assert.Null(parsed.Scope);
        Assert.Throws<ArgumentException>(() => CliOptions.Parse([.. args, "--at", "2027-03-01", "--prep-window-days", "28", "--prep-threshold", "2"]));
    }

    private static SnapshotFile Snapshot(EventRecord[] events, TalkRecord? talk = null, SubmissionRecord[]? submissions = null)
    {
        var tables = MaintenanceIntegrity.TableNames.ToDictionary(n => n, _ => new List<MaintenanceEntity>());
        tables["Events"] = events.Select(e => SampleCatalog.FromRecord("events", e.Slug, e, "Slug")).ToList();
        var canonical = talk ?? Talk();
        tables["Talks"] = [SampleCatalog.FromRecord("talks", canonical.Slug, canonical, "Slug")];
        tables["Submissions"] = (submissions ?? []).Select(s => SampleCatalog.FromRecord(s.EventSlug, s.SubmissionId, s, "EventSlug", "SubmissionId")).ToList();
        var snapshot = new MaintenanceSnapshot
        {
            SnapshotId = "test-snapshot", CreatedUtc = Clock, WritersFrozen = true, Tables = tables,
            Target = new() { TableEndpoint = "https://test.table.core.windows.net/", TableNames = MaintenanceIntegrity.TableNames.ToDictionary(n => n, n => n) },
        };
        return new(Api.AbsoluteUri, snapshot with { Checksum = MaintenanceIntegrity.SnapshotChecksum(snapshot) });
    }

    private static EventRecord Event(string slug, EventCategory category, DateTimeOffset? start) => new()
    {
        Slug = slug, Name = slug, Category = category, EventType = EventType.Conference, Priority = Priority.Medium,
        EventDateStart = start, FocusFit = [Lane.AgentOps],
    };
    private static TalkRecord Talk() => new() { Slug = "agentops-real-world", CanonicalTitle = "Canonical talk", Lane = Lane.AgentOps, ReusabilityScore = 5 };
}
