using SpeakerPipeline.Core;

namespace SpeakerPipeline.Migrate;

/// <summary>
/// DTOs mirroring the storage-entity JSON shape used in <c>samples/</c>
/// (PartitionKey/RowKey + string enums + comma-joined lanes), plus mapping to
/// the Core record shape the API accepts. This mapping is the tool's reason to
/// exist: convert an external export into API calls.
/// </summary>
internal static class SeedMapping
{
    public static EventRecord ToRecord(EventSeed s) => new()
    {
        Slug = s.RowKey,
        Name = s.Name,
        EventType = ParseEnum(s.EventType, EventType.Conference),
        Category = ParseEnum(s.Category, EventCategory.Monitor),
        Priority = ParseEnum(s.Priority, Priority.NA),
        FocusFit = ParseLanes(s.FocusFit),
        StatusDetail = s.StatusDetail,
        EventDateStart = s.EventDateStart,
        EventDateEnd = s.EventDateEnd,
        CfpDeadline = s.CfpDeadline,
        CfpUrl = s.CfpUrl,
        EventUrl = s.EventUrl,
        Location = s.Location,
        Format = ParseNullableEnum<EventFormat>(s.Format),
        TravelBurden = ParseNullableEnum<TravelBurden>(s.TravelBurden),
        NextAction = s.NextAction,
        Notes = s.Notes,
        SourceSeenOn = ParseNullableEnum<SourceSeenOn>(s.SourceSeenOn),
        LastVerifiedUtc = s.LastVerifiedUtc,
        DoNotResurface = s.DoNotResurface ?? false,
        DiscoveredByAgent = s.DiscoveredByAgent,
        DecidedByAgent = s.DecidedByAgent,
        SchemaVersion = s.SchemaVersion ?? 1,
    };

    public static SubmissionRecord ToRecord(SubmissionSeed s) => new()
    {
        EventSlug = s.PartitionKey,
        SubmissionId = s.RowKey,
        EventName = s.EventName,
        TalkSlug = s.TalkSlug,
        TalkTitleUsed = s.TalkTitleUsed,
        AbstractUsed = s.AbstractUsed,
        SubmittedOnUtc = s.SubmittedOnUtc,
        Status = ParseEnum(s.Status, SubmissionStatus.Submitted),
        DecisionReceivedUtc = s.DecisionReceivedUtc,
        ContactPerson = s.ContactPerson,
        ContactEmail = s.ContactEmail,
        FollowUpNeededBy = s.FollowUpNeededBy,
        Notes = s.Notes,
        SchemaVersion = s.SchemaVersion ?? 1,
    };

    public static TalkRecord ToRecord(TalkSeed s) => new()
    {
        Slug = s.RowKey,
        CanonicalTitle = s.CanonicalTitle,
        Lane = ParseEnum(s.Lane, Lane.AgentOps),
        AbstractShort = s.AbstractShort,
        AbstractLong = s.AbstractLong,
        BioVariant = s.BioVariant,
        LengthMinutes = s.LengthMinutes,
        Format = ParseNullableEnum<TalkFormat>(s.Format),
        DeckUrl = s.DeckUrl,
        LastDeliveredUtc = s.LastDeliveredUtc,
        DeliveryCount = s.DeliveryCount ?? 0,
        ReusabilityScore = s.ReusabilityScore,
        Retired = s.Retired ?? false,
        SchemaVersion = s.SchemaVersion ?? 1,
    };

    private static T ParseEnum<T>(string? value, T fallback) where T : struct, Enum
        => Enum.TryParse<T>(value, ignoreCase: true, out var v) ? v : fallback;

    private static T? ParseNullableEnum<T>(string? value) where T : struct, Enum
        => Enum.TryParse<T>(value, ignoreCase: true, out var v) ? v : null;

    private static IReadOnlyList<Lane> ParseLanes(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => ParseNullableEnum<Lane>(p))
                .Where(l => l.HasValue)
                .Select(l => l!.Value)];
}

internal sealed class EventSeed
{
    public string RowKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string? EventType { get; set; }
    public string? Category { get; set; }
    public string? Priority { get; set; }
    public string? FocusFit { get; set; }
    public string? StatusDetail { get; set; }
    public DateTimeOffset? EventDateStart { get; set; }
    public DateTimeOffset? EventDateEnd { get; set; }
    public DateTimeOffset? CfpDeadline { get; set; }
    public string? CfpUrl { get; set; }
    public string? EventUrl { get; set; }
    public string? Location { get; set; }
    public string? Format { get; set; }
    public string? TravelBurden { get; set; }
    public string? NextAction { get; set; }
    public string? Notes { get; set; }
    public string? SourceSeenOn { get; set; }
    public DateTimeOffset? LastVerifiedUtc { get; set; }
    public bool? DoNotResurface { get; set; }
    public string? DiscoveredByAgent { get; set; }
    public string? DecidedByAgent { get; set; }
    public int? SchemaVersion { get; set; }
}

internal sealed class SubmissionSeed
{
    public string PartitionKey { get; set; } = "";
    public string RowKey { get; set; } = "";
    public string EventName { get; set; } = "";
    public string TalkSlug { get; set; } = "";
    public string TalkTitleUsed { get; set; } = "";
    public string AbstractUsed { get; set; } = "";
    public DateTimeOffset SubmittedOnUtc { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset? DecisionReceivedUtc { get; set; }
    public string? ContactPerson { get; set; }
    public string? ContactEmail { get; set; }
    public DateTimeOffset? FollowUpNeededBy { get; set; }
    public string? Notes { get; set; }
    public int? SchemaVersion { get; set; }
}

internal sealed class TalkSeed
{
    public string RowKey { get; set; } = "";
    public string CanonicalTitle { get; set; } = "";
    public string? Lane { get; set; }
    public string? AbstractShort { get; set; }
    public string? AbstractLong { get; set; }
    public string? BioVariant { get; set; }
    public int? LengthMinutes { get; set; }
    public string? Format { get; set; }
    public string? DeckUrl { get; set; }
    public DateTimeOffset? LastDeliveredUtc { get; set; }
    public int? DeliveryCount { get; set; }
    public int? ReusabilityScore { get; set; }
    public bool? Retired { get; set; }
    public int? SchemaVersion { get; set; }
}
