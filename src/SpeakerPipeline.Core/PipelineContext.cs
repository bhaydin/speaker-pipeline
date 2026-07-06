namespace SpeakerPipeline.Core;

/// <summary>
/// The calendar/load context the scoring agent needs to reason about the rubric
/// factors it otherwise can't see — committed engagements, blackout windows, and
/// how many new-topic preps are already in flight. Assembled server-side once per
/// scoring run (BUILD_PLAN.GO_FORWARD B1); the agent windows it per candidate
/// when building each prompt.
/// </summary>
public sealed record PipelineContext
{
    /// <summary>When the context was assembled — anchors the windowing in the prompt.</summary>
    public DateTimeOffset AsOfUtc { get; init; }

    /// <summary>Events Brian has committed to (Accepted / Delivered), with a derived effort hint.</summary>
    public IReadOnlyList<CommittedEngagement> Committed { get; init; } = [];

    /// <summary>Date ranges Brian is unavailable (the Blackouts table).</summary>
    public IReadOnlyList<BlackoutWindow> Blackouts { get; init; } = [];

    /// <summary>New-topic-class preps whose event falls in the current calendar month.</summary>
    public int NewTopicPrepsThisMonth { get; init; }

    /// <summary>New-topic-class preps whose event falls in the next calendar month.</summary>
    public int NewTopicPrepsNextMonth { get; init; }

    /// <summary>An empty context — used when scoring without calendar awareness (e.g. the 2-arg prompt overload).</summary>
    public static PipelineContext Empty { get; } = new();
}

/// <summary>
/// A committed speaking engagement in the surrounding calendar. Effort is derived
/// from the linked talk's reusability (high reusability → DeckAdapt, low/none →
/// NewTopic), so the scorer can weigh prep congestion without an explicit
/// per-event effort field.
/// </summary>
public sealed record CommittedEngagement(
    string Slug,
    string Name,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    EffortClass Effort);

/// <summary>A blackout range surfaced to the scorer for overlap checks.</summary>
public sealed record BlackoutWindow(
    DateTimeOffset Start,
    DateTimeOffset End,
    string Reason,
    BlackoutHardness Hardness);
