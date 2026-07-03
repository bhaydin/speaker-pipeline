using SpeakerPipeline.Core;
using SpeakerPipeline.Storage;

namespace SpeakerPipeline.Storage.Tests;

public class MappingTests
{
    [Fact]
    public void EventRecord_round_trips_through_TableEntity()
    {
        var original = new EventRecord
        {
            Slug = "northwoods-tech-summit-2027",
            Name = "Northwoods Tech Summit 2027",
            EventType = EventType.Conference,
            Category = EventCategory.SubmitNow,
            Priority = Priority.High,
            FocusFit = [Lane.AgentOps, Lane.HybridAgents],
            StatusDetail = "CFP open",
            EventDateStart = new DateTimeOffset(2027, 5, 4, 0, 0, 0, TimeSpan.Zero),
            EventDateEnd = new DateTimeOffset(2027, 5, 6, 0, 0, 0, TimeSpan.Zero),
            CfpDeadline = new DateTimeOffset(2027, 2, 15, 23, 59, 0, TimeSpan.Zero),
            CfpUrl = "https://sessionize.example.org/x",
            EventUrl = "https://example.org/",
            Location = "Minocqua, WI",
            Format = EventFormat.InPerson,
            TravelBurden = TravelBurden.Low,
            NextAction = "Submit before deadline",
            SourceSeenOn = SourceSeenOn.Sessionize,
            LastVerifiedUtc = new DateTimeOffset(2026, 5, 24, 0, 0, 0, TimeSpan.Zero),
            DoNotResurface = false,
            DiscoveredByAgent = "sessionize-scanner",
            DecidedByAgent = "scoring-agent-v1",
            SchemaVersion = 1,
        };

        var entity = Mapping.ToEntity(original);
        var roundTripped = Mapping.ToEventRecord(entity);

        Assert.Equal(original.Slug, roundTripped.Slug);
        Assert.Equal(original.Name, roundTripped.Name);
        Assert.Equal(original.EventType, roundTripped.EventType);
        Assert.Equal(original.Category, roundTripped.Category);
        Assert.Equal(original.Priority, roundTripped.Priority);
        Assert.Equal(original.FocusFit, roundTripped.FocusFit);
        Assert.Equal(original.Format, roundTripped.Format);
        Assert.Equal(original.TravelBurden, roundTripped.TravelBurden);
        Assert.Equal(original.SourceSeenOn, roundTripped.SourceSeenOn);
        Assert.Equal(original.SchemaVersion, roundTripped.SchemaVersion);
    }

    [Fact]
    public void TalkRecord_round_trips()
    {
        var original = new TalkRecord
        {
            Slug = "agentops-real-world",
            CanonicalTitle = "AgentOps in the Real World",
            Lane = Lane.AgentOps,
            AbstractShort = "short",
            LengthMinutes = 60,
            Format = TalkFormat.Talk,
            DeliveryCount = 3,
            ReusabilityScore = 5,
            Retired = false,
        };

        var entity = Mapping.ToEntity(original);
        var roundTripped = Mapping.ToTalkRecord(entity);

        Assert.Equal(original.Slug, roundTripped.Slug);
        Assert.Equal(original.CanonicalTitle, roundTripped.CanonicalTitle);
        Assert.Equal(original.Lane, roundTripped.Lane);
        Assert.Equal(original.Format, roundTripped.Format);
        Assert.Equal(original.DeliveryCount, roundTripped.DeliveryCount);
        Assert.Equal(original.ReusabilityScore, roundTripped.ReusabilityScore);
    }

    [Fact]
    public void SubmissionRecord_round_trips()
    {
        var original = new SubmissionRecord
        {
            EventSlug = "great-lakes-cloud-conf-2027",
            SubmissionId = "agentops-real-world-2026-11-12",
            EventName = "Great Lakes Cloud Conf 2027",
            TalkSlug = "agentops-real-world",
            TalkTitleUsed = "AgentOps in the Real World",
            AbstractUsed = "abstract",
            SubmittedOnUtc = new DateTimeOffset(2026, 11, 12, 14, 5, 0, TimeSpan.Zero),
            Status = SubmissionStatus.Accepted,
            DecisionReceivedUtc = new DateTimeOffset(2026, 12, 18, 16, 20, 0, TimeSpan.Zero),
        };

        var entity = Mapping.ToEntity(original);
        var roundTripped = Mapping.ToSubmissionRecord(entity);

        Assert.Equal(original.EventSlug, roundTripped.EventSlug);
        Assert.Equal(original.SubmissionId, roundTripped.SubmissionId);
        Assert.Equal(original.Status, roundTripped.Status);
        Assert.Equal(original.SubmittedOnUtc, roundTripped.SubmittedOnUtc);
    }

