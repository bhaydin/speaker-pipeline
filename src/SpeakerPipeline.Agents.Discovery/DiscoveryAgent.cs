using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Discovery;

/// <summary>
/// The discovery agent (Scout). Pulls watchlist pages, extracts a structured
/// event from each, and reconciles it into the tracker as a Monitor-category
/// candidate (which the Evaluator then scores). New or changed events are
/// written; unchanged ones and the do-not-resurface list are left alone, so the
/// run is idempotent and safe to schedule.
/// </summary>
public sealed partial class DiscoveryAgent
{
    public const string ActivitySourceName = "SpeakerPipeline.Agents.Discovery";
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Below this the extraction is treated as too weak to write.</summary>
    private const int MinConfidence = 4;

    /// <summary>
    /// Candidates within this band below the floor are quarantined for review
    /// rather than dropped: <c>floor-2 .. floor-1</c>.
    /// </summary>
    private const int QuarantineBand = 2;

    private readonly IChatClient _chat;
    private readonly ISpeakerPipelineApiClient _api;
    private readonly IReadOnlyList<ISourceAdapter> _sources;
    private readonly ILogger<DiscoveryAgent> _logger;
    private readonly DiscoveryOptions _options;
    private readonly SearchOptions _searchOptions;

    public DiscoveryAgent(
        IChatClient chat,
        ISpeakerPipelineApiClient api,
        IEnumerable<ISourceAdapter> sources,
        IOptions<DiscoveryOptions> options,
        IOptions<SearchOptions> searchOptions,
        ILogger<DiscoveryAgent> logger)
    {
        _chat = chat;
        _api = api;
        _sources = [.. sources];
        _options = options.Value;
        _searchOptions = searchOptions.Value;
        _logger = logger;
    }

    public async Task<DiscoveryRunReport> RunAsync(CancellationToken ct = default)
    {
        using var runActivity = ActivitySource.StartActivity("discovery.run");
        runActivity?.SetTag("agent.name", _options.AgentName);

        var candidates = new List<DiscoveryCandidate>();
        foreach (var source in _sources)
        {
            candidates.AddRange(await source.FetchAsync(ct));
        }

        var existing = (await _api.GetEventsAsync(ct: ct))
            .ToDictionary(e => e.Slug, StringComparer.OrdinalIgnoreCase);

        var decidedBy = $"{_options.AgentName}-{_options.AgentVersion}";
        var now = DateTimeOffset.UtcNow;

        var changed = new List<DiscoveryResult>();
        var quarantined = new List<DiscoveryResult>();
        var dropped = new Dictionary<string, int>(StringComparer.Ordinal);
        var bySource = new Dictionary<string, int>(StringComparer.Ordinal);
        long inputTokens = 0, outputTokens = 0;
        int extractedCount = 0, passedFloor = 0;

        void Drop(string reason) => dropped[reason] = dropped.GetValueOrDefault(reason) + 1;

        foreach (var candidate in candidates)
        {
            var sourceName = candidate.Source.ToString();
            bySource[sourceName] = bySource.GetValueOrDefault(sourceName) + 1;

            // Structured feeds arrive pre-extracted (no LLM); raw pages are extracted here.
            ExtractedEvent extracted;
            if (candidate is { Extracted: { } structured, Page: null })
            {
                extracted = structured;
            }
            else if (candidate is { Extracted: null, Page: { } page })
            {
                try
                {
                    var (e, inTok, outTok) = await ExtractAsync(page, ct);
                    extracted = e;
                    inputTokens += inTok;
                    outputTokens += outTok;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Discovery: extraction failed for {Url}", candidate.Url);
                    Drop("extract_failed");
                    continue;
                }
            }
            else
            {
                _logger.LogWarning(
                    "Discovery: dropped invalid candidate for {Url} (hasPage={HasPage}, hasExtracted={HasExtracted})",
                    candidate.Url,
                    candidate.Page is not null,
                    candidate.Extracted is not null);
                Drop("invalid_candidate");
                continue;
            }

            if (!extracted.IsEvent || string.IsNullOrWhiteSpace(extracted.EventName))
            {
                Drop("not_event");
                continue;
            }
            extractedCount++;

            var slug = Slugify(extracted.EventName);
            existing.TryGetValue(slug, out var current);

            var floor = candidate.Page?.MinConfidence ?? MinConfidence;
            if (extracted.Confidence < floor)
            {
                // Quarantine tier: hold genuinely-new candidates in the band just
                // under the floor for review instead of dropping them silently. An
                // event that already exists keeps its own category.
                if (current is null && extracted.Confidence >= floor - QuarantineBand)
                {
                    var (qUpsert, qSummary, _) = Reconcile(
                        null, extracted, slug, candidate.Source, now, decidedBy, candidate.DiscoveredVia,
                        newCategory: EventCategory.Quarantine);
                    if (qUpsert is null)
                    {
                        Drop(ClassifyDrop(qSummary));
                        continue;
                    }
                    if (await TryUpsertAsync(qUpsert, slug, ct))
                    {
                        existing[slug] = qUpsert;
                        quarantined.Add(new DiscoveryResult(
                            slug, extracted.EventName, IsNew: true,
                            $"quarantined (confidence {extracted.Confidence} < floor {floor})"));
                        _logger.LogInformation("Discovery: quarantined {Slug} (confidence {Confidence} < floor {Floor})",
                            slug, extracted.Confidence, floor);
                    }
                    continue;
                }

                _logger.LogInformation("Discovery: dropped {Url} (confidence={Confidence} < floor={Floor})",
                    candidate.Url, extracted.Confidence, floor);
                Drop("low_confidence");
                continue;
            }
            passedFloor++;

            var (upsert, summary, isNew) = Reconcile(current, extracted, slug, candidate.Source, now, decidedBy, candidate.DiscoveredVia);
            if (upsert is null)
            {
                Drop(ClassifyDrop(summary));
                continue;
            }

            if (await TryUpsertAsync(upsert, slug, ct))
            {
                existing[slug] = upsert;
                changed.Add(new DiscoveryResult(slug, extracted.EventName, isNew, summary));
                _logger.LogInformation("Discovery: {Verb} {Slug} ({Summary})", isNew ? "new" : "updated", slug, summary);
            }
        }

        var funnel = new DiscoveryFunnel
        {
            Targets = candidates.Count,
            Extracted = extractedCount,
            PassedFloor = passedFloor,
            New = changed.Count(r => r.IsNew),
            Updated = changed.Count(r => !r.IsNew),
            Quarantined = quarantined.Count,
            Dropped = dropped,
            CandidatesBySource = bySource,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            SearchQueries = CountSearchQueries(),
        };

        runActivity?.SetTag("candidates.fetched", candidates.Count);
        runActivity?.SetTag("events.changed", changed.Count);
        runActivity?.SetTag("events.quarantined", quarantined.Count);
        runActivity?.SetTag("tokens.total", funnel.TotalTokens);
        _logger.LogInformation(
            "Discovery run complete. {Changed} changed, {Quarantined} quarantined from {Candidates} candidates. Tokens={Tokens}, searchQueries={Queries}.",
            changed.Count, quarantined.Count, candidates.Count, funnel.TotalTokens, funnel.SearchQueries);

        return new DiscoveryRunReport(changed, quarantined, funnel);
    }

