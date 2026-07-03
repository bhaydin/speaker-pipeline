using SpeakerPipeline.Api.Auth;
using SpeakerPipeline.Api.Validation;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Endpoints;

public static class TopicsEndpoints
{
    public static IEndpointRouteBuilder MapTopicsApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v1/topics")
            .WithTags("Topics")
            .RequireAuthorization(AuthExtensions.PolicyName);

        group.MapGet("", GetTopics).WithName("GetTopics");
        group.MapGet("{topicId}", GetTopic).WithName("GetTopic");
        group.MapPost("", PostTopic)
            .AddEndpointFilter<ValidationFilter<TopicRecord>>()
            .WithName("CreateTopic");
        group.MapPut("{topicId}", PutTopic)
            .AddEndpointFilter<ValidationFilter<TopicRecord>>()
            .WithName("UpdateTopic");

        return routes;
    }

    private static async Task<IResult> GetTopics(ITopicRepository topics, CancellationToken ct, string? stage = null)
    {
        if (!string.IsNullOrWhiteSpace(stage))
        {
            if (!Enum.TryParse<TopicStage>(stage, ignoreCase: true, out var parsed))
            {
                return Results.Problem($"Unknown stage value: '{stage}'.", statusCode: StatusCodes.Status400BadRequest);
            }
            return Results.Ok(await topics.GetByStageAsync(parsed, ct));
        }

        return Results.Ok(await topics.GetAllAsync(ct));
    }

    private static async Task<IResult> GetTopic(string topicId, ITopicRepository topics, CancellationToken ct)
    {
        var record = await topics.GetAsync(topicId, ct);
        return record is null ? Results.NotFound() : Results.Ok(record);
    }

    private static async Task<IResult> PostTopic(TopicRecord record, ITopicRepository topics, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var stamped = record with { CreatedUtc = record.CreatedUtc ?? now, UpdatedUtc = now };
        await topics.UpsertAsync(stamped, ct);
        return Results.Created($"/v1/topics/{stamped.TopicId}", stamped);
    }

    private static async Task<IResult> PutTopic(string topicId, TopicRecord record, ITopicRepository topics, CancellationToken ct)
    {
        if (!string.Equals(topicId, record.TopicId, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem($"URL topicId '{topicId}' does not match body TopicId '{record.TopicId}'.", statusCode: StatusCodes.Status400BadRequest);
        }

        var existing = await topics.GetAsync(topicId, ct);
        var now = DateTimeOffset.UtcNow;
        var stamped = record with
        {
            CreatedUtc = existing?.CreatedUtc ?? record.CreatedUtc ?? now,
            UpdatedUtc = now,
        };
        await topics.UpsertAsync(stamped, ct);
        return Results.Ok(stamped);
    }
}
