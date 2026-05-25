using SpeakerPipeline.Api.Auth;
using SpeakerPipeline.Api.Validation;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Endpoints;

public static class EventsEndpoints
{
    public static IEndpointRouteBuilder MapEventsApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v1/events")
            .WithTags("Events")
            .RequireAuthorization(AuthExtensions.PolicyName);

        group.MapGet("", GetEvents).WithName("GetEvents");
        group.MapGet("{slug}", GetEvent).WithName("GetEvent");
        group.MapPost("", PostEvent)
            .AddEndpointFilter<ValidationFilter<EventRecord>>()
            .WithName("CreateEvent");
        group.MapPut("{slug}", PutEvent)
            .AddEndpointFilter<ValidationFilter<EventRecord>>()
            .WithName("UpdateEvent");
        group.MapDelete("{slug}", DeleteEvent).WithName("DeleteEvent");

        return routes;
    }

    private static async Task<IResult> GetEvents(
        IEventRepository events,
        CancellationToken ct,
        string? category = null,
        int? deadlineDays = null)
    {
        if (deadlineDays is int days and > 0)
        {
            var upcoming = await events.GetUpcomingDeadlinesAsync(TimeSpan.FromDays(days), ct);
            return Results.Ok(upcoming);
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            var categories = category
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c => Enum.TryParse<EventCategory>(c, ignoreCase: true, out var v) ? (EventCategory?)v : null)
                .Where(c => c.HasValue)
                .Select(c => c!.Value)
                .ToArray();

            if (categories.Length == 0)
            {
                return Results.Problem($"Unknown category value(s): '{category}'.", statusCode: StatusCodes.Status400BadRequest);
            }

            var byCategory = await events.GetByCategoryAsync(categories, ct);
            return Results.Ok(byCategory);
        }

        var all = new List<EventRecord>();
        await foreach (var e in events.QueryAsync(ct))
        {
            all.Add(e);
        }
        return Results.Ok(all);
    }

    private static async Task<IResult> GetEvent(string slug, IEventRepository events, CancellationToken ct)
    {
        var record = await events.GetAsync(slug, ct);
        return record is null ? Results.NotFound() : Results.Ok(record);
    }

    private static async Task<IResult> PostEvent(EventRecord record, IEventRepository events, CancellationToken ct)
    {
        await events.UpsertAsync(record, ct);
        return Results.Created($"/v1/events/{record.Slug}", record);
    }

    private static async Task<IResult> PutEvent(string slug, EventRecord record, IEventRepository events, CancellationToken ct)
    {
        if (!string.Equals(slug, record.Slug, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem($"URL slug '{slug}' does not match body slug '{record.Slug}'.", statusCode: StatusCodes.Status400BadRequest);
        }
        await events.UpsertAsync(record, ct);
        return Results.Ok(record);
    }

    private static async Task<IResult> DeleteEvent(string slug, IEventRepository events, CancellationToken ct)
    {
        await events.DeleteAsync(slug, ct);
        return Results.NoContent();
    }
}
