using SpeakerPipeline.Core;
using SpeakerPipeline.Migrate;

namespace SpeakerPipeline.Migrate.Tests;

public sealed class ReviewedManifestTests
{
    private static readonly Uri Api = new("https://pipeline.example.org/");

    [Fact]
    public void Reviewed_delete_is_bound_to_exact_snapshot_and_evidence()
    {
        var export = Export("Events", "events", "reviewed-fiction");
        var result = ReviewedManifestBuilder.Build(export, Api, [Decision("Events", "events", "reviewed-fiction")]);
        result.Validate(Api, export);
        Assert.Equal("current-etag", Assert.Single(result.Plan.Changes).ExpectedETag);
        Assert.Contains("fixture", Assert.Single(result.Review).Reason);
        Assert.Throws<ArgumentException>(() => (result with { Review = [] }).Validate(Api, export));
    }

    [Fact]
    public void Retained_children_and_production_talks_cannot_be_deleted()
    {
        var export = Export("Events", "events", "parent");
        export.Snapshot.Tables["Submissions"].Add(new() { PartitionKey = "parent", RowKey = "retained", ETag = "child" });
        export = export with { Snapshot = export.Snapshot with { Checksum = MaintenanceIntegrity.SnapshotChecksum(export.Snapshot) } };
        Assert.Throws<ArgumentException>(() => ReviewedManifestBuilder.Build(export, Api, [Decision("Events", "events", "parent")]));
        var talk = SampleCatalog.TalkKeys.First();
        Assert.Throws<ArgumentException>(() => ReviewedManifestBuilder.Build(Export("Talks", "talks", talk), Api, [Decision("Talks", "talks", talk)]));
    }

    [Fact]
    public void Unknown_duplicate_and_unsupported_review_decisions_fail()
    {
        var export = Export("Events", "events", "known");
        var decision = Decision("Events", "events", "known");
        Assert.Throws<ArgumentException>(() => ReviewedManifestBuilder.Build(export, Api, [decision, decision]));
        Assert.Throws<ArgumentException>(() => ReviewedManifestBuilder.Build(export, Api, [decision with { RowKey = "missing" }]));
        Assert.Throws<ArgumentException>(() => ReviewedManifestBuilder.Build(export, Api, [decision with { Reason = "" }]));
        Assert.Throws<ArgumentException>(() => ReviewedManifestBuilder.Build(export, Api, [decision with { Disposition = "repair", PropertiesToRemove = ["Notes"] }]));
    }

    private static ReviewedDecision Decision(string table, string partition, string key) => new()
    {
        Table = table, PartitionKey = partition, RowKey = key, Disposition = "delete",
        Reason = "Operator compared the fixture and confirmed only automated derived fields differ.",
    };

    private static SnapshotFile Export(string table, string partition, string key)
    {
        var snapshot = new MaintenanceSnapshot
        {
            SnapshotId = "review-test", CreatedUtc = DateTimeOffset.UtcNow, WritersFrozen = true,
            Target = new() { TableEndpoint = "https://data.table.core.windows.net/", TableNames = MaintenanceIntegrity.TableNames.ToDictionary(t => t, t => t) },
            Tables = MaintenanceIntegrity.TableNames.ToDictionary(t => t, _ => new List<MaintenanceEntity>()),
        };
        snapshot.Tables[table].Add(new() { PartitionKey = partition, RowKey = key, ETag = "current-etag" });
        return new(Api.AbsoluteUri, snapshot with { Checksum = MaintenanceIntegrity.SnapshotChecksum(snapshot) });
    }
}
