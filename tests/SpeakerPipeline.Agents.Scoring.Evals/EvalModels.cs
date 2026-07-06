using System.Text.Json.Serialization;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Scoring.Evals;

internal sealed record GoldenSet
{
    public int SchemaVersion { get; init; } = 1;
    public string RubricVersion { get; init; } = "v2";
    public List<GoldenCase> Cases { get; init; } = [];
}

internal sealed record GoldenCase
{
    public required string Id { get; init; }
    public string? Description { get; init; }
    public required EventInput Event { get; init; }

    /// <summary>Optional calendar/load context. Absent ⇒ scored with an empty context (no known conflicts).</summary>
    public PipelineContextInput? Context { get; init; }

    public required Recommendation ExpectedRecommendation { get; init; }
    public List<Recommendation> ExpectedRecommendationAlternates { get; init; } = [];
    public string? ExpectedTalkSlug { get; init; }
    public List<string> RationaleKeywords { get; init; } = [];
}

/// <summary>Golden-file shape for a <see cref="PipelineContext"/> — only the fields the rubric consumes.</summary>
internal sealed record PipelineContextInput
{
    public DateTimeOffset AsOfUtc { get; init; }
    public List<CommittedEngagementInput> Committed { get; init; } = [];
    public List<BlackoutInput> Blackouts { get; init; } = [];
    public int NewTopicPrepsThisMonth { get; init; }
    public int NewTopicPrepsNextMonth { get; init; }

    public PipelineContext ToPipelineContext() => new()
    {
        AsOfUtc = AsOfUtc,
        Committed = [.. Committed.Select(c => new CommittedEngagement(c.Slug, c.Name, c.Start, c.End, c.Effort))],
        Blackouts = [.. Blackouts.Select(b => new BlackoutWindow(b.Start, b.End, b.Reason, b.Hardness))],
        NewTopicPrepsThisMonth = NewTopicPrepsThisMonth,
        NewTopicPrepsNextMonth = NewTopicPrepsNextMonth,
    };
}

internal sealed record CommittedEngagementInput
{
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public DateTimeOffset? Start { get; init; }
    public DateTimeOffset? End { get; init; }
    public EffortClass Effort { get; init; } = EffortClass.NewTopic;
}

internal sealed record BlackoutInput
{
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public string Reason { get; init; } = string.Empty;
    public BlackoutHardness Hardness { get; init; } = BlackoutHardness.Hard;
}

/// <summary>
/// Trimmed EventRecord shape for the golden file — only the fields the
/// rubric actually consumes. Keeps the JSON small and reviewer-friendly.
/// </summary>
internal sealed record EventInput
{
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required EventType EventType { get; init; }
    public required EventCategory Category { get; init; }
    public required Priority Priority { get; init; }
    public List<Lane> FocusFit { get; init; } = [];
    public DateTimeOffset? EventDateStart { get; init; }
    public DateTimeOffset? CfpDeadline { get; init; }
    public string? Location { get; init; }
    public EventFormat? Format { get; init; }
    public TravelBurden? TravelBurden { get; init; }
    public string? Notes { get; init; }

    public EventRecord ToEventRecord() => new()
    {
        Slug = Slug,
        Name = Name,
        EventType = EventType,
        Category = Category,
        Priority = Priority,
        FocusFit = FocusFit,
        EventDateStart = EventDateStart,
        CfpDeadline = CfpDeadline,
        Location = Location,
        Format = Format,
        TravelBurden = TravelBurden,
        Notes = Notes,
    };
}

internal sealed record EvalCaseReport
{
    public required string CaseId { get; init; }
    public required bool Passed { get; init; }
    public required Recommendation Expected { get; init; }
    public required Recommendation Actual { get; init; }
    public required int FitScore { get; init; }
    public required int EffortScore { get; init; }
    public required int ConfidenceScore { get; init; }
    public required string Rationale { get; init; }
    public List<string> MissingKeywords { get; init; } = [];
    public string? FailureReason { get; init; }
}

internal sealed record EvalReport
{
    public required string RunId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset CompletedAtUtc { get; init; }
    public required string RubricVersion { get; init; }
    public required int TotalCases { get; init; }
    public required int Passed { get; init; }
    public required int Failed { get; init; }
    public required List<EvalCaseReport> Cases { get; init; }

    [JsonIgnore]
    public bool AnyFailure => Failed > 0;
}
