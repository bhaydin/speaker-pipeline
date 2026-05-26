using SpeakerPipeline.Api.Auth;
using SpeakerPipeline.Api.Validation;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Endpoints;

public static class SubmissionsEndpoints
{
    public static IEndpointRouteBuilder MapSubmissionsApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v1/events/{eventSlug}/submissions")
            .WithTags("Submissions")
            .RequireAuthorization(AuthExtensions.PolicyName);

        group.MapGet("", GetForEvent).WithName("GetSubmissionsForEvent");

        group.MapPost("", PostSubmission)
            .AddEndpointFilter<ValidationFilter<SubmissionRecord>>()
            .WithName("CreateSubmission");

        group.MapPut("{submissionId}", PutSubmission)
            .AddEndpointFilter<ValidationFilter<SubmissionRecord>>()
            .WithName("UpdateSubmission");

        return routes;
    }

    private static async Task<IResult> GetForEvent(string eventSlug, ISubmissionRepository submissions, CancellationToken ct)
    {
        var list = await submissions.GetForEventAsync(eventSlug, ct);
        return Results.Ok(list);
    }

    private static async Task<IResult> PostSubmission(string eventSlug, SubmissionRecord record, ISubmissionRepository submissions, CancellationToken ct)
    {
        if (!string.Equals(eventSlug, record.EventSlug, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem($"URL eventSlug '{eventSlug}' does not match body EventSlug '{record.EventSlug}'.", statusCode: StatusCodes.Status400BadRequest);
        }
        await submissions.UpsertAsync(record, ct);
        return Results.Created($"/v1/events/{eventSlug}/submissions/{record.SubmissionId}", record);
    }

    private static async Task<IResult> PutSubmission(string eventSlug, string submissionId, SubmissionRecord record, ISubmissionRepository submissions, CancellationToken ct)
    {
        if (!string.Equals(eventSlug, record.EventSlug, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(submissionId, record.SubmissionId, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem("URL eventSlug/submissionId must match body.", statusCode: StatusCodes.Status400BadRequest);
        }
        await submissions.UpsertAsync(record, ct);
        return Results.Ok(record);
    }
}
