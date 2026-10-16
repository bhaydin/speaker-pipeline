using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Migrate;

internal sealed record SnapshotFile(string ApiOrigin, MaintenanceSnapshot Snapshot);
internal sealed record ReviewItem(string Table, string PartitionKey, string RowKey, string Disposition, string Reason);
internal sealed record CleanupManifest(string ApiOrigin, string SnapshotChecksum, MaintenancePlan Plan, List<ReviewItem> Review, string Checksum = "")
{
    public string CalculateChecksum() => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        ApiOrigin, SnapshotChecksum, PlanChecksum = MaintenanceIntegrity.PlanChecksum(Plan), Review,
    })));

    public void Validate(Uri api, SnapshotFile export)
    {
        ManifestBuilder.ValidateSnapshot(export, api);
        if (ApiOrigin != api.AbsoluteUri || SnapshotChecksum != export.Snapshot.Checksum ||
            Plan.Snapshot.Checksum != export.Snapshot.Checksum || MaintenanceIntegrity.SnapshotChecksum(Plan.Snapshot) != export.Snapshot.Checksum || Checksum != CalculateChecksum())
            throw new ArgumentException("Manifest target, snapshot, or checksum does not match. Generate a fresh preview; do not edit manifest JSON.");
    }
}

internal static class ManifestBuilder
{
    public static void ValidateSnapshot(SnapshotFile export, Uri api)
    {
        if (export.ApiOrigin != api.AbsoluteUri || export.Snapshot.Version != 1 ||
            export.Snapshot.Checksum != MaintenanceIntegrity.SnapshotChecksum(export.Snapshot))
            throw new ArgumentException("Snapshot target, version, or checksum does not match.");
        if (!MaintenanceIntegrity.TableNames.Order().SequenceEqual(export.Snapshot.Tables.Keys.Order()) ||
            !MaintenanceIntegrity.TableNames.Order().SequenceEqual(export.Snapshot.Target.TableNames.Keys.Order()))
            throw new ArgumentException("Snapshot must include all six tables and their physical names.");
        foreach (var table in export.Snapshot.Tables)
            if (table.Value.Select(r => (r.PartitionKey, r.RowKey)).Distinct().Count() != table.Value.Count)
                throw new ArgumentException("Snapshot contains duplicate entity keys.");
    }

    public static CleanupManifest Build(SnapshotFile export, Uri api)
    {
        ValidateSnapshot(export, api);
        var snapshot = export.Snapshot;
        var changes = new List<MaintenanceChange>();
        var review = new List<ReviewItem>();
        var submissions = snapshot.Tables["Submissions"];
        var removableSubmissions = submissions.Where(s => SampleCatalog.Matches("Submissions", s)).ToList();
        foreach (var table in snapshot.Tables.OrderBy(t => t.Key, StringComparer.Ordinal))
        foreach (var row in table.Value.OrderBy(r => r.PartitionKey, StringComparer.Ordinal).ThenBy(r => r.RowKey, StringComparer.Ordinal))
        {
            var exactSample = SampleCatalog.Matches(table.Key, row);
            if ((table.Key == "Submissions" && exactSample) ||
                (table.Key == "Events" && exactSample && !submissions.Any(s => s.PartitionKey == row.RowKey && !removableSubmissions.Contains(s))))
            {
                changes.Add(Change(table.Key, row, MaintenanceChangeKind.Delete));
                review.Add(new(table.Key, row.PartitionKey, row.RowKey, "delete", "Exact shipped sample fingerprint, including every stored property."));
            }
            else if (table.Key == "Talks" && row.PartitionKey == "talks" && SampleCatalog.TalkKeys.Contains(row.RowKey) &&
                row.Properties.TryGetValue("DeckUrl", out var deck) && deck == new MaintenanceValue(MaintenanceValueType.String, "<placeholder-sharepoint-deck-url>"))
            {
                changes.Add(Change(table.Key, row, MaintenanceChangeKind.RemoveProperties) with { PropertiesToRemove = ["DeckUrl"] });
                review.Add(new(table.Key, row.PartitionKey, row.RowKey, "keep-and-clear-placeholder", "Retain the canonical talk and every other property; remove only the exact placeholder DeckUrl."));
            }
            else
            {
                var reason = table.Key switch
                {
                    "Talks" => "Retain canonical/other talk content; no approved placeholder deck removal.",
                    "NotificationLog" => "Retain notification history and deduplication state; review sample-linked records separately.",
                    "Events" when SampleCatalog.EventKeys.Contains(row.RowKey) => "Known sample key has changed data or a retained submission; requires review before any deletion.",
                    "Submissions" when SampleCatalog.EventKeys.Contains(row.PartitionKey) => "Sample-linked submission does not exactly match the shipped fingerprint; retain for review.",
                    _ => "No exact approved sample fingerprint; retain for review.",
                };
                review.Add(new(table.Key, row.PartitionKey, row.RowKey, "retain-review", reason));
            }
        }
        var plan = new MaintenancePlan
        {
            OperationId = Guid.NewGuid().ToString("n"), Snapshot = snapshot, Changes = changes,
            Purpose = "Production cleanup: exact fictional samples and canonical-talk placeholder deck links only.",
        };
        var manifest = new CleanupManifest(api.AbsoluteUri, snapshot.Checksum, plan, review);
        return manifest with { Checksum = manifest.CalculateChecksum() };
    }

