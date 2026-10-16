using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using SpeakerPipeline.Client;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Migrate;

internal static class MigrationCli
{
    public static async Task<int> RunAsync(string[] args, HttpMessageHandler? testHandler = null, TextWriter? output = null, TextWriter? error = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            await output.WriteLineAsync(CliOptions.Usage);
            return 0;
        }
        try
        {
            var options = CliOptions.Parse(args);
            if (options.Output is not null)
            {
                PrivateFiles.ValidatePath(options.Output);
                if (File.Exists(options.Output)) throw new ArgumentException("Output already exists; choose a new private output path.");
            }
            if (options.RepairsOutput is not null)
            {
                var repairPath = PrivateFiles.ValidatePath(options.RepairsOutput);
                if (File.Exists(repairPath) || string.Equals(repairPath, Path.GetFullPath(options.Output!), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Repairs output must be a new private file distinct from the report.");
            }
            using var http = new HttpClient(testHandler ?? CreateHandler(options)) { BaseAddress = options.Api, Timeout = TimeSpan.FromMinutes(10) };
            if (options.Command == "recompute")
            {
                var before = PrivateFiles.Read<SnapshotFile>(options.Source!);
                var after = PrivateFiles.Read<SnapshotFile>(options.Snapshot!);
                var preview = RecomputePreview.Build(before, after, options.Api, options.At!.Value, options.PrepWindowDays!.Value, options.PrepThreshold!.Value);
                PrivateFiles.WriteNew(options.Output!, preview.Report);
                PrivateFiles.WriteNew(options.RepairsOutput!, preview.Repairs);
                await output.WriteLineAsync($"Recompute preview: {preview.Repairs.Plan.Changes.Count} proposed flag repairs; {preview.Report.ScoringChanges.Count(s => s.ReviewStatus == "requires-explicit-operator-review")} affected candidates require review; {preview.Report.ScoringChanges.Count(s => s.ReviewStatus == "protected-do-not-rescore")} protected decisions. No model calls, API writes, or notifications.");
                return 0;
            }
            if (options.Command == "review")
            {
                var export = PrivateFiles.Read<SnapshotFile>(options.Snapshot!);
                var reviewed = ReviewedManifestBuilder.Build(export, options.Api, options.Source!);
                PrivateFiles.WriteNew(options.Output!, reviewed);
                await output.WriteLineAsync($"Reviewed cleanup manifest: {reviewed.Plan.Changes.Count} exact changes. No API writes. Operation: {reviewed.Plan.OperationId}.");
                return 0;
            }
            if (options.Command == "review-scoring")
            {
                var export = PrivateFiles.Read<SnapshotFile>(options.Snapshot!);
                var reviewed = ReviewedScoringManifestBuilder.Build(export, options.Api, options.Source!);
                PrivateFiles.WriteNew(options.Output!, reviewed);
                await output.WriteLineAsync($"Reviewed scoring manifest: {reviewed.Plan.Changes.Count} exact decision proposals. No model calls, API writes, or notifications. Operation: {reviewed.Plan.OperationId}.");
                return 0;
            }
            if (options.Command is "samples" or "import")
            {
                var batch = ImportBatch.Load(options.Source!, samples: options.Command == "samples");
                var api = new SpeakerPipelineApiClient(http, NullLogger<SpeakerPipelineApiClient>.Instance);
                var events = await api.GetEventsAsync();
                var talks = await api.GetTalksAsync();
                batch.ValidateReferences(events, talks);
                await output.WriteLineAsync($"Validated import: {batch.Talks.Count} talks, {batch.Events.Count} events, {batch.Submissions.Count} submissions. Mode: {(options.Apply ? "apply" : "preview (no writes)")}.");
                if (!options.Apply) return 0;
                // Complete all parsing, mapping and relationship validation before the first write.
                // Fail on the first HTTP error; retrying these keyed upserts is idempotent.
                foreach (var talk in batch.Talks) await api.UpsertTalkAsync(talk);
                foreach (var item in batch.Events) await api.UpsertEventAsync(item);
                foreach (var submission in batch.Submissions) await api.UpsertSubmissionAsync(submission);
                await output.WriteLineAsync("Import complete.");
                return 0;
            }
            if (options.Command == "export")
            {
                var snapshot = await GetAsync<MaintenanceSnapshot>(http, "v1/admin/data/snapshot");
                var export = new SnapshotFile(options.Api.AbsoluteUri, snapshot);
                ManifestBuilder.ValidateSnapshot(export, options.Api);
                PrivateFiles.WriteNew(options.Output!, export);
                await output.WriteLineAsync($"Snapshot exported: {snapshot.Tables.Values.Sum(t => t.Count)} rows in six tables; writers frozen: {snapshot.WritersFrozen}. SHA-256: {snapshot.Checksum}.");
                return 0;
            }
            if (options.Command == "preview")
            {
                var export = PrivateFiles.Read<SnapshotFile>(options.Source!);
                var manifest = ManifestBuilder.Build(export, options.Api);
                PrivateFiles.WriteNew(options.Output!, manifest);
                await output.WriteLineAsync($"Cleanup preview: {manifest.Plan.Changes.Count} exact changes; {manifest.Review.Count(r => r.Disposition == "retain-review")} retained for review. No API writes. Operation: {manifest.Plan.OperationId}.");
                return 0;
            }
            if (options.Command == "restore-snapshot")
            {
                var export = PrivateFiles.Read<SnapshotFile>(options.Source!);
                if (!Uri.TryCreate(export.ApiOrigin, UriKind.Absolute, out var originalApi))
                    throw new ArgumentException("Snapshot origin is invalid.");
                ManifestBuilder.ValidateSnapshot(export, originalApi);
                if (options.Apply && !export.Snapshot.WritersFrozen)
                    throw new ArgumentException("Full restore apply requires a snapshot taken while writers were frozen.");
                if (options.Api.AbsoluteUri != export.ApiOrigin && !options.Api.IsLoopback)
                    throw new ArgumentException("A full snapshot may be restored only to its original API origin or a loopback rehearsal API.");
                var targetPath = PrivateFiles.ValidatePath(options.Output!);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                using var receipt = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(receipt, new { export.Snapshot.Checksum, Status = "interrupted-or-failed; retry the same snapshot only" }, PrivateFiles.Json);
                receipt.Flush(flushToDisk: true);
                var restored = await PostAsync<MaintenanceSnapshotRestoreRequest, MaintenanceResult>(http, "v1/admin/data/restore-snapshot",
                    new() { Snapshot = export.Snapshot, Apply = options.Apply });
                if (restored.SnapshotChecksum != export.Snapshot.Checksum)
                    throw new ArgumentException("API restore result does not match the supplied snapshot.");
                receipt.Position = 0;
                receipt.SetLength(0);
                JsonSerializer.Serialize(receipt, restored, PrivateFiles.Json);
                receipt.Flush(flushToDisk: true);
                await output.WriteLineAsync($"Snapshot restore: {restored.Status}; completed {restored.CompletedCount}/{restored.ChangeCount}. Result saved privately.");
                return options.Apply && !restored.Applied ? 2 : 0;
            }
            var suppliedSnapshot = PrivateFiles.Read<SnapshotFile>(options.Snapshot!);
            var suppliedManifest = PrivateFiles.Read<CleanupManifest>(options.Source!);
            suppliedManifest.Validate(options.Api, suppliedSnapshot);
            // Reserve the result path before any mutation. A failed/interrupted operation can
            // be resumed with the same manifest and a fresh result path; the API journal wins.
            var path = PrivateFiles.ValidatePath(options.Output!);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var resultFile = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var failureReceipt = new { suppliedManifest.Plan.OperationId, suppliedManifest.SnapshotChecksum, Status = "interrupted-or-failed; inspect API journal before retry" };
            JsonSerializer.Serialize(resultFile, failureReceipt, PrivateFiles.Json);
            resultFile.Flush(flushToDisk: true);
            MaintenanceResult result;
            if (options.Command == "restore")
            {
                var existing = await GetAsync<MaintenanceResult>(http, $"v1/admin/data/operations/{Uri.EscapeDataString(suppliedManifest.Plan.OperationId)}");
                if (existing.SnapshotChecksum != suppliedManifest.SnapshotChecksum) throw new ArgumentException("Restore journal does not match the supplied snapshot.");
                result = await PostAsync<MaintenanceRestoreRequest, MaintenanceResult>(http, "v1/admin/data/restore",
                    new() { OperationId = suppliedManifest.Plan.OperationId, Apply = options.Apply });
            }
            else
            {
                if (!suppliedSnapshot.Snapshot.WritersFrozen) throw new ArgumentException("Apply requires a snapshot taken while all writers are frozen. Export and preview again after freezing.");
                var preflight = await PostAsync<MaintenanceApplyRequest, MaintenanceResult>(http, "v1/admin/data/cleanup",
                    new() { Plan = suppliedManifest.Plan });
                if (preflight.OperationId != suppliedManifest.Plan.OperationId || preflight.SnapshotChecksum != suppliedManifest.SnapshotChecksum || preflight.Applied)
                    throw new ArgumentException("API preflight does not match the supplied operation and snapshot.");
                result = await PostAsync<MaintenanceApplyRequest, MaintenanceResult>(http, "v1/admin/data/cleanup",
                    new() { Plan = suppliedManifest.Plan, Apply = true });
            }
            if (result.SnapshotChecksum != suppliedManifest.SnapshotChecksum || result.OperationId != suppliedManifest.Plan.OperationId)
                throw new ArgumentException("API result does not match the supplied operation and snapshot.");
            resultFile.Position = 0;
            resultFile.SetLength(0);
            JsonSerializer.Serialize(resultFile, result, PrivateFiles.Json);
            resultFile.Flush(flushToDisk: true);
            await output.WriteLineAsync($"Operation {result.OperationId}: {result.Status}; completed {result.CompletedCount}/{result.ChangeCount}. Result saved privately.");
            return options.Command == "apply" && (!result.Applied || result.Status != "Completed") ||
                options.Command == "restore" && options.Apply && !result.Restored ? 2 : 0;
        }
        catch (ArgumentException ex)
        {
            await error.WriteLineAsync("Validation failed: " + ex.Message);
            return 1;
        }
        catch (JsonException)
        {
            await error.WriteLineAsync("Invalid input JSON or API response. No entity content was logged. Check all required fields and mappings.");
            return 1;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            await error.WriteLineAsync("Invalid typed snapshot value. No entity content was logged; inspect the private source files.");
            return 1;
        }
        catch (HttpRequestException ex)
        {
            await error.WriteLineAsync($"API request failed (status {(int?)ex.StatusCode}); response bodies are not logged. For cleanup/restore, rerun the unchanged manifest with a new result path to resume its API journal.");
            return 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AuthenticationFailedException or OperationCanceledException or InvalidOperationException)
        {
            await error.WriteLineAsync($"Migration stopped ({ex.GetType().Name}); no credentials or entity content were logged. Check private files, operator login, and API journal before retrying.");
            return 2;
        }
    }

    private static HttpMessageHandler CreateHandler(CliOptions options)
    {
        // No redirects: neither a bearer token nor a sample payload may leave the explicit origin.
        var transport = new HttpClientHandler { AllowAutoRedirect = false };
        return new OperatorTokenHandler(options.Scope is null ? null : new AzureCliCredential(), options.Scope,
            maintenance: options.Command is not ("samples" or "import")) { InnerHandler = transport };
    }

    private static async Task<T> GetAsync<T>(HttpClient http, string path)
    {
        using var response = await http.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(PrivateFiles.Json) ?? throw new JsonException();
    }

    private static async Task<TResponse> PostAsync<TRequest, TResponse>(HttpClient http, string path, TRequest request)
    {
        using var response = await http.PostAsJsonAsync(path, request, PrivateFiles.Json);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(PrivateFiles.Json) ?? throw new JsonException();
    }
}

internal sealed class OperatorTokenHandler(TokenCredential? credential, string? scope, bool maintenance = false) : DelegatingHandler
{
    private AccessToken _cached;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (credential is null)
        {
            if (request.RequestUri?.IsLoopback != true) throw new InvalidOperationException("Local authentication cannot be used for a remote API.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", maintenance ? "maintenance" : "dev");
        }
        else
        {
            if (_cached.ExpiresOn <= DateTimeOffset.UtcNow.AddMinutes(5))
                _cached = await credential.GetTokenAsync(new TokenRequestContext([scope!]), cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cached.Token);
        }
        return await base.SendAsync(request, cancellationToken);
    }
}
