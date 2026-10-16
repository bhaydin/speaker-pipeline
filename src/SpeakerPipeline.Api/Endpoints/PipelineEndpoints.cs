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
        group.MapGet("context", GetContext).WithName("GetPipelineContext");

        return routes;
    }

    /// <summary>
    /// Assembles the calendar/load context the scoring agent needs: committed
    /// engagements (with a reusability-derived effort hint), blackout ranges, and
    /// new-topic prep counts. One query set per scoring run; the agent windows it
    /// per candidate. See BUILD_PLAN.GO_FORWARD B1.
    /// </summary>
    private static async Task<IResult> GetContext(
        IEventRepository events,
        ISubmissionRepository submissions,
        ITalkRepository talks,
        IBlackoutRepository blackouts,
        CancellationToken ct)
    {
        var relevant = new List<EventRecord>();
        await foreach (var record in events.QueryAsync(ct))
            if (PipelineContextAssembler.RelevantCategories.Contains(record.Category) || record.RecordedSubmissionStatus is SubmissionStatus.Submitted or SubmissionStatus.InReview or SubmissionStatus.Accepted)
                relevant.Add(record);
        var talkList = await talks.GetAllAsync(ct);
        var blackoutList = await blackouts.GetAllAsync(ct);

        var submissionTasks = relevant.Select(async e =>
        {
            var list = await submissions.GetForEventAsync(e.Slug, ct);
            return (e.Slug, List: (IReadOnlyList<SubmissionRecord>)list);
        });

        var submissionsByEvent = (await Task.WhenAll(submissionTasks))
            .ToDictionary(x => x.Slug, x => x.List, StringComparer.OrdinalIgnoreCase);

        var context = PipelineContextAssembler.Assemble(
            relevant, submissionsByEvent, talkList, blackoutList, DateTimeOffset.UtcNow);

        return Results.Ok(context);
    }

    private static async Task<IResult> PostAction(string slug, PipelineActionRequest request, IEventRepository events, HttpContext context, IConfiguration config, CancellationToken ct)
    {
        if (config.GetValue<bool>("Storage:OpportunitiesEnabled"))
        {
            var etag = context.Request.Headers.IfMatch.ToString();
            if (string.IsNullOrWhiteSpace(etag) || etag == "*") return Results.Problem("An exact If-Match header is required.", statusCode: 428);
            var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idempotencyKey)) return Results.Problem("An Idempotency-Key header is required.", statusCode: 428);
            if (!Enum.IsDefined(request.Action)) return Results.BadRequest(new { error = "Unknown pipeline action." });
            var action = request.Action switch
            {
                PipelineAction.Skip => OpportunityAction.Drop,
                PipelineAction.Confirmed => OpportunityAction.Submitted,
                _ => OpportunityAction.SetStatus,
            };
            var command = new OpportunityActionRequest
            {
                Action = action, IdempotencyKey = idempotencyKey,
                Status = request.Action == PipelineAction.Intend ? OpportunityStatus.SubmitNow : OpportunityStatus.Monitor,
                Note = request.Note, ReasonCode = request.ReasonCode,
            };
            try
            {
                var applied = await context.RequestServices.GetRequiredService<IOpportunityRepository>().ApplyActionAsync(slug, command, etag,
                    context.User.FindFirst("oid")?.Value ?? "authenticated-operator", ct);
                return Results.Ok(OpportunityMapping.Project(applied));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (OpportunityConflictException ex) { return Results.Json(new { error = ex.Message, current = ex.Current }, statusCode: 412); }
            catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
        }
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
