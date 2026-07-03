using SpeakerPipeline.Api.Auth;
using SpeakerPipeline.Api.Validation;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Endpoints;

public static class BlackoutsEndpoints
{
    public static IEndpointRouteBuilder MapBlackoutsApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v1/blackouts")
            .WithTags("Blackouts")
            .RequireAuthorization(AuthExtensions.PolicyName);

        group.MapGet("", GetBlackouts).WithName("GetBlackouts");
        group.MapGet("{blackoutId}", GetBlackout).WithName("GetBlackout");
        group.MapPost("", PostBlackout)
            .AddEndpointFilter<ValidationFilter<BlackoutRecord>>()
            .WithName("CreateBlackout");
        group.MapPut("{blackoutId}", PutBlackout)
            .AddEndpointFilter<ValidationFilter<BlackoutRecord>>()
            .WithName("UpdateBlackout");

        return routes;
    }

    private static async Task<IResult> GetBlackouts(IBlackoutRepository blackouts, CancellationToken ct)
        => Results.Ok(await blackouts.GetAllAsync(ct));

    private static async Task<IResult> GetBlackout(string blackoutId, IBlackoutRepository blackouts, CancellationToken ct)
    {
        var record = await blackouts.GetAsync(blackoutId, ct);
        return record is null ? Results.NotFound() : Results.Ok(record);
    }

    private static async Task<IResult> PostBlackout(BlackoutRecord record, IBlackoutRepository blackouts, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var stamped = record with { CreatedUtc = record.CreatedUtc ?? now, UpdatedUtc = now };
        await blackouts.UpsertAsync(stamped, ct);
        return Results.Created($"/v1/blackouts/{stamped.BlackoutId}", stamped);
    }

    private static async Task<IResult> PutBlackout(string blackoutId, BlackoutRecord record, IBlackoutRepository blackouts, CancellationToken ct)
    {
        if (!string.Equals(blackoutId, record.BlackoutId, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem($"URL blackoutId '{blackoutId}' does not match body BlackoutId '{record.BlackoutId}'.", statusCode: StatusCodes.Status400BadRequest);
        }

        var existing = await blackouts.GetAsync(blackoutId, ct);
        var now = DateTimeOffset.UtcNow;
        var stamped = record with
        {
            CreatedUtc = existing?.CreatedUtc ?? record.CreatedUtc ?? now,
            UpdatedUtc = now,
        };
        await blackouts.UpsertAsync(stamped, ct);
        return Results.Ok(stamped);
    }
}
