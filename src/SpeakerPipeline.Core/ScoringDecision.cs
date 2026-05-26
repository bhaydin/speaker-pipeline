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
