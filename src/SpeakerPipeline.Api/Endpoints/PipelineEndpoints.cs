using SpeakerPipeline.Api.Auth;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Endpoints;

/// <summary>
/// The pipeline-action surface behind the MCP <c>update_pipeline</c> tool and,
/// later, the Steward. Applies a closed set of explicit transitions to a
/// tracked event. Because the caller must pick <c>Intend</c> vs. <c>Confirmed</c>
/// (there is no ambiguous "submit"), the ambiguity guard is enforced by the
/// contract itself — see <see cref="PipelineAction"/> and BUILD_PLAN §3.2/§5.
/// The richer Submissions state machine (audit timestamps, per-talk rows) lands
/// with the Steward in Milestone 2; here we move <c>Events.Category</c>.
/// </summary>
public static class PipelineEndpoints
{
    public static IEndpointRouteBuilder MapPipelineApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v1/pipeline")
            .WithTags("Pipeline")
            .RequireAuthorization(AuthExtensions.PolicyName);

        group.MapPost("{slug}/actions", PostAction).WithName("ApplyPipelineAction");

        return routes;
    }

    private static async Task<IResult> PostAction(string slug, PipelineActionRequest request, IEventRepository events, CancellationToken ct)
    {
        var existing = await events.GetAsync(slug, ct);
        if (existing is null)
        {
            return Results.NotFound();
        }

        var (category, doNotResurface) = request.Action switch
        {
            PipelineAction.Skip => (EventCategory.Skip, true),
            PipelineAction.Monitor => (EventCategory.Monitor, existing.DoNotResurface),
            PipelineAction.Intend => (EventCategory.SubmitNow, existing.DoNotResurface),
            PipelineAction.Confirmed => (EventCategory.Submitted, existing.DoNotResurface),
            _ => (existing.Category, existing.DoNotResurface),
        };

        var updated = existing with
        {
            Category = category,
            DoNotResurface = doNotResurface,
            StatusDetail = string.IsNullOrWhiteSpace(request.Note) ? existing.StatusDetail : request.Note,
        };

        await events.UpsertAsync(updated, ct);
        return Results.Ok(updated);
    }
}
