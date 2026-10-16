using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Scoring;

internal sealed record ScoringInput(string Prompt, IReadOnlyList<CatalogTalk> Talks, IReadOnlyList<OpportunityHistoryEntry> Feedback,
    string Fingerprint, string EventFactsRevision, string CalendarRevision, string CatalogContextRevision, string FeedbackRevision, string ContextRevision)
{
    public static ScoringInput Prepare(EventRecord ev, IReadOnlyList<TalkRecord> legacyTalks, PipelineContext context,
        CatalogSnapshot? catalog, IReadOnlyList<OpportunityHistoryEntry> feedback, ScoringAgentOptions options)
    {
        var selected = catalog is null ? [] : CatalogScoringContext.SelectTalks(ev, catalog, options.MaxCatalogTalksPerCandidate);
        var examples = CatalogScoringContext.SelectFeedback(ev, feedback, options.MaxFeedbackExamples, selected);
        var basePrompt = ScoringRubric.BuildUserPrompt(ev, catalog is null ? legacyTalks.OrderBy(t => t.Slug, StringComparer.Ordinal).ToArray() : [], context);
        string CatalogBlock() => CatalogScoringContext.Build(ev, catalog ?? new(), selected, examples, options);
        var prompt = basePrompt + CatalogBlock();
        // Leave bounded space for the prior decision explanation, which is output context rather than cache identity.
        var characterLimit = options.MaxInputCharactersPerCandidate - 800;
        while (prompt.Length + ScoringRubric.SystemPrompt.Length > characterLimit && (examples.Count > 0 || selected.Count > 1))
        {
            if (examples.Count > 0) examples = examples.Take(examples.Count - 1).ToArray();
            else selected = selected.Take(selected.Count - 1).ToArray();
            prompt = basePrompt + CatalogBlock();
        }
        if (prompt.Length + ScoringRubric.SystemPrompt.Length > characterLimit) throw new ScoringBudgetException();

        var eventRevision = Hash(ScoringRubric.BuildUserPrompt(ev, [], PipelineContext.Empty) + ScoringRubric.DeadlineBand(ev.CfpDeadline, context.AsOfUtc));
        var calendarRevision = Hash(ScoringRubric.BuildContextBlock(ev, context) +
            (context.NewTopicPrepsThisMonth + context.NewTopicPrepsNextMonth > 0 ? context.AsOfUtc.ToString("yyyy-MM") : ""));
        var catalogRevision = Hash(catalog is null ? JsonSerializer.Serialize(legacyTalks.Where(t => !t.Retired).OrderBy(t => t.Slug)) :
            CatalogScoringContext.Build(ev, catalog, selected, [], options));
        var feedbackRevision = Hash(JsonSerializer.Serialize(examples.Select(f => new
            { f.Id, f.EventSlug, f.EventName, f.ReasonCode, f.Reason, f.Agree, f.DecisionId, f.Decision })));
        var contextRevision = Hash(calendarRevision + catalogRevision + feedbackRevision);
        var modelRevision = JsonSerializer.Serialize(new { options.Provider, options.ModelName, options.ModelRevision, options.MaxOutputTokens, Reasoning = "low" });
        // Raw generation IDs, repository commit timestamps, run time, ETag, and our previous verdict are deliberately absent.
        var fingerprint = Hash(ScoringRubric.Version + ScoringRubric.SystemPrompt + modelRevision + prompt + contextRevision);
        return new(prompt, selected, examples, fingerprint, eventRevision, calendarRevision, catalogRevision, feedbackRevision, contextRevision);
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
