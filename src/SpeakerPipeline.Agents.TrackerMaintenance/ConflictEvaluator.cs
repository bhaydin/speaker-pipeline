using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.TrackerMaintenance;

/// <summary>
/// The blackouts-first conflict engine (BUILD_PLAN.GO_FORWARD C1). Deterministic,
/// no external calendar — it reasons only over the Blackouts table and the
/// committed engagements already in the tracker. Pure so the tracker-maintenance
/// agent can persist the flags and the scoring context can consume them.
/// </summary>
public static class ConflictEvaluator
{
    /// <summary>Categories that count as a committed engagement for prep-congestion purposes.</summary>
    private static readonly EventCategory[] CommittedCategories = [EventCategory.Accepted, EventCategory.Delivered];

    /// <summary>
    /// Evaluates one event against the calendar:
    /// <list type="bullet">
    ///   <item><b>Family</b> — the event's date range overlaps a <see cref="BlackoutHardness.Hard"/> blackout.</item>
    ///   <item><b>Prep</b> — at least <paramref name="prepThreshold"/> committed engagements start within
    ///     <paramref name="prepWindowDays"/> days of the event (either side).</item>
    /// </list>
    /// An event with no start date can't be placed on the calendar, so both flags are false.
    /// </summary>
    public static (bool Family, bool Prep) Evaluate(
        EventRecord ev,
        IReadOnlyList<BlackoutRecord> blackouts,
        IReadOnlyList<EventRecord> allEvents,
        int prepWindowDays,
        int prepThreshold)
    {
        if (prepWindowDays < 0) throw new ArgumentOutOfRangeException(nameof(prepWindowDays), "Must be >= 0.");
        if (prepThreshold < 1) throw new ArgumentOutOfRangeException(nameof(prepThreshold), "Must be >= 1.");

        if (ev.EventDateStart is not { } start)
        {
            return (false, false);
        }

        var end = ev.EventDateEnd ?? start;

        var family = blackouts.Any(b =>
            b.Hardness == BlackoutHardness.Hard && Overlaps(b.StartDate, b.EndDate, start, end));

        var window = TimeSpan.FromDays(prepWindowDays);
        var nearbyCommitted = allEvents.Count(other =>
            !string.Equals(other.Slug, ev.Slug, StringComparison.OrdinalIgnoreCase)
            && CommittedCategories.Contains(other.Category)
            && other.EventDateStart is { } otherStart
            && (otherStart - start).Duration() <= window);

        return (family, nearbyCommitted >= prepThreshold);
    }

    /// <summary>True when two inclusive date ranges intersect.</summary>
    private static bool Overlaps(DateTimeOffset aStart, DateTimeOffset aEnd, DateTimeOffset bStart, DateTimeOffset bEnd)
        => aStart <= bEnd && bStart <= aEnd;
}
