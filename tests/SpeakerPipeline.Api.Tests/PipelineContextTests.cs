using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpeakerPipeline.Api;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Tests;

public class PipelineContextTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // --- Assembler: effort derivation ---------------------------------------

    [Fact]
    public void Committed_effort_comes_from_the_submitted_talk_reusability()
    {
        var events = new[]
        {
            Event("accepted-reusable", EventCategory.Accepted, start: Now.AddDays(20)),
            Event("delivered-fresh", EventCategory.Delivered, start: Now.AddDays(-10)),
        };
        var subs = new Dictionary<string, IReadOnlyList<SubmissionRecord>>
        {
            ["accepted-reusable"] = [Submission("accepted-reusable", "reusable-deck")],
            ["delivered-fresh"] = [Submission("delivered-fresh", "fresh-build")],
        };
        var talks = new[] { Talk("reusable-deck", 5), Talk("fresh-build", 2) };

        var ctx = PipelineContextAssembler.Assemble(events, subs, talks, [], Now);

        Assert.Equal(EffortClass.DeckAdapt, ctx.Committed.Single(c => c.Slug == "accepted-reusable").Effort);
        Assert.Equal(EffortClass.NewTopic, ctx.Committed.Single(c => c.Slug == "delivered-fresh").Effort);
    }

    [Fact]
    public void Committed_effort_falls_back_to_focusfit_lane_talk_then_newtopic()
    {
        var withLane = Event("lane-only", EventCategory.Accepted, start: Now.AddDays(5)) with { FocusFit = [Lane.AgentOps] };
        var bare = Event("bare", EventCategory.Accepted, start: Now.AddDays(5));
        var talks = new[] { Talk("agentops-talk", 5, Lane.AgentOps) };

        var ctx = PipelineContextAssembler.Assemble([withLane, bare], NoSubs, talks, [], Now);

        Assert.Equal(EffortClass.DeckAdapt, ctx.Committed.Single(c => c.Slug == "lane-only").Effort); // lane talk is reusable
        Assert.Equal(EffortClass.NewTopic, ctx.Committed.Single(c => c.Slug == "bare").Effort);       // nothing to reuse
    }

    [Fact]
    public void Only_accepted_and_delivered_are_committed_engagements()
    {
        var events = new[]
        {
            Event("a", EventCategory.Accepted, start: Now),
            Event("d", EventCategory.Delivered, start: Now),
            Event("s", EventCategory.SubmitNow, start: Now),
            Event("m", EventCategory.Monitor, start: Now),
        };

        var ctx = PipelineContextAssembler.Assemble(events, NoSubs, [], [], Now);

        Assert.Equal(["a", "d"], ctx.Committed.Select(c => c.Slug).OrderBy(s => s));
    }

    // --- Assembler: prep counts ---------------------------------------------

    [Fact]
    public void Newtopic_prep_counts_split_this_month_and_next()
    {
        var events = new[]
        {
            Event("prep-this", EventCategory.SubmitNow, start: Now.AddDays(5)),          // Jan (this)
            Event("prep-next", EventCategory.Submitted, start: new(2027, 2, 10, 0, 0, 0, TimeSpan.Zero)), // Feb (next)
            Event("reusable-this", EventCategory.SubmitNow, start: Now.AddDays(6)),      // Jan but DeckAdapt -> not counted
        };
        var subs = new Dictionary<string, IReadOnlyList<SubmissionRecord>>
        {
            ["reusable-this"] = [Submission("reusable-this", "deck")],
        };
        var talks = new[] { Talk("deck", 5) };

        var ctx = PipelineContextAssembler.Assemble(events, subs, talks, [], Now);

        Assert.Equal(1, ctx.NewTopicPrepsThisMonth);  // prep-this only; reusable-this is DeckAdapt
        Assert.Equal(1, ctx.NewTopicPrepsNextMonth);   // prep-next
    }

    [Fact]
    public void Blackouts_are_mapped_and_sorted()
    {
        var blackouts = new[]
        {
            new BlackoutRecord { BlackoutId = "b2", StartDate = Now.AddDays(30), EndDate = Now.AddDays(35), Reason = "trip", Hardness = BlackoutHardness.Soft },
            new BlackoutRecord { BlackoutId = "b1", StartDate = Now.AddDays(5), EndDate = Now.AddDays(9), Reason = "family", Hardness = BlackoutHardness.Hard },
        };

        var ctx = PipelineContextAssembler.Assemble([], NoSubs, [], blackouts, Now);

        Assert.Equal(2, ctx.Blackouts.Count);
        Assert.Equal("family", ctx.Blackouts[0].Reason); // earliest first
        Assert.Equal(BlackoutHardness.Soft, ctx.Blackouts[1].Hardness);
    }

    // --- Endpoint (over the wire, isolated factory) -------------------------

    [Fact]
    public async Task Context_endpoint_assembles_committed_blackouts_and_effort()
    {
        using var factory = new TestWebApplicationFactory();
        await factory.Talks.UpsertAsync(Talk("agentops-real-world", 5, Lane.AgentOps));
        await factory.Events.UpsertAsync(Event("committed-conf", EventCategory.Accepted, start: Now.AddDays(20)) with { FocusFit = [Lane.AgentOps] });
        await factory.Events.UpsertAsync(Event("ignored-monitor", EventCategory.Monitor, start: Now));
        await factory.Blackouts.UpsertAsync(new BlackoutRecord
        {
            BlackoutId = "family-spring", StartDate = Now.AddDays(18), EndDate = Now.AddDays(25),
            Reason = "family", Hardness = BlackoutHardness.Hard,
        });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "dev-token");

        var resp = await client.GetAsync("/v1/pipeline/context");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var ctx = await resp.Content.ReadFromJsonAsync<PipelineContext>(Json);
        Assert.NotNull(ctx);
        var engagement = Assert.Single(ctx!.Committed);            // the Monitor event is excluded
        Assert.Equal("committed-conf", engagement.Slug);
        Assert.Equal(EffortClass.DeckAdapt, engagement.Effort);    // reusable AgentOps talk in the lane
        Assert.Equal("family", Assert.Single(ctx.Blackouts).Reason);
    }

    // --- helpers ------------------------------------------------------------

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<SubmissionRecord>> NoSubs =
        new Dictionary<string, IReadOnlyList<SubmissionRecord>>();

    private static EventRecord Event(string slug, EventCategory category, DateTimeOffset? start = null) => new()
    {
        Slug = slug,
        Name = slug,
        EventType = EventType.Conference,
        Category = category,
        Priority = Priority.Medium,
        EventDateStart = start,
    };

    private static TalkRecord Talk(string slug, int reusability, Lane lane = Lane.AgentOps) => new()
    {
        Slug = slug,
        CanonicalTitle = slug,
        Lane = lane,
        ReusabilityScore = reusability,
    };

    private static SubmissionRecord Submission(string eventSlug, string talkSlug) => new()
    {
        EventSlug = eventSlug,
        SubmissionId = $"{talkSlug}-2027-01-01",
        EventName = eventSlug,
        TalkSlug = talkSlug,
        TalkTitleUsed = talkSlug,
        AbstractUsed = "abstract",
        SubmittedOnUtc = Now.AddDays(-30),
        Status = SubmissionStatus.Accepted,
    };
}
