using System.Globalization;
using Azure.Data.Tables;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Storage;

/// <summary>
/// Hand-rolled mapping between domain records and TableEntity. We avoid
/// ITableEntity-decorated POCOs so the Core project stays free of any
/// Azure SDK dependency.
/// </summary>
internal static class Mapping
{
    private const string EventsPartition = "events";
    private const string TalksPartition = "talks";

    // ---- Events ----------------------------------------------------------

    public static TableEntity ToEntity(EventRecord r)
    {
        if (!SlugSanitizer.IsValid(r.Slug))
        {
            throw new ArgumentException($"Invalid slug '{r.Slug}' — forbidden character in Table Storage key.", nameof(r));
        }

        var e = new TableEntity(EventsPartition, r.Slug)
        {
            ["Name"] = r.Name,
            ["EventType"] = r.EventType.ToString(),
            ["Category"] = r.Category.ToString(),
            ["Priority"] = r.Priority.ToString(),
            ["FocusFit"] = string.Join(",", r.FocusFit.Select(l => l.ToString())),
            ["StatusDetail"] = r.StatusDetail,
            ["EventDateStart"] = r.EventDateStart?.UtcDateTime,
            ["EventDateEnd"] = r.EventDateEnd?.UtcDateTime,
            ["CfpDeadline"] = r.CfpDeadline?.UtcDateTime,
            ["CfpUrl"] = r.CfpUrl,
            ["EventUrl"] = r.EventUrl,
            ["Location"] = r.Location,
            ["Format"] = r.Format?.ToString(),
            ["TravelBurden"] = r.TravelBurden?.ToString(),
            ["NextAction"] = r.NextAction,
            ["Notes"] = r.Notes,
            ["SourceSeenOn"] = r.SourceSeenOn?.ToString(),
            ["LastVerifiedUtc"] = r.LastVerifiedUtc?.UtcDateTime,
            ["DoNotResurface"] = r.DoNotResurface,
            ["DiscoveredByAgent"] = r.DiscoveredByAgent,
            ["DecidedByAgent"] = r.DecidedByAgent,
            ["SchemaVersion"] = r.SchemaVersion,
        };
        return e;
    }

    public static EventRecord ToEventRecord(TableEntity e) => new()
    {
        Slug = e.RowKey,
        Name = e.GetString("Name") ?? string.Empty,
        EventType = ParseEnum<EventType>(e.GetString("EventType")) ?? EventType.Conference,
        Category = ParseEnum<EventCategory>(e.GetString("Category")) ?? EventCategory.Monitor,
        Priority = ParseEnum<Priority>(e.GetString("Priority")) ?? Priority.NA,
        FocusFit = ParseLaneList(e.GetString("FocusFit")),
        StatusDetail = e.GetString("StatusDetail"),
        EventDateStart = e.GetDateTimeOffset("EventDateStart"),
        EventDateEnd = e.GetDateTimeOffset("EventDateEnd"),
        CfpDeadline = e.GetDateTimeOffset("CfpDeadline"),
        CfpUrl = e.GetString("CfpUrl"),
        EventUrl = e.GetString("EventUrl"),
        Location = e.GetString("Location"),
        Format = ParseEnum<EventFormat>(e.GetString("Format")),
        TravelBurden = ParseEnum<TravelBurden>(e.GetString("TravelBurden")),
        NextAction = e.GetString("NextAction"),
        Notes = e.GetString("Notes"),
        SourceSeenOn = ParseEnum<SourceSeenOn>(e.GetString("SourceSeenOn")),
        LastVerifiedUtc = e.GetDateTimeOffset("LastVerifiedUtc"),
        DoNotResurface = e.GetBoolean("DoNotResurface") ?? false,
        DiscoveredByAgent = e.GetString("DiscoveredByAgent"),
        DecidedByAgent = e.GetString("DecidedByAgent"),
        SchemaVersion = e.GetInt32("SchemaVersion") ?? 1,
    };

    // ---- Submissions -----------------------------------------------------

    public static TableEntity ToEntity(SubmissionRecord r)
    {
        if (!SlugSanitizer.IsValid(r.EventSlug))
        {
            throw new ArgumentException($"Invalid EventSlug '{r.EventSlug}'.", nameof(r));
        }

        if (!SlugSanitizer.IsValid(r.SubmissionId))
        {
            throw new ArgumentException($"Invalid SubmissionId '{r.SubmissionId}'.", nameof(r));
        }

        return new TableEntity(r.EventSlug, r.SubmissionId)
        {
            ["EventName"] = r.EventName,
            ["TalkSlug"] = r.TalkSlug,
            ["TalkTitleUsed"] = r.TalkTitleUsed,
            ["AbstractUsed"] = r.AbstractUsed,
            ["SubmittedOnUtc"] = r.SubmittedOnUtc.UtcDateTime,
            ["Status"] = r.Status.ToString(),
            ["DecisionReceivedUtc"] = r.DecisionReceivedUtc?.UtcDateTime,
            ["ContactPerson"] = r.ContactPerson,
            ["ContactEmail"] = r.ContactEmail,
            ["FollowUpNeededBy"] = r.FollowUpNeededBy?.UtcDateTime,
            ["Notes"] = r.Notes,
            ["SchemaVersion"] = r.SchemaVersion,
        };
    }

