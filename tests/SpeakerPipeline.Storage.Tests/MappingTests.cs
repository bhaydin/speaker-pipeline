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
}
