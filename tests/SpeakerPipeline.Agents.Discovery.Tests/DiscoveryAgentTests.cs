using SpeakerPipeline.Agents.Discovery;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Discovery.Tests;

public class DiscoveryAgentTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private const string Agent = "discovery-agent-v1";

    // --- Reconcile: new events ----------------------------------------------

    [Fact]
    public void Reconcile_new_event_enters_as_monitor_candidate()
    {
        var extracted = new ExtractedEvent
        {
            IsEvent = true,
            EventName = "Northwoods Tech Summit 2027",
            EventType = EventType.Conference,
            Location = "Minocqua, WI",
            Format = EventFormat.InPerson,
            CfpStatus = CfpStatus.Open,
            CfpDeadline = Now.AddDays(30),
            Confidence = 9,
        };

        var (upsert, summary, isNew) = DiscoveryAgent.Reconcile(
            existing: null, extracted, "northwoods-tech-summit-2027", SourceSeenOn.Sessionize, Now, Agent);

        Assert.True(isNew);
        Assert.Equal("new", summary);
        Assert.NotNull(upsert);
        Assert.Equal(EventCategory.Monitor, upsert!.Category);          // enters the scoring pool
        Assert.Equal(CfpStatus.Open, upsert.CfpStatus);
        Assert.Equal(SourceSeenOn.Sessionize, upsert.SourceSeenOn);
        Assert.Equal(Agent, upsert.DiscoveredByAgent);
        Assert.Equal(Now, upsert.LastVerifiedUtc);
    }

    // --- Reconcile: existing events -----------------------------------------

    [Fact]
    public void Reconcile_do_not_resurface_is_skipped()
    {
        var existing = Event("x", EventCategory.Pass) with { DoNotResurface = true };
        var extracted = new ExtractedEvent { IsEvent = true, EventName = "X", CfpStatus = CfpStatus.Open, Confidence = 9 };

        var (upsert, summary, _) = DiscoveryAgent.Reconcile(existing, extracted, "x", SourceSeenOn.Sessionize, Now, Agent);

        Assert.Null(upsert);
        Assert.Contains("do-not-resurface", summary);
    }

    [Fact]
    public void Reconcile_no_new_information_is_idempotent()
    {
        var existing = Event("x", EventCategory.Monitor) with { CfpStatus = CfpStatus.Open, Location = "Duluth" };
        // Extraction with no fresh signal (Unknown status, null location).
        var extracted = new ExtractedEvent { IsEvent = true, EventName = "X", CfpStatus = CfpStatus.Unknown, Location = null, Confidence = 8 };

        var (upsert, summary, _) = DiscoveryAgent.Reconcile(existing, extracted, "x", SourceSeenOn.Sessionize, Now, Agent);

        Assert.Null(upsert);
        Assert.Equal("no change", summary);
    }

    [Fact]
    public void Reconcile_cfp_status_change_updates_without_touching_category()
    {
        var existing = Event("x", EventCategory.SubmitNow) with { CfpStatus = CfpStatus.Open };
        var extracted = new ExtractedEvent { IsEvent = true, EventName = "X", CfpStatus = CfpStatus.Closed, Confidence = 9 };

        var (upsert, summary, isNew) = DiscoveryAgent.Reconcile(existing, extracted, "x", SourceSeenOn.Sessionize, Now, Agent);

        Assert.False(isNew);
        Assert.NotNull(upsert);
        Assert.Equal(CfpStatus.Closed, upsert!.CfpStatus);
        Assert.Equal(EventCategory.SubmitNow, upsert.Category);   // scoring's decision preserved
        Assert.Contains("CfpStatus", summary);
    }

    [Fact]
    public void Reconcile_unknown_extraction_never_overwrites_known_values()
    {
        var existing = Event("x", EventCategory.Monitor) with { CfpStatus = CfpStatus.Open, Location = "Duluth" };
        var extracted = new ExtractedEvent { IsEvent = true, EventName = "X", CfpStatus = CfpStatus.Unknown, Location = null, Confidence = 8 };

        var (upsert, _, _) = DiscoveryAgent.Reconcile(existing, extracted, "x", SourceSeenOn.Sessionize, Now, Agent);

        Assert.Null(upsert); // nothing to write — the known Open/Duluth values stand
    }

    // --- Slugify -------------------------------------------------------------

    [Theory]
    [InlineData("Northwoods Tech Summit 2027", "northwoods-tech-summit-2027")]
    [InlineData("  Great Lakes Cloud Conf  ", "great-lakes-cloud-conf")]
    [InlineData("AI & Agents!", "ai-agents")]
    [InlineData("Driftless.AI.Days", "driftless-ai-days")]
    public void Slugify_produces_stable_kebab_slugs(string name, string expected)
        => Assert.Equal(expected, DiscoveryAgent.Slugify(name));

    // --- ParseExtracted ------------------------------------------------------

    [Fact]
    public void ParseExtracted_reads_valid_json()
    {
        var e = DiscoveryAgent.ParseExtracted("""
            {"isEvent":true,"eventName":"X","cfpStatus":"Open","confidence":7}
            """);
        Assert.True(e.IsEvent);
        Assert.Equal(CfpStatus.Open, e.CfpStatus);
    }

    [Fact]
    public void ParseExtracted_tolerates_json_fences()
    {
        var e = DiscoveryAgent.ParseExtracted("```json\n{\"isEvent\":true,\"eventName\":\"X\"}\n```");
        Assert.True(e.IsEvent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("not json at all")]
    [InlineData("{\"isEvent\": tru")]
    public void ParseExtracted_returns_non_event_on_unusable_output(string content)
        => Assert.False(DiscoveryAgent.ParseExtracted(content).IsEvent);

    // --- HTML normalization --------------------------------------------------

    [Fact]
    public void Normalize_strips_scripts_tags_and_decodes_entities()
    {
        var html = "<html><head><style>.x{}</style></head><body><script>evil()</script>" +
                   "<h1>Contoso&nbsp;Conf</h1><p>Submit &amp; win</p></body></html>";

        var text = WatchlistSource.Normalize(html);

        Assert.DoesNotContain("evil()", text);
        Assert.DoesNotContain("<", text);
        Assert.Contains("Contoso", text);
        Assert.Contains("Submit & win", text);
    }

    private static EventRecord Event(string slug, EventCategory category) => new()
    {
        Slug = slug,
        Name = slug,
        EventType = EventType.Conference,
        Category = category,
        Priority = Priority.Medium,
    };
}
