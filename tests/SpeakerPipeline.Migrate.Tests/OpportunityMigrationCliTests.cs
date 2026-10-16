using System.Net;
using System.Net.Http.Json;
using SpeakerPipeline.Core;
using SpeakerPipeline.Migrate;

namespace SpeakerPipeline.Migrate.Tests;

public sealed class OpportunityMigrationCliTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "speaker-opportunity-migration-tests", Guid.NewGuid().ToString("n"));
    [Fact]
    public async Task Migration_previews_by_default_and_uses_only_bound_API_snapshot()
    {
        var origin = new Uri("https://pipeline.example.org/");
        var snapshot = new MaintenanceSnapshot
        {
            Version = 2, SnapshotId = "frozen", CreatedUtc = DateTimeOffset.Parse("2026-10-16T15:00:00Z"), WritersFrozen = true,
            Target = new() { TableEndpoint = "https://storage.example.org", TableNames = MaintenanceIntegrity.TableNames.Append("Opportunities").ToDictionary(n => n, n => n) },
            Tables = MaintenanceIntegrity.TableNames.Append("Opportunities").ToDictionary(n => n, _ => new List<MaintenanceEntity>()),
        };
        snapshot = snapshot with { Checksum = MaintenanceIntegrity.SnapshotChecksum(snapshot) };
        var source = Path.Combine(_directory, "snapshot.json");
        var output = Path.Combine(_directory, "preview.json");
        PrivateFiles.WriteNew(source, new SnapshotFile(origin.AbsoluteUri, snapshot));
        var handler = new PreviewHandler();
        var args = new[] { "migrate-opportunities", "--api", origin.AbsoluteUri, "--source", source, "--output", output, "--scope", "api://example/.default" };
        Assert.False(CliOptions.Parse(args).Apply);
        Assert.True(CliOptions.Parse([.. args, "--apply"]).Apply);
        Assert.Equal(0, await MigrationCli.RunAsync(args, handler, new StringWriter(), new StringWriter()));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(snapshot.Checksum, PrivateFiles.Read<OpportunityMigrationResult>(output).SnapshotChecksum);
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(args[..^2])); // remote origin needs explicit scope
    }
    private sealed class PreviewHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("/v1/admin/data/opportunities/migrate", request.RequestUri!.AbsolutePath);
            var payload = await request.Content!.ReadFromJsonAsync<OpportunityMigrationRequest>(PrivateFiles.Json, cancellationToken);
            Assert.False(payload!.Apply);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new OpportunityMigrationResult(payload.Snapshot.Checksum, 0, 0, 0, false), options: PrivateFiles.Json) };
        }
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
