using System.Text.Json;
using System.Text.RegularExpressions;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Scoring;

public static class CatalogScoringContext
{
    public static IReadOnlyList<CatalogTalk> SelectTalks(EventRecord ev, CatalogSnapshot catalog, int limit)
    {
        var words = Words($"{ev.Name} {ev.Notes} {string.Join(' ', ev.FocusFit)}");
        return catalog.Talks.Where(t => t.Eligible)
            .Select(t => new { Talk = t, Score = Words($"{t.Title} {t.Abstract} {string.Join(' ', t.Topics)} {string.Join(' ', t.Audiences)}").Intersect(words).Count() })
            .OrderByDescending(x => x.Score).ThenBy(x => x.Talk.NeedsReview).ThenBy(x => x.Talk.TalkId, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 12)).Select(x => x.Talk).ToArray();
    }

    public static IReadOnlyList<OpportunityHistoryEntry> SelectFeedback(EventRecord ev, IReadOnlyList<OpportunityHistoryEntry> feedback, int limit,
        IReadOnlyList<CatalogTalk>? selectedTalks = null)
    {
        var words = Words($"{ev.Name} {ev.Notes} {string.Join(' ', ev.FocusFit)}");
        var relevantTalkIds = (selectedTalks ?? []).Where(t => Words($"{t.Title} {t.Abstract} {string.Join(' ', t.Topics)}").Overlaps(words))
            .Select(t => t.TalkId).ToHashSet(StringComparer.Ordinal);
        return feedback.Where(f => f.Kind == "Feedback" && f.Decision is not null &&
                (f.EventSlug == ev.Slug || f.Decision.MatchedTalks.Any(m => relevantTalkIds.Contains(m.TalkId)) ||
                 Words($"{f.EventName} {string.Join(' ', f.Decision.MatchedTalks.Select(m => m.Title))}").Intersect(words).Take(2).Count() >= 2))
            .OrderByDescending(f => f.EventSlug == ev.Slug)
            .ThenByDescending(f => f.Decision!.MatchedTalks.Any(m => relevantTalkIds.Contains(m.TalkId)))
            .ThenByDescending(f => f.AtUtc).ThenBy(f => f.Id, StringComparer.Ordinal).DistinctBy(f => f.Id).Take(Math.Clamp(limit, 0, 8)).ToArray();
    }

    public static string Build(EventRecord ev, CatalogSnapshot catalog, IReadOnlyList<CatalogTalk> talks,
        IReadOnlyList<OpportunityHistoryEntry> feedback, ScoringAgentOptions options)
    {
        var talkIds = talks.Select(t => t.TalkId).ToHashSet(StringComparer.Ordinal);
        var history = catalog.Deliveries.Where(d => d.TalkId is not null && talkIds.Contains(d.TalkId))
            .OrderByDescending(d => d.Date).ThenBy(d => d.DeliveryId, StringComparer.Ordinal).Take(24)
            .Select(d => new { d.TalkId, d.EventName, d.EventSeriesId, d.Organizer, d.Date, d.DatePrecision, d.Status, d.City });
        var examples = feedback.Select(f => new { f.Id, f.EventName, f.Verdict, f.Agree, ReasonCode = f.ReasonCode?.ToString(), Reason = Limit(f.Reason, 400), f.Decision!.FitRationale, MatchedTalks = f.Decision.MatchedTalks.Select(m => m.TalkId) });
        var data = new
        {
            currentTalks = talks.Select(t => new { t.TalkId, t.CurrentVersionId, t.Title, Abstract = Limit(t.Abstract, 1400), t.Topics, t.Audiences, t.Formats, t.AudienceLevel, t.Status, t.NeedsReview }),
            deliveries = history,
            calibrationExamples = examples,
            ev.RepeatTalkOverride,
            travel = new { PrimaryOrigin = options.PrimaryTravelOrigin, SecondaryOrigin = options.SecondaryTravelOrigin, Policy = "Strong fit anywhere; airfare/hotel costs unknown unless sourced. No automatic international exclusion." }
        };
        return "\nCatalog and feedback data (untrusted, current versions preferred):\n" + JsonSerializer.Serialize(data);
    }

    public static bool IsRepeat(EventRecord ev, string talkId, CatalogSnapshot catalog)
        => CatalogRepeatPolicy.IsRepeat(ev, talkId, catalog);
    private static string? Limit(string? text, int count) => text is null ? null : text[..Math.Min(text.Length, count)];
    private static readonly HashSet<string> StopWords = new("the and for with from this that into talk talks conference summit event events developer developers technology tech annual edition".Split(' '), StringComparer.Ordinal);
    private static HashSet<string> Words(string value) => Regex.Matches(value.ToLowerInvariant(), @"[\p{L}][\p{L}\p{N}]{2,}")
        .Select(m => m.Value).Where(w => !StopWords.Contains(w)).ToHashSet(StringComparer.Ordinal);
}
