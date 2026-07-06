using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Discovery;

/// <summary>
/// The manual ingestion lane (BUILD_PLAN.GO_FORWARD A2). Fetches a single
/// human-supplied URL, extracts an event from it at a lower confidence floor
/// (a human vouched for it), reconciles it into the tracker as a Monitor
/// candidate, and returns a summary the caller can confirm. Reuses the same
/// normalize → extract → reconcile primitives as scheduled discovery, so the
/// two lanes never drift.
/// </summary>
public sealed class EventIngestor(
    HttpClient http,
    IChatClient chat,
    ISpeakerPipelineApiClient api,
    IOptions<DiscoveryOptions> options,
    ILogger<EventIngestor> logger) : IEventIngestService
{
    private readonly DiscoveryOptions _options = options.Value;

    /// <summary>A human vouched for the URL, so accept weaker extractions than scheduled discovery.</summary>
    internal const int IngestFloor = 2;

    public async Task<IngestResult> IngestAsync(string url, string? note = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return IngestResult.Failed(IngestStatus.FetchFailed, $"Not a valid http(s) URL: {url}");
        }

        string html;
        try
        {
            using var resp = await http.GetAsync(uri, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Ingest: {Url} returned {Status}", url, (int)resp.StatusCode);
                return IngestResult.Failed(IngestStatus.FetchFailed, $"Couldn't fetch the page ({(int)resp.StatusCode}).");
            }
            html = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ingest: failed to fetch {Url}", url);
            return IngestResult.Failed(IngestStatus.FetchFailed, "Couldn't reach that URL.");
        }

        var page = new SourcePage(url, SourceSeenOn.Manual, WatchlistSource.Normalize(html), MinConfidence: IngestFloor);
        var extracted = await ExtractAsync(page, ct);

        if (!extracted.IsEvent || string.IsNullOrWhiteSpace(extracted.EventName))
        {
            return IngestResult.Failed(IngestStatus.NotAnEvent, "That page doesn't look like a call for speakers.");
        }

        if (extracted.Confidence < IngestFloor)
        {
            return new IngestResult
            {
                Status = IngestStatus.LowConfidence,
                Message = $"Extraction was too weak to trust (confidence {extracted.Confidence}).",
                EventName = extracted.EventName,
                Confidence = extracted.Confidence,
            };
        }

        var slug = DiscoveryAgent.Slugify(extracted.EventName);
        var existing = await api.GetEventAsync(slug, ct);
        var now = DateTimeOffset.UtcNow;
        var decidedBy = $"{_options.AgentName}-{_options.AgentVersion}-ingest";

        var (upsert, summary, isNew) = DiscoveryAgent.Reconcile(
            existing, extracted, slug, SourceSeenOn.Manual, now, decidedBy, discoveredVia: null);

        if (upsert is null)
        {
            // do-not-resurface, closed, or no-change: nothing written, but tell the caller why.
            return new IngestResult
            {
                Status = IngestStatus.AlreadyTracked,
                Message = $"Already tracked ({summary}).",
                Slug = slug,
                EventName = extracted.EventName,
                Category = existing?.Category,
                Confidence = extracted.Confidence,
            };
        }

        // Stamp the human provenance (and any note) onto the record.
        var provenance = string.IsNullOrWhiteSpace(note) ? "Manually ingested" : $"Manually ingested — {note}";
        upsert = upsert with
        {
            StatusDetail = provenance,
            Notes = string.IsNullOrWhiteSpace(note) ? upsert.Notes : note.Trim(),
        };

        await api.UpsertEventAsync(upsert, ct);
        logger.LogInformation("Ingest: {Verb} {Slug} from {Url}", isNew ? "created" : "updated", slug, url);

        return new IngestResult
        {
            Status = isNew ? IngestStatus.Created : IngestStatus.Updated,
            Message = isNew
                ? $"Tracked \"{extracted.EventName}\" — queued for scoring."
                : $"Updated \"{extracted.EventName}\" ({summary}).",
            Slug = slug,
            EventName = extracted.EventName,
            Category = upsert.Category,
            Confidence = extracted.Confidence,
        };
    }

    private async Task<ExtractedEvent> ExtractAsync(SourcePage page, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, DiscoveryPrompt.SystemPrompt),
            new(ChatRole.User, DiscoveryPrompt.BuildUserPrompt(page)),
        };

        var chatOptions = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.Json,
            ModelId = _options.ModelName,
        };

        var response = await chat.GetResponseAsync(messages, chatOptions, ct);
        var raw = response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text ?? response.Text;
        return DiscoveryAgent.ParseExtracted(raw);
    }
}
