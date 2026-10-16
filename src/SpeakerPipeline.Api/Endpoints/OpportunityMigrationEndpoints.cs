using Azure;
using SpeakerPipeline.Api.Auth;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Endpoints;

public static class OpportunityMigrationEndpoints
{
    public static IEndpointRouteBuilder MapOpportunityMigrationApi(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/v1/admin/data/opportunities/migrate", async (OpportunityMigrationRequest request, IServiceProvider services, IConfiguration config, CancellationToken ct) =>
        {
            if (!config.GetValue<bool>("Maintenance:Enabled")) return Results.NotFound();
            try { return Results.Ok(await services.GetRequiredService<IOpportunityMigrationService>().MigrateAsync(request, config.GetValue<bool>("Maintenance:FreezeWrites"), ct)); }
            catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
            catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 409); }
            catch (RequestFailedException ex) when (ex.Status is 409 or 412) { return Results.Problem("Migration conflicted with another operation. Review and replay the same snapshot.", statusCode: 409); }
        }).RequireAuthorization(AuthExtensions.MaintenancePolicyName).ExcludeFromDescription();
        return routes;
    }
}
