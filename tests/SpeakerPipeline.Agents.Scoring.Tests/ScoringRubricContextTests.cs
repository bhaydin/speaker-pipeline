using SpeakerPipeline.Agents.Scoring;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Scoring.Tests;

/// <summary>
/// Covers the pipeline-context block in the scoring prompt: the ±8wk committed /
/// ±4wk blackout windowing relative to the candidate, and the undated fallback.
/// This is the new logic behind context-aware scoring (BUILD_PLAN.GO_FORWARD B1).
/// </summary>
public class ScoringRubricContextTests
{
    private static readonly DateTimeOffset Anchor = new(2027, 5, 20, 0, 0, 0, TimeSpan.Zero);

    private static readonly EventRecord Candidate = new()
    {
        Slug = "badgerland-agentops-forum-2027",
        Name = "Badgerland AgentOps Forum 2027",
        EventType = EventType.Conference,
        Category = EventCategory.Monitor,
        Priority = Priority.Medium,
        FocusFit = [Lane.AgentOps],
        EventDateStart = Anchor,
        CfpDeadline = Anchor.AddDays(-60),
    };

    private static PipelineContext Context(params CommittedEngagement[] committed) => new()
    {
        AsOfUtc = new(2027, 3, 1, 0, 0, 0, TimeSpan.Zero),
        Committed = committed,
        NewTopicPrepsThisMonth = 1,
        NewTopicPrepsNextMonth = 0,
    };

    [Fact]
    public void Empty_context_renders_none_for_committed_and_blackouts()
    {
        var prompt = ScoringRubric.BuildUserPrompt(Candidate, [], PipelineContext.Empty);

        Assert.Contains("Committed within ±8wk:  none", prompt);
        Assert.Contains("Blackouts within ±4wk:  none", prompt);
    }

    [Fact]
    public void Committed_engagement_inside_the_window_is_listed()
    {
        var ctx = Context(new CommittedEngagement("midwest-ai-summit", "Midwest AI Summit", Anchor.AddDays(5), Anchor.AddDays(7), EffortClass.NewTopic));

        var prompt = ScoringRubric.BuildUserPrompt(Candidate, [], ctx);

        Assert.Contains("midwest-ai-summit", prompt);
        Assert.Contains("effort=NewTopic", prompt);
    }

    [Fact]
    public void Committed_engagement_outside_8wk_is_windowed_out()
    {
        // 70 days after the anchor — beyond the ±8wk (56-day) window.
        var ctx = Context(new CommittedEngagement("far-off", "Far Off Conf", Anchor.AddDays(70), Anchor.AddDays(72), EffortClass.NewTopic));

        var prompt = ScoringRubric.BuildUserPrompt(Candidate, [], ctx);

        Assert.DoesNotContain("far-off", prompt);
        Assert.Contains("Committed within ±8wk:  none", prompt);
    }

    [Fact]
    public void Blackout_overlapping_the_4wk_window_is_listed()
    {
        var ctx = PipelineContext.Empty with
        {
            AsOfUtc = new(2027, 3, 1, 0, 0, 0, TimeSpan.Zero),
            Blackouts = [new BlackoutWindow(Anchor.AddDays(-3), Anchor.AddDays(3), "family", BlackoutHardness.Hard)],
        };

        var prompt = ScoringRubric.BuildUserPrompt(Candidate, [], ctx);

        Assert.Contains("family", prompt);
        Assert.Contains("Hard", prompt);
    }

    [Fact]
    public void Undated_candidate_reports_overlap_not_evaluated()
    {
        var undated = Candidate with { EventDateStart = null, CfpDeadline = null };

        var prompt = ScoringRubric.BuildUserPrompt(undated, [], Context());

        Assert.Contains("calendar overlap not evaluated", prompt);
    }

    [Fact]
    public void Prep_counts_are_always_shown()
    {
        var prompt = ScoringRubric.BuildUserPrompt(Candidate, [], Context());
        Assert.Contains("NewTopic preps in flight: 1 this month, 0 next", prompt);
    }
}
