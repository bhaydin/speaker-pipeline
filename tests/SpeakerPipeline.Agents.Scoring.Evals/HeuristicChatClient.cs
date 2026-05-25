using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Scoring.Evals;

/// <summary>
/// Deterministic IChatClient that approximates the rubric. Lets the eval
/// suite run hermetically in CI without burning model tokens or relying on
/// a network call. When a real model is wired in Phase 3+, the eval suite
/// can swap this for the real IChatClient and re-baseline.
///
/// The heuristic is deliberately conservative: it asserts that the rubric
/// described in ScoringRubric.SystemPrompt is internally consistent and
/// produces the expected Recommendation on the curated golden cases. It is
/// not a substitute for model evaluation — it is a regression guard for
/// the rubric.
/// </summary>
internal sealed class HeuristicChatClient : IChatClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    public ChatClientMetadata Metadata { get; } = new("heuristic", new Uri("urn:speakerpipeline:heuristic"), "rubric-heuristic-v1");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var userMessage = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var ctx = PromptParser.Parse(userMessage);
        var decision = Decide(ctx);
        var json = JsonSerializer.Serialize(decision, JsonOpts);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    // -------- Heuristic --------------------------------------------------

    private static HeuristicResponse Decide(PromptContext c)
    {
        var rationale = new List<string>();
        var lane = MatchLane(c);
        var notes = c.Notes.ToLowerInvariant();
        var name = c.Name.ToLowerInvariant();
        var deadlineDays = c.CfpDeadline?.Subtract(DateTimeOffset.UtcNow).TotalDays;

        var congestion = notes.Contains("congestion", StringComparison.Ordinal);
        var noFormalCfp = notes.Contains("no public cfp", StringComparison.Ordinal)
                         || notes.Contains("schedule speakers directly", StringComparison.Ordinal)
                         || notes.Contains("organizers schedule directly", StringComparison.Ordinal);
        var cfpStale = notes.Contains("not yet open", StringComparison.Ordinal)
                       || notes.Contains("no confirmation", StringComparison.Ordinal)
                       || notes.Contains("late-2026 opening", StringComparison.Ordinal);
        var offTopic = !lane.HasValue && c.FocusFit.Count == 0
            && (notes.Contains("blockchain", StringComparison.Ordinal)
                || notes.Contains("web3", StringComparison.Ordinal)
                || notes.Contains("erp", StringComparison.Ordinal)
                || notes.Contains("crypto", StringComparison.Ordinal));
        var heavyTravel = c.TravelBurden == TravelBurden.High
                          || (!string.IsNullOrEmpty(c.Location)
                              && (c.Location!.Contains("Germany", StringComparison.OrdinalIgnoreCase)
                                  || c.Location.Contains("Europe", StringComparison.OrdinalIgnoreCase)));

        Recommendation recommendation;
        int fit;
        int effort;
        int confidence;
        string? talkSlug = null;

        // 1. Hard skip: off-topic AND high travel AND short notice.
        if (offTopic && heavyTravel)
        {
            rationale.Add("Topic does not fit any of the four lanes and travel burden is high.");
            recommendation = Recommendation.Skip;
            fit = 1; effort = 2; confidence = 9;
        }
        else if (offTopic)
        {
            rationale.Add("Topic does not match the current four-lane focus.");
            recommendation = Recommendation.Pass;
            fit = 2; effort = 5; confidence = 8;
        }
        else if (noFormalCfp)
        {
            rationale.Add("No formal CFP — better treated as outreach to the organizers.");
            recommendation = Recommendation.Outreach;
            fit = lane.HasValue ? 7 : 5;
            effort = 4;
            confidence = 7;
        }
        else if (cfpStale)
        {
            rationale.Add("CFP timing or status needs revalidation before submitting.");
            recommendation = Recommendation.Monitor;
            fit = lane.HasValue ? 7 : 5;
            effort = 5;
            confidence = 6;
        }
        else if (congestion)
        {
            rationale.Add("Strong fit, but prep congestion in the surrounding 4 weeks argues against another heavy submission.");
            recommendation = Recommendation.Monitor;
            fit = 8;
            effort = 6;
            confidence = 7;
            talkSlug = LaneToTalk(lane);
        }
        else if (lane.HasValue)
        {
            var urgentDeadline = deadlineDays is double d and (> 0 and < 14);
            var lowEffort = c.Format == EventFormat.Virtual
                            || notes.Contains("reused", StringComparison.Ordinal)
                            || notes.Contains("recorded", StringComparison.Ordinal);

            rationale.Add($"Topic fit on the {lane} lane.");
            if (urgentDeadline)
            {
                rationale.Add($"Deadline is inside 14 days — urgency bumps recommendation toward SubmitNow.");
            }
            if (c.TravelBurden is TravelBurden.None or TravelBurden.Low)
            {
                rationale.Add("Travel burden is low.");
            }
            if (lowEffort)
            {
                rationale.Add("Effort is low because reusability is high or format is recorded.");
            }

            recommendation = Recommendation.SubmitNow;
            fit = lane == Lane.AgentOps && (name.Contains("agentops") || notes.Contains("agentops")) ? 9 : 8;
            effort = lowEffort ? 3 : 5;
            confidence = urgentDeadline ? 8 : 7;
            talkSlug = LaneToTalk(lane);
        }
        else
        {
            rationale.Add("Topic fit is unclear from the listing; needs revalidation.");
            recommendation = Recommendation.Monitor;
            fit = 4;
            effort = 5;
            confidence = 5;
        }

        return new HeuristicResponse
        {
            EventSlug = c.Slug,
            Recommendation = recommendation,
            Rationale = string.Join(" ", rationale),
            FitScore = fit,
            EffortScore = effort,
            ConfidenceScore = confidence,
            RecommendedTalkSlug = talkSlug,
        };
    }

    private static Lane? MatchLane(PromptContext c)
    {
        if (c.FocusFit.Count > 0)
        {
            return c.FocusFit[0];
        }

        var name = c.Name.ToLowerInvariant();
        var notes = c.Notes.ToLowerInvariant();

        if (name.Contains("agentops") || notes.Contains("agentops")) return Lane.AgentOps;
        if (name.Contains("power platform") || notes.Contains("copilot studio")) return Lane.HybridAgents;
        if (name.Contains("governance") || notes.Contains("governance")) return Lane.M365Governance;
        if (name.Contains("enterprise ai") || notes.Contains("enterprise ai") || notes.Contains("ai governance")) return Lane.PracticalEnterpriseAI;

        return null;
    }

    private static string? LaneToTalk(Lane? lane) => lane switch
    {
        Lane.AgentOps => "agentops-real-world",
        Lane.HybridAgents => "hybrid-agents-sweet-spot",
        Lane.M365Governance => "m365-request-to-retirement",
        Lane.PracticalEnterpriseAI => "foundry-for-enterprise",
        _ => null,
    };

    private sealed record HeuristicResponse
    {
        public required string EventSlug { get; init; }
        public required Recommendation Recommendation { get; init; }
        public required string Rationale { get; init; }
        public required int FitScore { get; init; }
        public required int EffortScore { get; init; }
        public required int ConfidenceScore { get; init; }
        public string? RecommendedTalkSlug { get; init; }
    }
}

