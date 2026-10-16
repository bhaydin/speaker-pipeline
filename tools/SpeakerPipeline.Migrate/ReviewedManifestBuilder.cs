using SpeakerPipeline.Core;

namespace SpeakerPipeline.Migrate;

/// <summary>Explicit operator evidence supplements, rather than weakens, automatic fingerprints.</summary>
internal sealed record ReviewedDecision
{
    public required string Table { get; init; }
    public required string PartitionKey { get; init; }
    public required string RowKey { get; init; }
    public required string Disposition { get; init; }
    public required string Reason { get; init; }
    public List<string> PropertiesToRemove { get; init; } = [];
}

internal static class ReviewedManifestBuilder
{
    public static CleanupManifest Build(SnapshotFile export, Uri api, string reviewedDecisionsPath)
        => Build(export, api, PrivateFiles.Read<List<ReviewedDecision>>(reviewedDecisionsPath));

    internal static CleanupManifest Build(SnapshotFile export, Uri api, IReadOnlyList<ReviewedDecision> decisions)
    {
        var automatic = ManifestBuilder.Build(export, api);
        var changes = automatic.Plan.Changes.ToList();
        var review = automatic.Review.ToList();
        var seen = new HashSet<(string, string, string)>();
        foreach (var decision in decisions)
        {
            if (!seen.Add((decision.Table, decision.PartitionKey, decision.RowKey)))
                throw new ArgumentException("Duplicate reviewed decision.");
            if (string.IsNullOrWhiteSpace(decision.Reason) || decision.Reason.Trim().Length < 12)
                throw new ArgumentException("Every reviewed decision needs concrete supporting evidence.");
            if (!export.Snapshot.Tables.TryGetValue(decision.Table, out var rows))
                throw new ArgumentException("Unknown reviewed table.");
            var row = rows.SingleOrDefault(r => r.PartitionKey == decision.PartitionKey && r.RowKey == decision.RowKey)
                ?? throw new ArgumentException("Reviewed record is absent from the exact snapshot.");
            if (decision.Disposition is not ("delete" or "retain" or "repair"))
                throw new ArgumentException("Review disposition must be delete, retain, or repair.");
            if (decision.Disposition == "delete" && decision.Table == "Talks" && SampleCatalog.TalkKeys.Contains(row.RowKey))
                throw new ArgumentException("The four adopted production talks must be retained.");
            if (decision.Disposition == "repair" &&
                (decision.Table != "Talks" || decision.PropertiesToRemove.Count != 1 || decision.PropertiesToRemove[0] != "DeckUrl" ||
                 !row.Properties.TryGetValue("DeckUrl", out var value) ||
                 value != new MaintenanceValue(MaintenanceValueType.String, "<placeholder-sharepoint-deck-url>")))
                throw new ArgumentException("Reviewed repair supports only an exact placeholder deck URL removal.");
            if (decision.Disposition != "repair" && decision.PropertiesToRemove.Count != 0)
                throw new ArgumentException("Property removals require a repair disposition.");
            changes.RemoveAll(c => c.Table == decision.Table && c.PartitionKey == decision.PartitionKey && c.RowKey == decision.RowKey);
            review.RemoveAll(r => r.Table == decision.Table && r.PartitionKey == decision.PartitionKey && r.RowKey == decision.RowKey);
            review.Add(new(decision.Table, decision.PartitionKey, decision.RowKey, decision.Disposition, decision.Reason.Trim()));
            if (decision.Disposition != "retain")
                changes.Add(new()
                {
                    Table = decision.Table, PartitionKey = row.PartitionKey, RowKey = row.RowKey, ExpectedETag = row.ETag,
                    Kind = decision.Disposition == "delete" ? MaintenanceChangeKind.Delete : MaintenanceChangeKind.RemoveProperties,
                    PropertiesToRemove = decision.PropertiesToRemove.ToList(),
                });
        }
        // Never produce a plan that would delete a parent while retaining its child.
        foreach (var parent in changes.Where(c => c.Table == "Events" && c.Kind == MaintenanceChangeKind.Delete))
            if (export.Snapshot.Tables["Submissions"].Any(s => s.PartitionKey == parent.RowKey &&
                !changes.Any(c => c.Table == "Submissions" && c.PartitionKey == s.PartitionKey && c.RowKey == s.RowKey && c.Kind == MaintenanceChangeKind.Delete)))
                throw new ArgumentException("Reviewed event deletion retains a related submission. Review both records explicitly.");
        var manifest = automatic with
        {
            Plan = automatic.Plan with { Changes = changes, Purpose = "Production cleanup with exact per-record operator evidence; all unreviewed records retain their conservative classification." },
            Review = review.OrderBy(r => r.Table, StringComparer.Ordinal).ThenBy(r => r.PartitionKey, StringComparer.Ordinal).ThenBy(r => r.RowKey, StringComparer.Ordinal).ToList(),
        };
        return manifest with { Checksum = manifest.CalculateChecksum() };
    }
}