    /// <summary>Maps a null-upsert reconcile summary to a stable funnel drop code.</summary>
    internal static string ClassifyDrop(string summary)
    {
        if (summary.Contains("do-not-resurface", StringComparison.OrdinalIgnoreCase))
        {
            return "do_not_resurface";
        }
        if (summary.Contains("cfp closed", StringComparison.OrdinalIgnoreCase))
        {
            return "cfp_closed";
        }
        return "unchanged";
    }

    private int CountSearchQueries()
    {
        if (!_searchOptions.Enabled)
        {
            return 0;
        }
        var nonBlank = _searchOptions.Queries.Count(q => !string.IsNullOrWhiteSpace(q));
        return Math.Min(nonBlank, _searchOptions.MaxQueriesPerRun);
    }

    private async Task<bool> TryUpsertAsync(EventRecord upsert, string slug, CancellationToken ct)
    {
        try
        {
            await _api.UpsertEventAsync(upsert, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Discovery: failed to upsert {Slug}", slug);
            return false;
        }
    }

    private async Task<(ExtractedEvent Extracted, long InputTokens, long OutputTokens)> ExtractAsync(SourcePage page, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, DiscoveryPrompt.SystemPrompt),
            new(ChatRole.User, DiscoveryPrompt.BuildUserPrompt(page)),
        };