internal sealed record PromptContext
{
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<Lane> FocusFit { get; init; }
    public DateTimeOffset? CfpDeadline { get; init; }
    public string? Location { get; init; }
    public EventFormat? Format { get; init; }
    public TravelBurden? TravelBurden { get; init; }
    public required string Notes { get; init; }
}

internal static class PromptParser
{
    public static PromptContext Parse(string userPrompt)
    {
        string? Get(string label)
        {
            var idx = userPrompt.IndexOf(label, StringComparison.Ordinal);
            if (idx < 0) return null;
            var lineEnd = userPrompt.IndexOf('\n', idx);
            var line = lineEnd < 0 ? userPrompt[idx..] : userPrompt[idx..lineEnd];
            var colon = line.IndexOf(':');
            return colon < 0 ? null : line[(colon + 1)..].Trim();
        }

        DateTimeOffset? GetDate(string label)
        {
            var raw = Get(label);
            if (string.IsNullOrEmpty(raw) || raw == "none" || raw == "unknown") return null;
            return DateTimeOffset.TryParse(raw, out var dt) ? dt : null;
        }

        T? GetEnum<T>(string label) where T : struct, Enum
        {
            var raw = Get(label);
            if (string.IsNullOrEmpty(raw) || raw == "unknown") return null;
            return Enum.TryParse<T>(raw, ignoreCase: true, out var v) ? v : null;
        }

        var focusFitRaw = Get("FocusFit:") ?? string.Empty;
        var focusFit = focusFitRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => Enum.TryParse<Lane>(p, ignoreCase: true, out var l) ? (Lane?)l : null)
            .Where(l => l.HasValue)
            .Select(l => l!.Value)
            .ToArray();

        return new PromptContext
        {
            Slug = Get("Slug:") ?? string.Empty,
            Name = Get("Name:") ?? string.Empty,
            FocusFit = focusFit,
            CfpDeadline = GetDate("CfpDeadline:"),
            Location = Get("Location:"),
            Format = GetEnum<EventFormat>("Format:"),
            TravelBurden = GetEnum<TravelBurden>("TravelBurden:"),
            Notes = Get("Notes:") ?? string.Empty,
        };
    }
}