    private static MaintenanceChange Change(string table, MaintenanceEntity row, MaintenanceChangeKind kind)
        => new() { Table = table, PartitionKey = row.PartitionKey, RowKey = row.RowKey, ExpectedETag = row.ETag, Kind = kind };
}

/// <summary>The embedded, fictional fixtures are the sole automatic deletion evidence.</summary>
internal static class SampleCatalog
{
    private static List<T> Read<T>(string name)
    {
        using var stream = typeof(SampleCatalog).Assembly.GetManifestResourceStream($"Samples.{name}.json")!;
        return JsonSerializer.Deserialize<List<T>>(stream, PrivateFiles.Json)!;
    }

    private static readonly Dictionary<string, MaintenanceEntity> EventRows = Read<EventSeed>("events")
        .Select(SeedMapping.ToRecord).Select(r => FromRecord("events", r.Slug, r, "Slug")).ToDictionary(r => r.RowKey, StringComparer.Ordinal);
    private static readonly Dictionary<(string, string), MaintenanceEntity> SubmissionRows = Read<SubmissionSeed>("submissions")
        .Select(SeedMapping.ToRecord).Select(r => FromRecord(r.EventSlug, r.SubmissionId, r, "EventSlug", "SubmissionId"))
        .ToDictionary(r => (r.PartitionKey, r.RowKey));
    public static readonly HashSet<string> EventKeys = EventRows.Keys.ToHashSet(StringComparer.Ordinal);
    public static readonly HashSet<string> TalkKeys = Read<TalkSeed>("talks").Select(t => t.RowKey).ToHashSet(StringComparer.Ordinal);

    public static bool Matches(string table, MaintenanceEntity row)
    {
        MaintenanceEntity? expected = null;
        if (table == "Events" && row.PartitionKey == "events") EventRows.TryGetValue(row.RowKey, out expected);
        if (table == "Submissions") SubmissionRows.TryGetValue((row.PartitionKey, row.RowKey), out expected);
        if (expected is null) return false;
        var properties = new Dictionary<string, MaintenanceValue>(row.Properties, StringComparer.Ordinal);
        // Flags introduced after the original seed are equivalent only when absent/false.
        if (table == "Events")
            foreach (var field in new[] { "FamilyConflictFlag", "PrepConflictFlag" })
                properties.TryAdd(field, new(MaintenanceValueType.Boolean, "false"));
        return properties.Count == expected.Properties.Count && properties.All(p =>
            expected.Properties.TryGetValue(p.Key, out var value) && Equivalent(value, p.Value));
    }

    internal static MaintenanceEntity FromRecord(string partition, string key, object record, params string[] keyProperties)
    {
        var properties = new Dictionary<string, MaintenanceValue>(StringComparer.Ordinal);
        foreach (var prop in record.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (keyProperties.Contains(prop.Name)) continue;
            var value = prop.GetValue(record);
            if (value is null) continue;
            properties.Add(prop.Name, value switch
            {
                string text => new(MaintenanceValueType.String, text),
                Enum enumeration => new(MaintenanceValueType.String, enumeration.ToString()),
                bool boolean => new(MaintenanceValueType.Boolean, boolean ? "true" : "false"),
                int number => new(MaintenanceValueType.Int32, number.ToString(CultureInfo.InvariantCulture)),
                DateTimeOffset date => new(MaintenanceValueType.DateTime, date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
                IReadOnlyList<Lane> lanes => new(MaintenanceValueType.String, string.Join(',', lanes)),
                _ => throw new InvalidOperationException("Unsupported sample property type."),
            });
        }
        return new() { PartitionKey = partition, RowKey = key, ETag = "sample", Properties = properties };
    }

    private static bool Equivalent(MaintenanceValue left, MaintenanceValue right)
        => left.Type == right.Type && (left.Type switch
        {
            MaintenanceValueType.DateTime => DateTimeOffset.TryParse(left.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var l) &&
                DateTimeOffset.TryParse(right.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var r) && l == r,
            MaintenanceValueType.Boolean => bool.TryParse(left.Value, out var l) && bool.TryParse(right.Value, out var r) && l == r,
            _ => left.Value == right.Value,
        });
}
