using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SpeakerPipeline.Agents.Scoring;
using SpeakerPipeline.Agents.TrackerMaintenance;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Migrate;

internal sealed record ConflictImpact(string EventSlug, bool PreviousFamily, bool PreviousPrep, bool RecomputedFamily, bool RecomputedPrep, string Disposition);
internal sealed record ScoringImpact(string EventSlug, EventCategory Category, string ReviewStatus, string Reason,
    string BeforePromptSha256, string AfterPromptSha256, string BeforePrompt, string AfterPrompt);
internal sealed record RecomputeReport(string ApiOrigin, string BeforeSnapshotChecksum, string AfterSnapshotChecksum,
    DateTimeOffset AtUtc, int PrepWindowDays, int PrepThreshold, PipelineContext BeforeContext, PipelineContext AfterContext,
    List<ConflictImpact> ConflictChanges, List<ScoringImpact> ScoringChanges, string RepairsManifestChecksum, string Checksum = "")
{
    public string CalculateChecksum() => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this with { Checksum = "" })));
}

internal static class RecomputePreview
{
    public static (RecomputeReport Report, CleanupManifest Repairs) Build(SnapshotFile beforeFile, SnapshotFile afterFile, Uri api,
        DateTimeOffset at, int prepWindowDays, int prepThreshold)
    {
        ManifestBuilder.ValidateSnapshot(beforeFile, api);
        ManifestBuilder.ValidateSnapshot(afterFile, api);
        if (!beforeFile.Snapshot.WritersFrozen || !afterFile.Snapshot.WritersFrozen)
            throw new ArgumentException("Recompute requires frozen before/after snapshots.");
        if (prepWindowDays < 0 || prepThreshold < 1) throw new ArgumentException("Invalid deployed conflict settings.");
        ValidateCleanupOnly(beforeFile.Snapshot, afterFile.Snapshot);
        var before = Project(beforeFile.Snapshot);
        var after = Project(afterFile.Snapshot);
        var beforeContext = PipelineContextAssembler.Assemble(before.Events, before.Submissions, before.Talks, before.Blackouts, at);
        var afterContext = PipelineContextAssembler.Assemble(after.Events, after.Submissions, after.Talks, after.Blackouts, at);
        var conflicts = new List<ConflictImpact>();
        var scores = new List<ScoringImpact>();
        var changes = new List<MaintenanceChange>();
        var review = new List<ReviewItem>();
        var beforeBySlug = before.Events.ToDictionary(e => e.Slug, StringComparer.Ordinal);
        foreach (var item in after.Events.OrderBy(e => e.Slug, StringComparer.Ordinal))
        {
            var prior = beforeBySlug[item.Slug];
            var priorFlags = ConflictEvaluator.Evaluate(prior, before.Blackouts, before.Events, prepWindowDays, prepThreshold);
            var flags = ConflictEvaluator.Evaluate(item, after.Blackouts, after.Events, prepWindowDays, prepThreshold);
            var dependencyChanged = flags != priorFlags;
            var storedDiffers = flags.Family != item.FamilyConflictFlag || flags.Prep != item.PrepConflictFlag;
            var effective = item;
            if (storedDiffers)
            {
                conflicts.Add(new(item.Slug, item.FamilyConflictFlag, item.PrepConflictFlag, flags.Family, flags.Prep,
                    dependencyChanged ? "proposed-cleanup-derived-repair" : "retain-review-preexisting-drift"));
                if (dependencyChanged)
                {
                    var raw = afterFile.Snapshot.Tables["Events"].Single(r => r.PartitionKey == "events" && r.RowKey == item.Slug);
                    var properties = new Dictionary<string, MaintenanceValue>();
                    if (flags.Family != item.FamilyConflictFlag) properties["FamilyConflictFlag"] = new(MaintenanceValueType.Boolean, flags.Family ? "true" : "false");
                    if (flags.Prep != item.PrepConflictFlag) properties["PrepConflictFlag"] = new(MaintenanceValueType.Boolean, flags.Prep ? "true" : "false");
                    changes.Add(new()
                    {
                        Table = "Events", PartitionKey = "events", RowKey = item.Slug, ExpectedETag = raw.ETag,
                        Kind = MaintenanceChangeKind.SetProperties, PropertiesToSet = properties,
                    });
                    review.Add(new("Events", "events", item.Slug, "proposed-derived-repair", "Cleanup changes the deterministic conflict result; only changed boolean flags are proposed. Decisions and unknown properties stay unchanged."));
                    effective = item with { FamilyConflictFlag = flags.Family, PrepConflictFlag = flags.Prep };
                }
            }
            var oldPrompt = ScoringRubric.BuildUserPrompt(prior, before.Talks, beforeContext);
            var newPrompt = ScoringRubric.BuildUserPrompt(effective, after.Talks, afterContext);
            if (oldPrompt == newPrompt) continue;
            var protectedDecision = item.DoNotResurface || item.Category is EventCategory.Skip or EventCategory.Accepted or EventCategory.Submitted or EventCategory.Delivered or EventCategory.Quarantine ||
                after.Submissions.GetValueOrDefault(item.Slug, []).Count != 0;
            scores.Add(new(item.Slug, item.Category,
                protectedDecision ? "protected-do-not-rescore" : "requires-explicit-operator-review",
                protectedDecision ? "Retained submission, committed/terminal/quarantined state, or DoNotResurface protects this decision." :
                    "The exact prompt changed, but manual decisions are not reliably distinguishable from agent decisions. No scoring or decision writes were performed.",
                Hash(oldPrompt), Hash(newPrompt), oldPrompt, newPrompt));
        }
        var plan = new MaintenancePlan
        {
            OperationId = Guid.NewGuid().ToString("n"), Snapshot = afterFile.Snapshot, Changes = changes,
            Purpose = "Reviewed cleanup-derived conflict flags only; preserve all category, priority, intent, suppression, and provenance decisions.",
        };
        var repairs = new CleanupManifest(api.AbsoluteUri, afterFile.Snapshot.Checksum, plan, review);
        repairs = repairs with { Checksum = repairs.CalculateChecksum() };
        var report = new RecomputeReport(api.AbsoluteUri, beforeFile.Snapshot.Checksum, afterFile.Snapshot.Checksum, at,
            prepWindowDays, prepThreshold, beforeContext, afterContext, conflicts, scores, repairs.Checksum);
        return (report with { Checksum = report.CalculateChecksum() }, repairs);
    }

