using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using SpeakerPipeline.Api.Auth;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Endpoints;

public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v1/catalog").WithTags("Catalog").RequireAuthorization(AuthExtensions.PolicyName);
        group.MapGet("", (ICatalogRepository repo, CancellationToken ct) => repo.GetAsync(ct));
        group.MapGet("/generations/{id}", async (string id, ICatalogRepository repo, CancellationToken ct) =>
        {
            var generation = await repo.GetGenerationAsync(id, ct);
            return generation is null ? Results.NotFound() : Results.Ok(generation);
        });
        group.MapPost("/sync", (CatalogSyncRequest request, ICatalogRepository repo, CancellationToken ct) => repo.EnqueueAsync(request, ct));
        group.MapPost("/jobs/claim", async (ICatalogRepository repo, CancellationToken ct) =>
        {
            var job = await repo.ClaimAsync(ct);
            return job is null ? Results.NoContent() : Results.Ok(job);
        });
        group.MapPost("/jobs/{id}/complete", async (string id, CatalogJobCompletion request, ICatalogRepository repo, CancellationToken ct) =>
        {
            try { await repo.CompleteAsync(id, request, ct); return Results.NoContent(); }
            catch (RequestFailedException ex) when (ex.Status == 412) { return Results.StatusCode(412); }
        });
        group.MapPut("", async (CatalogPublishRequest request, ICatalogRepository repo, CancellationToken ct) =>
        {
            try { await repo.PublishAsync(request, ct); return Results.NoContent(); }
            catch (RequestFailedException ex) when (ex.Status is 409 or 412) { return Results.StatusCode(412); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid catalog generation." }); }
        });
        group.MapGet("/extractions/{hash}", async (string hash, ICatalogRepository repo, CancellationToken ct) =>
        {
            var value = await repo.GetExtractionAsync(hash, ct);
            return value is null ? Results.NotFound() : Results.Ok(value);
        });
        group.MapPut("/extractions", async (CatalogExtraction request, ICatalogRepository repo, CancellationToken ct) =>
        { await repo.SaveExtractionAsync(request, ct); return Results.NoContent(); });
        group.MapPost("/webhook", AcceptWebhook);
        group.MapPost("/review", async (CatalogReviewRequest request, ICatalogRepository repo, ITalkRepository legacyTalks, CancellationToken ct) =>
        {
            try
            {
                if (request.LegacyTalkSlug is { } legacy && await legacyTalks.GetAsync(legacy, ct) is null)
                    return Results.BadRequest(new { error = "Unknown legacy talk slug; existing talk identities must be preserved." });
                var updated = CatalogReview.Apply(await repo.GetAsync(ct), request);
                await repo.PublishAsync(new CatalogPublishRequest(updated, request.ExpectedGenerationId), ct);
                return Results.Ok(updated);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is RequestFailedException { Status: 409 or 412 }) { return Results.StatusCode(412); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid catalog review." }); }
        });
        return routes;
    }

    private static async Task<IResult> AcceptWebhook(CatalogWebhookRequest request, IConfiguration config, ICatalogRepository repo, CancellationToken ct)
    {
        var secret = config["Catalog:GitHub:WebhookSecret"];
        if (string.IsNullOrWhiteSpace(secret)) return Results.StatusCode(503);
        if (string.IsNullOrEmpty(request.Payload) || request.Payload.Length > 1_000_000 || string.IsNullOrWhiteSpace(request.DeliveryId) || request.DeliveryId.Length > 100 || request.Signature?.Length != 71)
            return Results.BadRequest();
        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(request.Payload)));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(request.Signature)))
            return Results.Unauthorized();
        if (request.Event != "push") return Results.Accepted();
        try
        {
            using var json = JsonDocument.Parse(request.Payload);
            var root = json.RootElement;
            var name = root.GetProperty("repository").GetProperty("full_name").GetString();
            if (name != "bhaydin/bhaydin-talks" && name != "bhaydin/Presentations") return Results.BadRequest();
            var defaultBranch = root.GetProperty("repository").GetProperty("default_branch").GetString();
            if (string.IsNullOrWhiteSpace(defaultBranch)) return Results.BadRequest();
            if (root.GetProperty("ref").GetString() != "refs/heads/" + defaultBranch) return Results.Accepted();
            await repo.EnqueueAsync(new CatalogSyncRequest(request.DeliveryId, "github-push"), ct);
            return Results.Accepted();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return Results.BadRequest(); }
    }
}
