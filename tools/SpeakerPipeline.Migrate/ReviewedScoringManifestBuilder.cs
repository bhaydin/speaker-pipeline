using System.Text.Json;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Migrate;

/// <summary>Explicit operator approval of concrete shadow output, never inferred from an agent stamp.</summary>
internal sealed record ReviewedScoringDecision
{
    public required string EventSlug { get; init; }
    public required string Category { get; init; }
    public required string Priority { get; init; }
    public required string NextAction { get; init; }
    public required string DecidedByAgent { get; init; }
    public required string Reason { get; init; }
    public required string OutputProofSha256 { get; init; }
}

internal static class ReviewedScoringManifestBuilder
{
    public static CleanupManifest Build(SnapshotFile export, Uri api, string decisionsPath)
        => Build(export, api, PrivateFiles.Read<List<ReviewedScoringDecision>>(decisionsPath));

    internal static CleanupManifest Build(SnapshotFile export, Uri api, IReadOnlyList<ReviewedScoringDecision> decisions)
    {
        ManifestBuilder.ValidateSnapshot(export, api);
        if (!export.Snapshot.WritersFrozen)
            throw new ArgumentException("Reviewed scoring repair requires a frozen snapshot.");
        if (decisions.Count == 0 || decisions.Select(d => d.EventSlug).Distinct(StringComparer.Ordinal).Count() != decisions.Count)
            throw new ArgumentException("Reviewed scoring decisions must be nonempty and name each event only once.");

        var changes = new List<MaintenanceChange>();
        var review = new List<ReviewItem>();
        foreach (var decision in decisions.OrderBy(d => d.EventSlug, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(decision.Reason) || decision.Reason.Trim().Length < 12 ||
                decision.OutputProofSha256 is not { Length: 64 } || !decision.OutputProofSha256.All(Uri.IsHexDigit))
                throw new ArgumentException("Each reviewed decision requires concrete approval evidence and the SHA256 of its private shadow output.");
            var row = export.Snapshot.Tables["Events"].SingleOrDefault(r => r.PartitionKey == "events" && r.RowKey == decision.EventSlug)
                ?? throw new ArgumentException("Reviewed scoring event is absent from the exact snapshot.");
            if (string.IsNullOrWhiteSpace(row.ETag) || row.ETag == "*")
                throw new ArgumentException("Reviewed scoring event requires its exact snapshot ETag.");
            // Provenance cannot establish that a person never edited the decision afterwards.
            // The review reason must resolve that uncertainty; this gate alone grants no approval.
            if (Text(row, "Category") is not ("Monitor" or "SubmitNow" or "Outreach") ||
                !ScoringAgent(Text(row, "DecidedByAgent")) ||
                (row.Properties.TryGetValue("DoNotResurface", out var suppressed) && suppressed != new MaintenanceValue(MaintenanceValueType.Boolean, "false")) ||
                export.Snapshot.Tables["Submissions"].Any(s => s.PartitionKey == row.RowKey))
                throw new ArgumentException("Reviewed scoring repair requires an agent-marked candidate without suppression or submissions.");
            var coherent = decision.Category switch
            {
                "SubmitNow" => decision.Priority is "High" or "MediumHigh" or "Medium" or "Low",
                "Outreach" => decision.Priority == "Medium",
                "Monitor" => decision.Priority == "Low",
                "Pass" => decision.Priority == "NA",
                _ => false,
            };
            if (!coherent || string.IsNullOrWhiteSpace(decision.NextAction) || decision.NextAction.Length > 240 ||
                !ScoringAgent(decision.DecidedByAgent))
                throw new ArgumentException("Reviewed scoring fields must match supported category/priority pairs, a 1–240 character rationale, and scoring-agent provenance.");
            var properties = new Dictionary<string, MaintenanceValue>(StringComparer.Ordinal)
            {
                ["Category"] = new(MaintenanceValueType.String, decision.Category),
                ["Priority"] = new(MaintenanceValueType.String, decision.Priority),
                ["NextAction"] = new(MaintenanceValueType.String, decision.NextAction),
                ["DecidedByAgent"] = new(MaintenanceValueType.String, decision.DecidedByAgent),
            };
            if (properties.All(p => row.Properties.TryGetValue(p.Key, out var old) && old == p.Value))
                throw new ArgumentException("Reviewed scoring repair must change at least one approved field.");
            changes.Add(new()
            {
                Table = "Events", PartitionKey = "events", RowKey = row.RowKey, ExpectedETag = row.ETag,
                Kind = MaintenanceChangeKind.ApplyScoringDecision, PropertiesToSet = properties,
            });
            review.Add(new("Events", "events", row.RowKey, "reviewed-scoring-decision",
                $"{decision.Reason.Trim()} Shadow output SHA256: {decision.OutputProofSha256.ToLowerInvariant()}"));
        }

        // Bind approval evidence into the existing journal plan hash, without altering its protocol.
        var evidence = decisions.OrderBy(d => d.EventSlug, StringComparer.Ordinal).Select(d => new
        {
            d.EventSlug, Reason = d.Reason.Trim(), OutputProofSha256 = d.OutputProofSha256.ToLowerInvariant(),
        });
        var plan = new MaintenancePlan
        {
            OperationId = Guid.NewGuid().ToString("n"), Snapshot = export.Snapshot, Changes = changes,
            Purpose = "Explicitly reviewed scoring repair; four fields only. Approval evidence: " + JsonSerializer.Serialize(evidence),
        };
        var manifest = new CleanupManifest(api.AbsoluteUri, export.Snapshot.Checksum, plan, review);
        return manifest with { Checksum = manifest.CalculateChecksum() };
    }

    private static string? Text(MaintenanceEntity row, string name)
        => row.Properties.TryGetValue(name, out var value) && value.Type == MaintenanceValueType.String ? value.Value : null;

    private static bool ScoringAgent(string? value)
        => value is { Length: > 14 } && value.StartsWith("scoring-agent-", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(value[14..]);
}
