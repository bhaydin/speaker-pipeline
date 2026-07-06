namespace SpeakerPipeline.Core;

/// <summary>
/// The single source of truth for how a scoring <see cref="Recommendation"/>
/// maps to the tracker's <see cref="EventCategory"/>. Shared by the API (when it
/// applies a decision) and the scoring agent (when it detects a verdict flip), so
/// the two never drift.
/// </summary>
public static class ScoringMap
{
    public static EventCategory ToCategory(Recommendation recommendation) => recommendation switch
    {
        Recommendation.SubmitNow => EventCategory.SubmitNow,
        Recommendation.Outreach => EventCategory.Outreach,
        Recommendation.Monitor => EventCategory.Monitor,
        Recommendation.Pass => EventCategory.Pass,
        Recommendation.Skip => EventCategory.Skip,
        _ => EventCategory.Monitor,
    };
}

/// <summary>
/// How a fresh scoring decision relates to what the tracker already held for the
/// event. Verdict <see cref="Changed"/> flips are the interesting signal in a
/// scoring digest; <see cref="New"/> events were never scored before.
/// </summary>
public enum VerdictChange
{
    /// <summary>The event had no prior agent decision.</summary>
    New,

    /// <summary>The event was scored before and the recommendation moved it to a different category.</summary>
    Changed,

    /// <summary>The event was scored before and lands in the same category.</summary>
    Unchanged,
}
