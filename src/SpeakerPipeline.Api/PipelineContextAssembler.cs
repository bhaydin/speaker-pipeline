using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api;

/// <summary>
/// Assembles a <see cref="PipelineContext"/> from the raw tracker data. Pure and
/// clock-injected so the effort-derivation and prep-count logic is unit-testable
/// without the API host. Effort is derived from the linked talk's reusability
/// (the event has no explicit effort field): a highly-reusable talk means an
/// existing deck adapts (DeckAdapt); a low/unknown one implies a fresh build
/// (NewTopic).
/// </summary>
public static class PipelineContextAssembler
{
    /// <summary>ReusabilityScore at or above this means an existing deck adapts (DeckAdapt); below/none is NewTopic.</summary>
    internal const int DeckAdaptReusabilityThreshold = 4;

    private static readonly EventCategory[] CommittedCategories = [EventCategory.Accepted, EventCategory.Delivered];
    private static readonly EventCategory[] PrepCategories = [EventCategory.SubmitNow, EventCategory.Submitted, EventCategory.Accepted];

    /// <summary>Categories the context endpoint needs to fetch (the union of committed + in-flight prep).</summary>
    public static readonly EventCategory[] RelevantCategories =
        [EventCategory.SubmitNow, EventCategory.Submitted, EventCategory.Accepted, EventCategory.Delivered];

    public static PipelineContext Assemble(
        IReadOnlyList<EventRecord> events,
        IReadOnlyDictionary<string, IReadOnlyList<SubmissionRecord>> submissionsByEvent,
        IReadOnlyList<TalkRecord> talks,
        IReadOnlyList<BlackoutRecord> blackouts,
        DateTimeOffset now)
    {
        var talkBySlug = talks
            .GroupBy(t => t.Slug, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        EffortClass EffortFor(EventRecord e)
        {
            // Prefer the talk actually submitted for this event; fall back to the
            // most-reusable talk in a matching lane; else assume a fresh build.
            TalkRecord? talk = null;
            var latest = submissionsByEvent.GetValueOrDefault(e.Slug, [])
                .OrderByDescending(s => s.SubmittedOnUtc)
                .FirstOrDefault();
            if (latest is not null)
            {
                talkBySlug.TryGetValue(latest.TalkSlug, out talk);
            }
            talk ??= e.FocusFit.Count == 0
                ? null
                : talks.Where(t => e.FocusFit.Contains(t.Lane))
                       .OrderByDescending(t => t.ReusabilityScore ?? 0)
                       .FirstOrDefault();

            return EffortFrom(talk);
        }

        var committed = events
            .Where(e => CommittedCategories.Contains(e.Category))
            .Select(e => new CommittedEngagement(e.Slug, e.Name, e.EventDateStart, e.EventDateEnd, EffortFor(e)))
            .OrderBy(c => c.Start ?? DateTimeOffset.MaxValue)
            .ToList();

        var blackoutWindows = blackouts
            .Select(b => new BlackoutWindow(b.StartDate, b.EndDate, b.Reason, b.Hardness))
            .OrderBy(b => b.Start)
            .ToList();

        var next = now.AddMonths(1);

        int PrepCount(int year, int month) => events.Count(e =>
            PrepCategories.Contains(e.Category)
            && e.EventDateStart is { } d && d.Year == year && d.Month == month
            && EffortFor(e) == EffortClass.NewTopic);

        return new PipelineContext
        {
            AsOfUtc = now,
            Committed = committed,
            Blackouts = blackoutWindows,
            NewTopicPrepsThisMonth = PrepCount(now.Year, now.Month),
            NewTopicPrepsNextMonth = PrepCount(next.Year, next.Month),
        };
    }

    internal static EffortClass EffortFrom(TalkRecord? talk) =>
        talk?.ReusabilityScore is int r && r >= DeckAdaptReusabilityThreshold
            ? EffortClass.DeckAdapt
            : EffortClass.NewTopic;
}
