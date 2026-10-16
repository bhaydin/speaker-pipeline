namespace SpeakerPipeline.Core;

/// <summary>
/// The scoring agent's output for a single event.
/// FitScore, EffortScore, and ConfidenceScore are each on a 1–10 scale.
/// </summary>
public sealed record ScoringDecision
{
    public required string EventSlug { get; init; }
    public required Recommendation Recommendation { get; init; }
    public required string Rationale { get; init; }
    public required int FitScore { get; init; }
    public required int EffortScore { get; init; }
    public required int ConfidenceScore { get; init; }

    public string? RecommendedTalkSlug { get; init; }
    public string? InputRevision { get; init; }
    /// <summary>Hash of the substantive inputs actually supplied to the scorer. Unchanged inputs reuse the prior decision.</summary>
    public string? InputFingerprint { get; init; }
    public string? EventFactsRevision { get; init; }
    public string? CalendarRevision { get; init; }
    public string? CatalogContextRevision { get; init; }
    public string? FeedbackRevision { get; init; }
    public string? ContextRevision { get; init; }
    public string? ModelName { get; init; }
    public string? ModelProvider { get; init; }
    public string? ModelRevision { get; init; }
    public string? CatalogGenerationId { get; init; }
    public string RubricVersion { get; init; } = "legacy-unknown";
    public string? FitRationale { get; init; }
    /// <summary>Grounded explanation of a verdict flip compared with the prior scored decision.</summary>
    public string? ChangeReason { get; init; }
    /// <summary>Effort 1 is trivial reuse; effort 10 is a substantial new build. Higher = more work.</summary>
    public string? EffortRationale { get; init; }
    public string? GeographyRationale { get; init; }
    public IReadOnlyList<TalkMatch> MatchedTalks { get; init; } = [];
    public IReadOnlyList<string> FeedbackIds { get; init; } = [];
    public long? InputTokens { get; init; }
    /// <summary>Provider completion tokens, including reasoning tokens where reported.</summary>
    public long? OutputTokens { get; init; }
    public long? ReasoningTokens { get; init; }
    public DateTimeOffset DecidedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? DecidedByAgent { get; init; }

    public static void ValidateScore(int score, string fieldName)
    {
        if (score is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(fieldName, score, "Scores must be in the inclusive range [1, 10].");
        }
    }
}