        var options = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.Json,
            ModelId = _options.ModelName,
        };

        var response = await _chat.GetResponseAsync(messages, options, ct);
        var raw = response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text ?? response.Text;
        return (ParseExtracted(raw), response.Usage?.InputTokenCount ?? 0, response.Usage?.OutputTokenCount ?? 0);
    }

    internal static ExtractedEvent ParseExtracted(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return new ExtractedEvent { IsEvent = false };
        }

        var trimmed = rawJson.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
            {
                trimmed = trimmed[(firstNewline + 1)..];
                if (trimmed.EndsWith("```", StringComparison.Ordinal))
                {
                    trimmed = trimmed[..^3];
                }
            }
            else
            {
                // One-line fence (e.g. ```json{...}```) — fall back to extracting the JSON object bounds.
                var firstBrace = trimmed.IndexOf('{');
                var lastBrace = trimmed.LastIndexOf('}');
                if (firstBrace >= 0 && lastBrace > firstBrace)
                {
                    trimmed = trimmed[firstBrace..(lastBrace + 1)];
                }
            }

            trimmed = trimmed.Trim();
        }

        try
        {
            return JsonSerializer.Deserialize<ExtractedEvent>(trimmed, JsonOpts)
                ?? new ExtractedEvent { IsEvent = false };
        }
        catch (JsonException)
        {
            // A garbled page is a skip, not a failure — the run continues.
            return new ExtractedEvent { IsEvent = false };
        }
    }

    /// <summary>
    /// Merges an extracted event with what the tracker already holds. New events
    /// enter as Monitor candidates; the do-not-resurface list is skipped; an
    /// existing event is updated only when the extraction carries new information
    /// that actually differs — and never has its Category/Priority/DoNotResurface
    /// overwritten (those belong to scoring and to Brian). Returns a null upsert
    /// when there is nothing to write, which keeps the run idempotent.
    /// </summary>
    internal static (EventRecord? Upsert, string ChangeSummary, bool IsNew) Reconcile(
        EventRecord? existing, ExtractedEvent extracted, string slug, SourceSeenOn source, DateTimeOffset now, string decidedBy,
        string? discoveredVia = null, EventCategory newCategory = EventCategory.Monitor)
    {
        if (existing is null)
        {
            // A newly-discovered CFP that is already closed isn't worth tracking when it came from search —
            // don't flood the tracker with dead opportunities from search noise.
            if (!string.IsNullOrWhiteSpace(discoveredVia) && extracted.CfpStatus == CfpStatus.Closed)
            {
                return (null, "skipped: cfp closed", false);
            }

            var provenance = string.IsNullOrWhiteSpace(discoveredVia)
                ? "Discovered"
                : $"Discovered via search: {discoveredVia}";

            var created = new EventRecord
            {
                Slug = slug,
                Name = extracted.EventName,
                EventType = extracted.EventType,
                Category = newCategory, // Monitor enters the scoring pool; Quarantine holds for review
                Priority = Priority.NA,
                FocusFit = extracted.FocusFit,
                EventDateStart = extracted.EventStartDate,
                EventDateEnd = extracted.EventEndDate,
                CfpDeadline = extracted.CfpDeadline,
                CfpUrl = extracted.CfpUrl,
                CfpStatus = extracted.CfpStatus,
                EventUrl = extracted.EventUrl,
                Location = extracted.Location,
                Format = extracted.Format,
                TravelBurden = extracted.TravelBurden,
                SourceSeenOn = source,
                LastVerifiedUtc = now,
                DiscoveredByAgent = decidedBy,
                StatusDetail = provenance,
            };
            return (created, "new", true);
        }

        if (existing.DoNotResurface)
        {
            return (null, "skipped: do-not-resurface", false);
        }

        // Only NEW information that differs counts as a change — a null/Unknown
        // extraction means "no fresh signal", so it never overwrites known data.
        var changes = new List<string>();
        if (extracted.CfpStatus != CfpStatus.Unknown && existing.CfpStatus != extracted.CfpStatus)
        {
            changes.Add($"CfpStatus {existing.CfpStatus?.ToString() ?? "null"}->{extracted.CfpStatus}");
        }
        if (extracted.CfpDeadline is not null && existing.CfpDeadline != extracted.CfpDeadline)
        {
            changes.Add("CfpDeadline");
        }
        if (extracted.Location is not null && !string.Equals(existing.Location, extracted.Location, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add("Location");
        }
        if (extracted.Format is not null && existing.Format != extracted.Format)
        {
            changes.Add("Format");
        }
        if (extracted.CfpUrl is not null && !string.Equals(existing.CfpUrl, extracted.CfpUrl, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add("CfpUrl");
        }

        if (changes.Count == 0)
        {
            return (null, "no change", false);
        }

        var summary = string.Join("; ", changes);
        var updated = existing with
        {
            CfpStatus = extracted.CfpStatus != CfpStatus.Unknown ? extracted.CfpStatus : existing.CfpStatus,
            CfpDeadline = extracted.CfpDeadline ?? existing.CfpDeadline,
            Location = extracted.Location ?? existing.Location,
            Format = extracted.Format ?? existing.Format,
            CfpUrl = extracted.CfpUrl ?? existing.CfpUrl,
            LastVerifiedUtc = now,
            DiscoveredByAgent = existing.DiscoveredByAgent ?? decidedBy,
            StatusDetail = $"Updated by discovery: {summary}",
        };
        return (updated, summary, false);
    }

    /// <summary>Kebab-cases an event name into a stable slug (the tracker's row key).</summary>
    internal static string Slugify(string name)
    {
        var slug = NonSlugChars().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        return slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugChars();
}
