using System.Text;
using SpeakerPipeline.Api.Auth;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Endpoints;

public static class OperationsEndpoints
{
    private static readonly HashSet<string> Kinds = ["runs", "deliveries", "messages", "messagechunks", "callbacks", "incidents", "outbox", "outboxchunks"];

    public static IEndpointRouteBuilder MapOperationsApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v1/operations").RequireAuthorization(AuthExtensions.PolicyName);
        group.MapGet("/{kind}/{id}", async (string kind, string id, IOperationsRepository store, CancellationToken ct) =>
            !Valid(kind, id) ? Results.BadRequest() : await store.GetAsync(kind, id, ct) is { } found ? Results.Ok(found) : Results.NotFound());
        group.MapGet("/{kind}", async (string kind, DateTimeOffset? since, IOperationsRepository store, CancellationToken ct) =>
            !Kinds.Contains(kind) ? Results.BadRequest() : Results.Ok(await store.ListAsync(kind, since ?? DateTimeOffset.UtcNow.AddDays(-14), ct)));
        group.MapPut("/{kind}/{id}", async (string kind, string id, OperationDocument document, HttpRequest request, IOperationsRepository store, CancellationToken ct) =>
        {
            if (!Valid(kind, id) || kind != document.Kind || id != document.Id || document.Payload.Length > 30000 || Encoding.UTF8.GetByteCount(document.Payload) > 60000)
                return Results.BadRequest();
            try { using var parsed = System.Text.Json.JsonDocument.Parse(document.Payload); }
            catch (System.Text.Json.JsonException) { return Results.BadRequest(); }
            var version = request.Headers.IfMatch.FirstOrDefault();
            if (version == "*") return Results.BadRequest();
            var saved = await store.SaveAsync(document with { UpdatedUtc = DateTimeOffset.UtcNow }, version, ct);
            return saved is null ? Results.Conflict() : Results.Ok(saved);
        });
        return routes;
    }

    private static bool Valid(string kind, string id) => Kinds.Contains(kind) && id.Length is > 0 and <= 128
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
