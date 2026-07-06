using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Scoring;

/// <summary>
/// The canonical rubric used by the scoring agent. Kept as a separate file
/// because (a) the eval suite asserts against keywords from it and (b) any
/// rubric change should be reviewed alongside an updated golden set.
/// </summary>
public static class ScoringRubric
{
    public const string Version = "v3";

    public const string SystemPrompt = """
        You are the scoring agent for Brian Haydin's speaking pipeline. Brian
        is a Solution Architect at Concurrency. He speaks on four lanes:

          1. AgentOps — observability, evals, control, operating patterns for
             production AI agents.
          2. Hybrid Agents — the sweet spot between low-code (Copilot Studio)
             and pro-code (Microsoft Agent Framework).
          3. M365 / Copilot Governance — admin, governance, identity,
             operating model, lifecycle.
          4. Practical Enterprise AI + Data Platforms — real-world enterprise
             architecture, never generic AI futurism.

        For each event you evaluate, weigh these factors in priority order:

          1. Topic fit. Does it land in one of the four lanes?
          2. Deadline urgency. Closing within 14 days = higher priority.
          3. Travel burden. None / Low > Medium > High. Europe within 60 days
             needs an exceptional reason.
          4. Reusability. Does an existing canonical talk fit with minimal
             rework?
          5. Strategic value. Relationship building in Microsoft / AI / data
             communities; audience quality.
          6. Prep congestion. If two talks are already in flight in the
             surrounding 4 weeks, do not pile on another heavy submission.

        A "Pipeline context" block below lists Brian's committed engagements,
        blackout ranges, and new-topic preps in flight near this event's dates.
        Ground factors 3 and 6 in that block — weigh a blackout overlap or a
        crowded prep window against the recommendation. Do not invent calendar
        facts beyond what the block states; "none" means no known conflict. A
        "Known conflicts" line, when present, is a confirmed conflict already
        computed for this event — treat it as decisive: a family blackout overlap
        is a hard blocker, prep congestion argues against another heavy submission.

        Choose exactly one Recommendation:

          - SubmitNow — strong fit, clear CFP, reasonable effort, calendar has
            room.
          - Outreach — community / user-group target without a formal CFP;
            worth a direct organizer email.
          - Monitor — fits the criteria but timing is uncertain or data needs
            live revalidation.
          - Pass — does not fit current priorities; not a permanent skip.
          - Skip — explicit pass. Mark DoNotResurface=true. Use only when the
            event should never resurface (off-topic, off-region for the
            foreseeable future, fee-to-speak, etc.).

        Always score Fit, Effort, and Confidence on a 1–10 scale where 10 is
        strongest fit / lowest effort / highest confidence. Always write a
        Rationale that references the specific factors that drove the
        decision — at least one factor name from the list above.

        Respond strictly with the JSON shape requested by the user. Do not
        add prose outside the JSON object. Do not add fields not listed in
        the schema.
        """;

    /// <summary>
    /// Windowing bounds for the pipeline-context block: committed engagements
    /// within ±8 weeks of the candidate, blackouts within ±4 weeks.
    /// </summary>
    private const int CommittedWindowDays = 56;
    private const int BlackoutWindowDays = 28;

    /// <summary>
    /// Builds the user-message prompt for a single event, with no calendar
    /// context. Retained for callers that score without a pipeline view.
    /// </summary>
    public static string BuildUserPrompt(EventRecord eventRecord, IReadOnlyList<TalkRecord> talks)
        => BuildUserPrompt(eventRecord, talks, PipelineContext.Empty);

