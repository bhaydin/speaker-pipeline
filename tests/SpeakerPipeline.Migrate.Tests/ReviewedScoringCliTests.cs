using System.Net;
using SpeakerPipeline.Core;
using SpeakerPipeline.Migrate;

namespace SpeakerPipeline.Migrate.Tests;

public sealed class ReviewedScoringCliTests : IDisposable
{
    private readonly string _private = Path.Combine(Path.GetTempPath(), "speaker-pipeline-reviewed-scoring-tests", Guid.NewGuid().ToString("n"));
    private static readonly Uri Api = new("https://pipeline.example.org/");

    [Fact]
    public void Review_requires_all_explicit_inputs_and_cannot_apply_or_authenticate_implicitly()
    {
        var args = new[] { "review-scoring", "--api", Api.AbsoluteUri, "--source", "review.json", "--snapshot", "snapshot.json", "--output", "manifest.json" };
        var options = CliOptions.Parse(args);
        Assert.False(options.Apply);
        Assert.Null(options.Scope);
        Assert.Throws<ArgumentException>(() => CliOptions.Parse([.. args, "--apply"]));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(args[..^2]));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["review-scoring", "--api", Api.AbsoluteUri, "--source", "review.json", "--output", "manifest.json"]));
    }

    [Fact]
    public async Task Review_creates_exact_private_proposal_without_any_network_calls()
    {
        var export = Export();
        var args = Inputs(export, Decision());
        var handler = new RejectNetworkHandler();
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, await MigrationCli.RunAsync(args, handler, output, error));
        Assert.Equal(0, handler.CallCount);
        var manifest = PrivateFiles.Read<CleanupManifest>(Path.Combine(_private, "manifest.json"));
        manifest.Validate(Api, export);
        var change = Assert.Single(manifest.Plan.Changes);
        Assert.Equal(MaintenanceChangeKind.ApplyScoringDecision, change.Kind);
        Assert.Equal("candidate", change.RowKey);
        Assert.Equal("exact-etag", change.ExpectedETag);
        Assert.Equal(4, change.PropertiesToSet.Count);
        Assert.Contains(Decision().OutputProofSha256, manifest.Plan.Purpose);
        Assert.Contains("No model calls, API writes, or notifications", output.ToString());
        Assert.DoesNotContain(Decision().NextAction, output.ToString());
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("invalid-proof")]
    [InlineData("protected")]
    [InlineData("different-target")]
    [InlineData("manual-original")]
    [InlineData("manual-proposed")]
    [InlineData("empty-agent-version")]
    public async Task Invalid_review_never_contacts_API_or_creates_manifest(string scenario)
    {
        var export = Export(scenario == "protected" ? "Accepted" : "Monitor", scenario == "manual-original" ? "manual-operator" : "scoring-agent-v1");
        var decision = Decision() with
        {
            OutputProofSha256 = scenario == "invalid-proof" ? "not-a-hash" : Decision().OutputProofSha256,
            DecidedByAgent = scenario switch { "manual-proposed" => "manual-operator", "empty-agent-version" => "scoring-agent-", _ => "scoring-agent-v1" },
        };
        if (scenario == "different-target") export = export with { ApiOrigin = "https://another.example.org/" };
        var handler = new RejectNetworkHandler();
        var error = new StringWriter();
        Assert.Equal(1, await MigrationCli.RunAsync(Inputs(export, decision), handler, new StringWriter(), error));
        Assert.Equal(0, handler.CallCount);
        Assert.False(File.Exists(Path.Combine(_private, "manifest.json")));
        Assert.DoesNotContain(decision.NextAction, error.ToString());
        Assert.DoesNotContain(decision.Reason, error.ToString());
    }

    [Fact]
    public async Task Existing_private_output_is_never_overwritten()
    {
        var args = Inputs(Export(), Decision());
        var path = Path.Combine(_private, "manifest.json");
        File.WriteAllText(path, "existing-review");
        var handler = new RejectNetworkHandler();
        Assert.Equal(1, await MigrationCli.RunAsync(args, handler, new StringWriter(), new StringWriter()));
        Assert.Equal("existing-review", File.ReadAllText(path));
        Assert.Equal(0, handler.CallCount);
    }

    private string[] Inputs(SnapshotFile snapshot, ReviewedScoringDecision decision)
    {
        var source = Path.Combine(_private, "review.json");
        var snapshotPath = Path.Combine(_private, "snapshot.json");
        PrivateFiles.WriteNew(source, new[] { decision });
        PrivateFiles.WriteNew(snapshotPath, snapshot);
        return ["review-scoring", "--api", Api.AbsoluteUri, "--source", source, "--snapshot", snapshotPath, "--output", Path.Combine(_private, "manifest.json")];
    }

    private static ReviewedScoringDecision Decision() => new()
    {
        EventSlug = "candidate", Category = "Outreach", Priority = "Medium",
        NextAction = "Reviewed private rationale for organizer outreach.", DecidedByAgent = "scoring-agent-v1",
        Reason = "Operator confirmed this proposal preserves the intended decision and approved the shadow output.",
        OutputProofSha256 = new string('a', 64),
    };

    private static SnapshotFile Export(string category = "Monitor", string agent = "scoring-agent-v1")
    {
        var snapshot = new MaintenanceSnapshot
        {
            SnapshotId = "review-scoring-test", CreatedUtc = DateTimeOffset.UtcNow, WritersFrozen = true,
            Target = new() { TableEndpoint = "https://data.table.core.windows.net/", TableNames = MaintenanceIntegrity.TableNames.ToDictionary(t => t, t => t) },
            Tables = MaintenanceIntegrity.TableNames.ToDictionary(t => t, _ => new List<MaintenanceEntity>()),
        };
        snapshot.Tables["Events"].Add(new()
        {
            PartitionKey = "events", RowKey = "candidate", ETag = "exact-etag",
            Properties = new()
            {
                ["Category"] = new(MaintenanceValueType.String, category),
                ["DecidedByAgent"] = new(MaintenanceValueType.String, agent),
                ["DoNotResurface"] = new(MaintenanceValueType.Boolean, "false"),
                ["UnknownProperty"] = new(MaintenanceValueType.String, "preserved"),
            },
        });
        return new(Api.AbsoluteUri, snapshot with { Checksum = MaintenanceIntegrity.SnapshotChecksum(snapshot) });
    }

    public void Dispose()
    {
        if (Directory.Exists(_private)) Directory.Delete(_private, recursive: true);
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        }
    }
}
