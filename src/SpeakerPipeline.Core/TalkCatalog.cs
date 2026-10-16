namespace SpeakerPipeline.Core;

public sealed record CatalogSource(string Repository, string Path, string CommitSha, string ContentHash);
public sealed record CatalogTalkVersion
{
    public required string VersionId { get; init; }
    public required string TalkId { get; init; }
    public required string Title { get; init; }
    public string Abstract { get; init; } = "";
    public DateTimeOffset? EffectiveDate { get; init; }
    public bool Extracted { get; init; }
    public string SourceKind { get; init; } = "metadata";
    public required CatalogSource Source { get; init; }
}

public sealed record CatalogTalk
{
    public required string TalkId { get; init; }
    public required string CurrentVersionId { get; init; }
    public required string Title { get; init; }
    public string Abstract { get; init; } = "";
    public IReadOnlyList<string> PriorTitles { get; init; } = [];
    public IReadOnlyList<string> Topics { get; init; } = [];
    public IReadOnlyList<string> Audiences { get; init; } = [];
    public IReadOnlyList<string> Formats { get; init; } = [];
    public IReadOnlyList<int> DurationsMinutes { get; init; } = [];
    public string? AudienceLevel { get; init; }
    public string Status { get; init; } = "draft";
    public bool Eligible { get; init; } = true;
    public bool NeedsReview { get; init; }
    public string PublicationStatus { get; init; } = "private";
    public required CatalogSource Source { get; init; }
}

public sealed record CatalogDelivery
{
    public required string DeliveryId { get; init; }
    public string? TalkId { get; init; }
    public required string EventName { get; init; }
    public string? EventSeriesId { get; init; }
    public string? Organizer { get; init; }
    public string? SessionTitle { get; init; }
    public DateTimeOffset? Date { get; init; }
    public string DatePrecision { get; init; } = "unknown";
    public string Status { get; init; } = "planned";
    public string Role { get; init; } = "speaker";
    public string? City { get; init; }
    public string? Region { get; init; }
    public string? Country { get; init; }
    public string? DeliveryMode { get; init; }
    public string? EventUrl { get; init; }
    public string? ArtifactUrl { get; init; }
    public string? AudienceLevel { get; init; }
    public int? DurationMinutes { get; init; }
    public IReadOnlyList<string> PreviousIds { get; init; } = [];
    public bool NeedsReview { get; init; }
    public required CatalogSource Source { get; init; }
}

public sealed record CatalogIssue(string Code, string SourcePath, string Detail);
public sealed record CatalogArtifact(string RepositoryPath, string Url, string? DeliveryId, bool NeedsReview);
public sealed record CatalogSnapshot
{
    public string GenerationId { get; init; } = "";
    public DateTimeOffset SyncedAtUtc { get; init; }
    public IReadOnlyDictionary<string, string> RepositoryHeads { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<CatalogTalk> Talks { get; init; } = [];
    public IReadOnlyList<CatalogTalkVersion> Versions { get; init; } = [];
    public IReadOnlyList<CatalogDelivery> Deliveries { get; init; } = [];
    /// <summary>Unmodified imported history lets an owner undo reviewed duplicate mappings.</summary>
    public IReadOnlyList<CatalogDelivery> SourceDeliveries { get; init; } = [];
    public IReadOnlyList<CatalogIssue> Issues { get; init; } = [];
    public IReadOnlyList<CatalogArtifact> PublicArtifacts { get; init; } = [];
    public IReadOnlyList<CatalogDeliveryCorrection> DeliveryCorrections { get; init; } = [];
    public IReadOnlyDictionary<string, bool> EligibilityOverrides { get; init; } = new Dictionary<string, bool>();
    // Explicitly reviewed aliases only. Source imports never invent legacy slug mappings.
    public IReadOnlyDictionary<string, string> LegacyTalkAliases { get; init; } = new Dictionary<string, string>();
}

public sealed record TalkMatch(string TalkId, string VersionId, string Title, string Rationale);
public sealed record CatalogExtraction(string ContentHash, string Abstract, string? AudienceLevel, IReadOnlyList<string> Topics, string ModelVersion);
public sealed record CatalogSyncRequest(string DeliveryId, string Reason);
public sealed record CatalogSyncJob(string Id, string Reason, string LeaseToken, DateTimeOffset LeaseExpiresUtc);
public sealed record CatalogJobCompletion(string LeaseToken, bool Succeeded, string? ErrorCode = null);
public sealed record CatalogPublishRequest(CatalogSnapshot Snapshot, string? ExpectedGenerationId);
public sealed record CatalogWebhookRequest(string DeliveryId, string Event, string Signature, string Payload);
public sealed record CatalogDeliveryCorrection(string DeliveryId, string? AliasOfDeliveryId, DateTimeOffset? Date, string? DatePrecision,
    string? EventSeriesId, string? Status, string? City);
public sealed record CatalogReviewRequest
{
    public required string ExpectedGenerationId { get; init; }
    public string? LegacyTalkSlug { get; init; }
    public string? TalkId { get; init; }
    public bool? Eligible { get; init; }
    public CatalogDeliveryCorrection? Delivery { get; init; }
}

public static class CatalogReview
{
    public static CatalogSnapshot Apply(CatalogSnapshot current, CatalogReviewRequest review)
    {
        if (current.GenerationId != review.ExpectedGenerationId) throw new InvalidOperationException("Catalog generation changed.");
        var aliases = new Dictionary<string, string>(current.LegacyTalkAliases, StringComparer.Ordinal);
        var eligibility = new Dictionary<string, bool>(current.EligibilityOverrides, StringComparer.Ordinal);
        var corrections = current.DeliveryCorrections.ToList();
        if (review.TalkId is { } id)
        {
            if (!current.Talks.Any(t => t.TalkId == id)) throw new ArgumentException("Unknown talk ID.");
            if (review.LegacyTalkSlug is { } legacy) aliases[legacy] = id;
            if (review.Eligible is { } eligible) eligibility[id] = eligible;
        }
        else if (review.LegacyTalkSlug is not null || review.Eligible is not null) throw new ArgumentException("Talk ID is required.");
        if (review.Delivery is { } delivery)
        {
            var sourceDeliveries = current.SourceDeliveries.Count > 0 ? current.SourceDeliveries : current.Deliveries;
            if (!sourceDeliveries.Any(d => d.DeliveryId == delivery.DeliveryId) || delivery.AliasOfDeliveryId == delivery.DeliveryId ||
                delivery.AliasOfDeliveryId is { } target && (!sourceDeliveries.Any(d => d.DeliveryId == target) || corrections.Any(c => c.DeliveryId == target && c.AliasOfDeliveryId is not null) || corrections.Any(c => c.AliasOfDeliveryId == delivery.DeliveryId)) ||
                delivery.Status is not (null or "planned" or "completed" or "cancelled") ||
                delivery.DatePrecision is not (null or "day" or "month" or "year" or "unknown"))
                throw new ArgumentException("Invalid delivery correction.");
            corrections.RemoveAll(d => d.DeliveryId == delivery.DeliveryId);
            corrections.Add(delivery);
        }
        if (review.TalkId is null && review.Delivery is null) throw new ArgumentException("A review change is required.");
        return ApplyOverrides(current with { GenerationId = Guid.NewGuid().ToString("N"), LegacyTalkAliases = aliases, EligibilityOverrides = eligibility, DeliveryCorrections = corrections });
    }

