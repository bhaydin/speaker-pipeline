namespace SpeakerPipeline.Agents.Scoring;

/// <summary>
/// The canonical rubric used by the scoring agent. Kept as a separate file
/// because (a) the eval suite asserts against keywords from it and (b) any
/// rubric change should be reviewed alongside an updated golden set.
/// </summary>
public static class ScoringRubric
{
    public const string Version = "v1";

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
    /// Builds the user-message prompt for a single event. Keep this stable —
    /// it's part of the eval contract.
    /// </summary>
    public static string BuildUserPrompt(Core.EventRecord eventRecord, IReadOnlyList<Core.TalkRecord> talks)
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
}