    [Fact]
    public void ToEntity_rejects_slug_with_forbidden_characters()
    {
        var bad = new TalkRecord
        {
            Slug = "has/slash",
            CanonicalTitle = "x",
            Lane = Lane.AgentOps,
        };

        Assert.Throws<ArgumentException>(() => Mapping.ToEntity(bad));
    }

    [Fact]
    public void TopicRecord_round_trips()
    {
        var original = new TopicRecord
        {
            TopicId = "model-access-supply-chain-risk",
            Title = "Model-access supply-chain risk",
            OneLiner = "Who can reach your model, and what they can do once they're there.",
            Stage = TopicStage.Validated,
            Source = TopicSource.Claude,
            Lane = Lane.AgentOps,
            EffortClass = EffortClass.NewTopic,
            Notes = "Surfaced mid-conversation.",
            RelatedContentUrls = ["https://example.org/a", "https://example.org/b"],
            CreatedUtc = new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero),
            UpdatedUtc = new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero),
        };

        var entity = Mapping.ToEntity(original);
        var roundTripped = Mapping.ToTopicRecord(entity);

        Assert.Equal(original.TopicId, roundTripped.TopicId);
        Assert.Equal(original.Title, roundTripped.Title);
        Assert.Equal(original.OneLiner, roundTripped.OneLiner);
        Assert.Equal(original.Stage, roundTripped.Stage);
        Assert.Equal(original.Source, roundTripped.Source);
        Assert.Equal(original.Lane, roundTripped.Lane);
        Assert.Equal(original.EffortClass, roundTripped.EffortClass);
        Assert.Equal(original.RelatedContentUrls, roundTripped.RelatedContentUrls);
        Assert.Equal(original.CreatedUtc, roundTripped.CreatedUtc);
    }

    [Fact]
    public void TopicRecord_round_trips_with_minimal_fields()
    {
        var original = new TopicRecord
        {
            TopicId = "bare-idea",
            Title = "A bare idea",
            Stage = TopicStage.Idea,
            Source = TopicSource.Manual,
        };

        var entity = Mapping.ToEntity(original);
        var roundTripped = Mapping.ToTopicRecord(entity);

        Assert.Null(roundTripped.Lane);
        Assert.Null(roundTripped.EffortClass);
        Assert.Empty(roundTripped.RelatedContentUrls);
        Assert.Equal(TopicStage.Idea, roundTripped.Stage);
    }

    [Fact]
    public void BlackoutRecord_round_trips()
    {
        var original = new BlackoutRecord
        {
            BlackoutId = "family-2027-07",
            StartDate = new DateTimeOffset(2027, 7, 10, 0, 0, 0, TimeSpan.Zero),
            EndDate = new DateTimeOffset(2027, 7, 20, 0, 0, 0, TimeSpan.Zero),
            Reason = "Family salmon trip",
            Hardness = BlackoutHardness.Hard,
            Source = "manual",
            CreatedUtc = new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero),
            UpdatedUtc = new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero),
        };

        var entity = Mapping.ToEntity(original);
        var roundTripped = Mapping.ToBlackoutRecord(entity);

        Assert.Equal(original.BlackoutId, roundTripped.BlackoutId);
        Assert.Equal(original.StartDate, roundTripped.StartDate);
        Assert.Equal(original.EndDate, roundTripped.EndDate);
        Assert.Equal(original.Reason, roundTripped.Reason);
        Assert.Equal(original.Hardness, roundTripped.Hardness);
        Assert.Equal(original.Source, roundTripped.Source);
    }

    [Fact]
    public void NotificationLogRecord_round_trips()
    {
        var original = new NotificationLogRecord
        {
            Period = "2026-07",
            NotificationId = "great-lakes-cloud-conf-2027-cfp-closing",
            Channel = NotificationChannel.Telegram,
            Urgency = NotificationUrgency.Urgent,
            SentUtc = new DateTimeOffset(2026, 7, 3, 8, 30, 0, TimeSpan.Zero),
            DedupeKey = "great-lakes-cloud-conf-2027|cfp-closing",
            EntityRef = "great-lakes-cloud-conf-2027",
            Summary = "CFP closes in 5 days",
        };

        var entity = Mapping.ToEntity(original);
        var roundTripped = Mapping.ToNotificationLogRecord(entity);

        Assert.Equal(original.Period, roundTripped.Period);
        Assert.Equal(original.NotificationId, roundTripped.NotificationId);
        Assert.Equal(original.Channel, roundTripped.Channel);
        Assert.Equal(original.Urgency, roundTripped.Urgency);
        Assert.Equal(original.SentUtc, roundTripped.SentUtc);
        Assert.Equal(original.DedupeKey, roundTripped.DedupeKey);
        Assert.Equal(original.EntityRef, roundTripped.EntityRef);
    }
}