    public static CatalogSnapshot ApplyOverrides(CatalogSnapshot snapshot)
    {
        var corrections = snapshot.DeliveryCorrections.ToDictionary(x => x.DeliveryId, StringComparer.Ordinal);
        var sourceDeliveries = snapshot.SourceDeliveries.Count > 0 ? snapshot.SourceDeliveries : snapshot.Deliveries;
        var deliveries = sourceDeliveries.Where(d => !corrections.TryGetValue(d.DeliveryId, out var c) || c.AliasOfDeliveryId is null)
            .Select(d => corrections.TryGetValue(d.DeliveryId, out var c) ? d with
            {
                Date = c.DatePrecision == "unknown" ? null : c.Date ?? d.Date, DatePrecision = c.DatePrecision ?? d.DatePrecision, EventSeriesId = c.EventSeriesId ?? d.EventSeriesId,
                Status = c.Status ?? d.Status, City = c.City ?? d.City, NeedsReview = false
            } : d).ToArray();
        return snapshot with
        {
            Deliveries = deliveries,
            SourceDeliveries = sourceDeliveries,
            Talks = snapshot.Talks.Select(t => t.Status.Equals("retired", StringComparison.OrdinalIgnoreCase) ? t with { Eligible = false } : snapshot.EligibilityOverrides.TryGetValue(t.TalkId, out var eligible) ? t with { Eligible = eligible } : t).ToArray()
        };
    }
}

public interface ICatalogApiClient
{
    Task<CatalogSnapshot> GetCatalogAsync(CancellationToken ct = default);
    Task<bool> EnqueueCatalogSyncAsync(CatalogSyncRequest request, CancellationToken ct = default);
    Task<CatalogSyncJob?> ClaimCatalogSyncAsync(CancellationToken ct = default);
    Task CompleteCatalogSyncAsync(string jobId, CatalogJobCompletion request, CancellationToken ct = default);
    Task PublishCatalogAsync(CatalogPublishRequest request, CancellationToken ct = default);
    Task<CatalogExtraction?> GetCatalogExtractionAsync(string hash, CancellationToken ct = default);
    Task SaveCatalogExtractionAsync(CatalogExtraction extraction, CancellationToken ct = default);
    Task AcceptCatalogWebhookAsync(CatalogWebhookRequest request, CancellationToken ct = default);
    Task<CatalogSnapshot> ReviewCatalogAsync(CatalogReviewRequest request, CancellationToken ct = default);
}

public interface ICatalogRepository
{
    Task<CatalogSnapshot> GetAsync(CancellationToken ct = default);
    async Task<CatalogSnapshot?> GetGenerationAsync(string generationId, CancellationToken ct = default)
    {
        var current = await GetAsync(ct);
        return current.GenerationId == generationId ? current : null;
    }
    Task<bool> EnqueueAsync(CatalogSyncRequest request, CancellationToken ct = default);
    Task<CatalogSyncJob?> ClaimAsync(CancellationToken ct = default);
    Task CompleteAsync(string id, CatalogJobCompletion request, CancellationToken ct = default);
    Task PublishAsync(CatalogPublishRequest request, CancellationToken ct = default);
    Task<CatalogExtraction?> GetExtractionAsync(string hash, CancellationToken ct = default);
    Task SaveExtractionAsync(CatalogExtraction extraction, CancellationToken ct = default);
}