    public static SubmissionRecord ToSubmissionRecord(TableEntity e) => new()
    {
        EventSlug = e.PartitionKey,
        SubmissionId = e.RowKey,
        EventName = e.GetString("EventName") ?? string.Empty,
        TalkSlug = e.GetString("TalkSlug") ?? string.Empty,
        TalkTitleUsed = e.GetString("TalkTitleUsed") ?? string.Empty,
        AbstractUsed = e.GetString("AbstractUsed") ?? string.Empty,
        SubmittedOnUtc = e.GetDateTimeOffset("SubmittedOnUtc") ?? DateTimeOffset.MinValue,
        Status = ParseEnum<SubmissionStatus>(e.GetString("Status")) ?? SubmissionStatus.Submitted,
        DecisionReceivedUtc = e.GetDateTimeOffset("DecisionReceivedUtc"),
        ContactPerson = e.GetString("ContactPerson"),
        ContactEmail = e.GetString("ContactEmail"),
        FollowUpNeededBy = e.GetDateTimeOffset("FollowUpNeededBy"),
        Notes = e.GetString("Notes"),
        SchemaVersion = e.GetInt32("SchemaVersion") ?? 1,
    };

    // ---- Talks -----------------------------------------------------------

    public static TableEntity ToEntity(TalkRecord r)
    {
        if (!SlugSanitizer.IsValid(r.Slug))
        {
            throw new ArgumentException($"Invalid slug '{r.Slug}'.", nameof(r));
        }

        return new TableEntity(TalksPartition, r.Slug)
        {
            ["CanonicalTitle"] = r.CanonicalTitle,
            ["Lane"] = r.Lane.ToString(),
            ["AbstractShort"] = r.AbstractShort,
            ["AbstractLong"] = r.AbstractLong,
            ["BioVariant"] = r.BioVariant,
            ["LengthMinutes"] = r.LengthMinutes,
            ["Format"] = r.Format?.ToString(),
            ["DeckUrl"] = r.DeckUrl,
            ["LastDeliveredUtc"] = r.LastDeliveredUtc?.UtcDateTime,
            ["DeliveryCount"] = r.DeliveryCount,
            ["ReusabilityScore"] = r.ReusabilityScore,
            ["Retired"] = r.Retired,
            ["SchemaVersion"] = r.SchemaVersion,
        };
    }

    public static TalkRecord ToTalkRecord(TableEntity e) => new()
    {
        Slug = e.RowKey,
        CanonicalTitle = e.GetString("CanonicalTitle") ?? string.Empty,
        Lane = ParseEnum<Lane>(e.GetString("Lane")) ?? Lane.AgentOps,
        AbstractShort = e.GetString("AbstractShort"),
        AbstractLong = e.GetString("AbstractLong"),
        BioVariant = e.GetString("BioVariant"),
        LengthMinutes = e.GetInt32("LengthMinutes"),
        Format = ParseEnum<TalkFormat>(e.GetString("Format")),
        DeckUrl = e.GetString("DeckUrl"),
        LastDeliveredUtc = e.GetDateTimeOffset("LastDeliveredUtc"),
        DeliveryCount = e.GetInt32("DeliveryCount") ?? 0,
        ReusabilityScore = e.GetInt32("ReusabilityScore"),
        Retired = e.GetBoolean("Retired") ?? false,
        SchemaVersion = e.GetInt32("SchemaVersion") ?? 1,
    };

    // ---- Helpers ---------------------------------------------------------

    public static string EventsPartitionKey => EventsPartition;
    public static string TalksPartitionKey => TalksPartition;

    private static T? ParseEnum<T>(string? value) where T : struct, Enum
        => string.IsNullOrEmpty(value)
            ? null
            : Enum.TryParse<T>(value, ignoreCase: true, out var result) ? result : null;

    private static IReadOnlyList<Lane> ParseLaneList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return [.. value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => ParseEnum<Lane>(part))
            .Where(l => l.HasValue)
            .Select(l => l!.Value)];
    }

    public static string FormatOdataDateTime(DateTimeOffset dt)
        => dt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
}