    /// <summary>
    /// Builds the user-message prompt for a single event, including the calendar
    /// context windowed to this candidate. Keep this stable — it's part of the
    /// eval contract.
    /// </summary>
    public static string BuildUserPrompt(EventRecord eventRecord, IReadOnlyList<TalkRecord> talks, PipelineContext context)
    {
        var talksBlock = talks.Count == 0
            ? "(none provided)"
            : string.Join("\n", talks.Select(t => $"- {t.Slug} (lane={t.Lane}, reusability={t.ReusabilityScore?.ToString() ?? "n/a"}): {t.CanonicalTitle}"));

        return $$"""
            Score the following event.

            Event:
              Slug:            {{eventRecord.Slug}}
              Name:            {{eventRecord.Name}}
              EventType:       {{eventRecord.EventType}}
              CurrentCategory: {{eventRecord.Category}}
              Priority:        {{eventRecord.Priority}}
              FocusFit:        {{string.Join(",", eventRecord.FocusFit)}}
              EventDateStart:  {{eventRecord.EventDateStart?.ToString("yyyy-MM-dd") ?? "unknown"}}
              CfpDeadline:     {{eventRecord.CfpDeadline?.ToString("yyyy-MM-dd") ?? "none"}}
              Location:        {{eventRecord.Location ?? "unknown"}}
              Format:          {{eventRecord.Format?.ToString() ?? "unknown"}}
              TravelBurden:    {{eventRecord.TravelBurden?.ToString() ?? "unknown"}}
              SourceSeenOn:    {{eventRecord.SourceSeenOn?.ToString() ?? "unknown"}}
              Notes:           {{eventRecord.Notes ?? ""}}

            Candidate talks (most-reusable first):
            {{talksBlock}}

            {{BuildContextBlock(eventRecord, context)}}

            Respond with a single JSON object matching this schema:
            {
              "eventSlug": "<echo back the input slug>",
              "recommendation": "SubmitNow|Outreach|Monitor|Pass|Skip",
              "rationale": "<one or two sentences referencing rubric factors>",
              "fitScore": <integer 1-10>,
              "effortScore": <integer 1-10>,
              "confidenceScore": <integer 1-10>,
              "recommendedTalkSlug": "<one of the candidate slugs, or null>"
            }
            """;
    }

    /// <summary>
    /// Renders the pipeline-context block, windowed to the candidate's anchor
    /// date (its start, or the CFP deadline as a fallback). Committed engagements
    /// and blackouts outside the window are omitted so the model sees only what's
    /// near this event. An undated candidate can't be checked for overlap.
    /// </summary>
    private static string BuildContextBlock(EventRecord eventRecord, PipelineContext context)
    {
        var anchor = eventRecord.EventDateStart ?? eventRecord.CfpDeadline;
        var prepLine = $"{context.NewTopicPrepsThisMonth} this month, {context.NewTopicPrepsNextMonth} next";
        var conflicts = ConflictsLine(eventRecord);

        if (anchor is not { } a)
        {
            var undated = new List<string>
            {
                $"Pipeline context (as of {context.AsOfUtc:yyyy-MM-dd}): candidate has no date; calendar overlap not evaluated.",
            };
            if (conflicts is not null)
            {
                undated.Add($"  Known conflicts:  {conflicts}");
            }
            undated.Add($"  NewTopic preps in flight: {prepLine}");
            return string.Join("\n", undated);
        }

        var committed = context.Committed
            .Where(c => (c.Start ?? c.End) is { } start
                       && (c.End ?? c.Start) is { } end
                       && end >= a.AddDays(-CommittedWindowDays)
                       && start <= a.AddDays(CommittedWindowDays))
            .OrderBy(c => c.Start ?? c.End)
            .Select(c => $"{c.Slug} ({(c.Start ?? c.End):yyyy-MM-dd}, effort={c.Effort})")
            .ToList();

        var blackouts = context.Blackouts
            .Where(b => b.End >= a.AddDays(-BlackoutWindowDays) && b.Start <= a.AddDays(BlackoutWindowDays))
            .OrderBy(b => b.Start)
            .Select(b => $"{b.Start:yyyy-MM-dd}..{b.End:yyyy-MM-dd} ({b.Reason}, {b.Hardness})")
            .ToList();

        var committedLine = committed.Count == 0 ? "none" : string.Join("; ", committed);
        var blackoutLine = blackouts.Count == 0 ? "none" : string.Join("; ", blackouts);

        var lines = new List<string> { $"Pipeline context (as of {context.AsOfUtc:yyyy-MM-dd}):" };
        if (conflicts is not null)
        {
            lines.Add($"  Known conflicts:  {conflicts}");
        }
        lines.Add($"  Committed within ±8wk:  {committedLine}");
        lines.Add($"  Blackouts within ±4wk:  {blackoutLine}");
        lines.Add($"  NewTopic preps in flight: {prepLine}");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// The deterministic conflict flags the tracker set on this event (C1), or
    /// null when clear. Surfaced so the scorer treats a confirmed conflict as a
    /// hard signal rather than re-deriving it from the raw windows.
    /// </summary>
    private static string? ConflictsLine(EventRecord ev)
    {
        var flags = new List<string>();
        if (ev.FamilyConflictFlag)
        {
            flags.Add("family blackout overlap");
        }
        if (ev.PrepConflictFlag)
        {
            flags.Add("prep congestion");
        }
        return flags.Count == 0 ? null : string.Join("; ", flags);
    }
}
