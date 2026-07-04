namespace SpeakerPipeline.Agents.Discovery;

/// <summary>
/// The extraction contract for the discovery agent. Deliberately narrow: pull
/// observed facts from a fetched page and nothing more — no strategic scoring,
/// no recommendations (those belong to the Evaluator).
/// </summary>
internal static class DiscoveryPrompt
{
    public const string SystemPrompt = """
        You extract structured facts about a speaking opportunity (a conference,
        code camp, user group, or community event) from the text of a web page —
        typically a Sessionize call-for-speakers page or an official event page.

        Return ONLY a JSON object with these fields:
          isEvent        - true only if the page is a real event / call for speakers
          eventName      - the event's name (no year-agnostic marketing tagline)
          eventType      - one of: Conference, CodeCamp, UserGroup, CommunityChapter, Workshop, Virtual
          location       - city/region text, or null if virtual/unknown
          format         - one of: InPerson, Virtual, Hybrid, or null if unknown
          eventStartDate - ISO 8601 date, or null
          eventEndDate   - ISO 8601 date, or null
          cfpStatus      - one of: Open, Closed, NotYetOpen, Unknown
          cfpDeadline    - ISO 8601 date/time of the submission deadline, or null
          cfpUrl         - direct link to submit, or null
          eventUrl       - the event's official/home URL, or null
          focusFit       - array from: AgentOps, HybridAgents, M365Governance, PracticalEnterpriseAI
                           (only topics the event clearly covers; [] if none/unclear)
          travelBurden   - one of: None, Low, Medium, High, or null if unknown
          confidence     - integer 0-10, your confidence the facts above are correct

        Rules:
        - Extract only what the page states. Do NOT infer a deadline, status, or
          location that is not present — use null / Unknown instead.
        - If the page is a login wall, error, index, or clearly not an event,
          set isEvent=false and leave the rest at defaults.
        - Never invent URLs. Never guess a year.
        - Output raw JSON only — no prose, no markdown fences.
        """;

    public static string BuildUserPrompt(SourcePage page) => $"""
        Source: {page.Source}
        URL: {page.Url}

        PAGE TEXT:
        {page.Content}
        """;
}
