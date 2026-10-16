using Azure;
using SpeakerPipeline.Api.Auth;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Endpoints;

public static class MaintenanceEndpoints
{
    public static IEndpointRouteBuilder MapMaintenanceApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v1/admin/data")
            .RequireAuthorization(AuthExtensions.MaintenancePolicyName)
            .ExcludeFromDescription();

        group.MapGet("snapshot", (HttpContext context, IConfiguration configuration) =>
            Run(context, configuration, apply: false, async service => Results.Ok(
                await service.ExportAsync(configuration.GetValue<bool>("Maintenance:FreezeWrites"), context.RequestAborted))));
        group.MapPost("cleanup", (MaintenanceApplyRequest request, HttpContext context, IConfiguration configuration) =>
            Run(context, configuration, request.Apply, async service => Results.Ok(await service.CleanupAsync(request, context.RequestAborted))));
        group.MapPost("restore", (MaintenanceRestoreRequest request, HttpContext context, IConfiguration configuration) =>
            Run(context, configuration, request.Apply, async service => Results.Ok(await service.RestoreAsync(request, context.RequestAborted))));
        group.MapPost("restore-snapshot", (MaintenanceSnapshotRestoreRequest request, HttpContext context, IConfiguration configuration, IHostEnvironment environment) =>
            Run(context, configuration, request.Apply, async service => Results.Ok(await service.RestoreSnapshotAsync(
                request, environment.IsDevelopment() && !RuntimeSafety.IsAzureHost, context.RequestAborted))));
        group.MapGet("operations/{operationId}", (string operationId, HttpContext context, IConfiguration configuration) =>
            Run(context, configuration, apply: false, async service =>
            {
                var result = await service.GetOperationAsync(operationId, context.RequestAborted);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }));
        return routes;
    }

    private static async Task<IResult> Run(HttpContext context, IConfiguration configuration, bool apply, Func<IPipelineMaintenanceService, Task<IResult>> action)
    {
        if (!configuration.GetValue<bool>("Maintenance:Enabled")) return Results.NotFound();
        if (apply && !configuration.GetValue<bool>("Maintenance:FreezeWrites"))
            return Results.Problem("Business writes must be frozen before cleanup or restore can apply.", statusCode: StatusCodes.Status409Conflict);
        try
        {
            return await action(context.RequestServices.GetRequiredService<IPipelineMaintenanceService>());
        }
        catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest); }
        catch (FormatException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest); }
        catch (KeyNotFoundException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound); }
        catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            return Results.Problem("Storage changed or another maintenance operation holds the lock. Retry the same operation after reviewing its journal.", statusCode: StatusCodes.Status409Conflict);
        }
    }
}