    private static void ValidateCleanupOnly(MaintenanceSnapshot before, MaintenanceSnapshot after)
    {
        if (before.Target.TableEndpoint != after.Target.TableEndpoint || before.Target.TableNames.Count != after.Target.TableNames.Count ||
            before.Target.TableNames.Any(p => !after.Target.TableNames.TryGetValue(p.Key, out var value) || value != p.Value))
            throw new ArgumentException("Before/after snapshots belong to different storage targets.");
        if (after.CreatedUtc < before.CreatedUtc) throw new ArgumentException("After snapshot predates the before snapshot.");
        foreach (var table in after.Tables)
        {
            var originals = before.Tables[table.Key].ToDictionary(r => (r.PartitionKey, r.RowKey));
            if (table.Key is not ("Events" or "Submissions" or "NotificationLog") && originals.Count != table.Value.Count)
                throw new ArgumentException("Cleanup must retain all talks, topics, and blackouts for this recompute flow.");
            foreach (var row in table.Value)
            {
                if (!originals.TryGetValue((row.PartitionKey, row.RowKey), out var prior))
                    throw new ArgumentException("Rows were added between cleanup snapshots; a separate reviewed analysis is required.");
                if (MaintenanceIntegrity.EntityChecksum(prior) == MaintenanceIntegrity.EntityChecksum(row)) continue;
                var deckOnly = table.Key == "Talks" && row.PartitionKey == "talks" && SampleCatalog.TalkKeys.Contains(row.RowKey) &&
                    prior.Properties.TryGetValue("DeckUrl", out var deck) && deck == new MaintenanceValue(MaintenanceValueType.String, "<placeholder-sharepoint-deck-url>") &&
                    !row.Properties.ContainsKey("DeckUrl") && MaintenanceIntegrity.EntityChecksum(prior with
                    { Properties = prior.Properties.Where(p => p.Key != "DeckUrl").ToDictionary(p => p.Key, p => p.Value) }) == MaintenanceIntegrity.EntityChecksum(row);
                if (!deckOnly) throw new ArgumentException("Retained records changed beyond the exact placeholder deck removal; recompute attribution requires an unchanged baseline.");
            }
        }
    }

    private sealed record Projection(IReadOnlyList<EventRecord> Events, IReadOnlyDictionary<string, IReadOnlyList<SubmissionRecord>> Submissions,
        IReadOnlyList<TalkRecord> Talks, IReadOnlyList<BlackoutRecord> Blackouts);

    private static Projection Project(MaintenanceSnapshot snapshot)
    {
        var events = snapshot.Tables["Events"].Where(r => r.PartitionKey == "events").OrderBy(r => r.RowKey, StringComparer.Ordinal)
            .Select(r => ToRecord<EventRecord>(r, ("Slug", r.RowKey))).ToArray();
        var talks = snapshot.Tables["Talks"].Where(r => r.PartitionKey == "talks").OrderBy(r => r.RowKey, StringComparer.Ordinal)
            .Select(r => ToRecord<TalkRecord>(r, ("Slug", r.RowKey))).ToArray();
        var blackouts = snapshot.Tables["Blackouts"].Where(r => r.PartitionKey == "blackout").OrderBy(r => r.RowKey, StringComparer.Ordinal)
            .Select(r => ToRecord<BlackoutRecord>(r, ("BlackoutId", r.RowKey))).ToArray();
        var submissions = snapshot.Tables["Submissions"].Select(r => ToRecord<SubmissionRecord>(r, ("EventSlug", r.PartitionKey), ("SubmissionId", r.RowKey)))
            .GroupBy(s => s.EventSlug, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (IReadOnlyList<SubmissionRecord>)g.ToArray(), StringComparer.Ordinal);
        return new(events, submissions, talks, blackouts);
    }

    private static T ToRecord<T>(MaintenanceEntity row, params (string Name, string Value)[] keys)
    {
        var known = typeof(T).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var fields = row.Properties.Where(p => known.Contains(p.Key)).ToDictionary(p => p.Key, p => Value(p.Value));
        foreach (var key in keys) fields[key.Name] = key.Value;
        if (fields.TryGetValue("FocusFit", out var lanes) && lanes is string laneText)
            fields["FocusFit"] = laneText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(fields), PrivateFiles.Json) ?? throw new ArgumentException("Cannot project snapshot row for recompute.");
    }

    private static object Value(MaintenanceValue value) => value.Type switch
    {
        MaintenanceValueType.String => value.Value,
        MaintenanceValueType.Boolean => bool.Parse(value.Value),
        MaintenanceValueType.Int32 => int.Parse(value.Value, CultureInfo.InvariantCulture),
        MaintenanceValueType.Int64 => long.Parse(value.Value, CultureInfo.InvariantCulture),
        MaintenanceValueType.Double => double.Parse(value.Value, CultureInfo.InvariantCulture),
        MaintenanceValueType.DateTime => DateTimeOffset.Parse(value.Value, CultureInfo.InvariantCulture),
        _ => throw new ArgumentException("Unexpected typed property in domain projection; review the snapshot before recompute."),
    };

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
