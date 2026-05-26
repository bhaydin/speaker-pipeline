using SpeakerPipeline.Api.Auth;
using SpeakerPipeline.Api.Validation;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Endpoints;

public static class TalksEndpoints
{
    public static IEndpointRouteBuilder MapTalksApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v1/talks")
            .WithTags("Talks")
            .RequireAuthorization(AuthExtensions.PolicyName);

        group.MapGet("", GetTalks).WithName("GetTalks");
        group.MapGet("{slug}", GetTalk).WithName("GetTalk");
        group.MapPost("", PostTalk)
            .AddEndpointFilter<ValidationFilter<TalkRecord>>()
            .WithName("CreateTalk");
        group.MapPut("{slug}", PutTalk)
            .AddEndpointFilter<ValidationFilter<TalkRecord>>()
            .WithName("UpdateTalk");

        return routes;
    }

    private static async Task<IResult> GetTalks(ITalkRepository talks, CancellationToken ct, string? lane = null)
    {
        if (!string.IsNullOrWhiteSpace(lane))
        {
            if (!Enum.TryParse<Lane>(lane, ignoreCase: true, out var laneValue))
            {
                return Results.Problem($"Unknown lane '{lane}'.", statusCode: StatusCodes.Status400BadRequest);
            }
            return Results.Ok(await talks.GetByLaneAsync(laneValue, ct));
        }
        return Results.Ok(await talks.GetAllAsync(ct));
    }

    private static async Task<IResult> GetTalk(string slug, ITalkRepository talks, CancellationToken ct)
    {
        var record = await talks.GetAsync(slug, ct);
        return record is null ? Results.NotFound() : Results.Ok(record);
    }

    private static async Task<IResult> PostTalk(TalkRecord record, ITalkRepository talks, CancellationToken ct)
    {
        await talks.UpsertAsync(record, ct);
        return Results.Created($"/v1/talks/{record.Slug}", record);
    }

    private static async Task<IResult> PutTalk(string slug, TalkRecord record, ITalkRepository talks, CancellationToken ct)
    {
        if (!string.Equals(slug, record.Slug, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem($"URL slug '{slug}' does not match body slug '{record.Slug}'.", statusCode: StatusCodes.Status400BadRequest);
        }
        await talks.UpsertAsync(record, ct);
        return Results.Ok(record);
    }
}
