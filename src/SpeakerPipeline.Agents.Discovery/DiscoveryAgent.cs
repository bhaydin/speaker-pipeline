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

    private readonly IChatClient _chat;
    private readonly ISpeakerPipelineApiClient _api;
    private readonly IReadOnlyList<ISourceAdapter> _sources;
    private readonly ILogger<DiscoveryAgent> _logger;
    private readonly DiscoveryOptions _options;

    public DiscoveryAgent(
        IChatClient chat,
        ISpeakerPipelineApiClient api,
        IEnumerable<ISourceAdapter> sources,
        IOptions<DiscoveryOptions> options,
        ILogger<DiscoveryAgent> logger)
    {
        _chat = chat;
        _api = api;
        _sources = [.. sources];
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DiscoveryResult>> RunAsync(CancellationToken ct = default)
    {
        using var runActivity = ActivitySource.StartActivity("discovery.run");
        runActivity?.SetTag("agent.name", _options.AgentName);

        var pages = new List<SourcePage>();
        foreach (var source in _sources)
        {
            pages.AddRange(await source.FetchAsync(ct));
        }
        runActivity?.SetTag("pages.fetched", pages.Count);

        var existing = (await _api.GetEventsAsync(ct: ct))
            .ToDictionary(e => e.Slug, StringComparer.OrdinalIgnoreCase);

        var decidedBy = $"{_options.AgentName}-{_options.AgentVersion}";
        var now = DateTimeOffset.UtcNow;
        var results = new List<DiscoveryResult>();

        foreach (var page in pages)
        {
            ExtractedEvent extracted;
            try
            {
                extracted = await ExtractAsync(page, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Discovery: extraction failed for {Url}", page.Url);
                continue;
            }

            if (!extracted.IsEvent || string.IsNullOrWhiteSpace(extracted.EventName) || extracted.Confidence < MinConfidence)
            {
                _logger.LogInformation("Discovery: skipped {Url} (isEvent={IsEvent}, confidence={Confidence})",
                    page.Url, extracted.IsEvent, extracted.Confidence);
                continue;
            }

            var slug = Slugify(extracted.EventName);
            existing.TryGetValue(slug, out var current);

            var (upsert, summary, isNew) = Reconcile(current, extracted, slug, page.Source, now, decidedBy);
            if (upsert is null)
            {
                continue;
            }

            try
            {
                await _api.UpsertEventAsync(upsert, ct);
                existing[slug] = upsert;
                results.Add(new DiscoveryResult(slug, extracted.EventName, isNew, summary));
                _logger.LogInformation("Discovery: {Verb} {Slug} ({Summary})", isNew ? "new" : "updated", slug, summary);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Discovery: failed to upsert {Slug}", slug);
            }
        }

        runActivity?.SetTag("events.changed", results.Count);
        _logger.LogInformation("Discovery run complete. {Changed} events changed from {Pages} pages.", results.Count, pages.Count);
        return results;
    }

    private async Task<ExtractedEvent> ExtractAsync(SourcePage page, CancellationToken ct)
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
        return ParseExtracted(raw);
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
            }
            if (trimmed.EndsWith("```", StringComparison.Ordinal))
            {
                trimmed = trimmed[..^3];
            }
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
        EventRecord? existing, ExtractedEvent extracted, string slug, SourceSeenOn source, DateTimeOffset now, string decidedBy)
    {
        if (existing is null)
        {
            var created = new EventRecord
            {
                Slug = slug,
                Name = extracted.EventName,
                EventType = extracted.EventType,
                Category = EventCategory.Monitor, // enters the scoring candidate pool
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
                StatusDetail = "Discovered",
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
            DiscoveredByAgent = decidedBy,
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
