using System.Text.Json.Serialization;
using FluentValidation;
using SpeakerPipeline.Api.Auth;
using SpeakerPipeline.Api.Endpoints;
using SpeakerPipeline.Api.Observability;
using SpeakerPipeline.Api.Validation;
using SpeakerPipeline.Core;
using SpeakerPipeline.Storage;

var builder = WebApplication.CreateBuilder(args);

RuntimeSafety.ValidateStorage(builder.Environment.EnvironmentName, RuntimeSafety.IsAzureHost,
    builder.Configuration["Storage:ConnectionString"]);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// Enums travel as strings on the wire — matching the samples, the architecture
// docs, and every ISpeakerPipelineApiClient consumer. Without this, minimal-API
// deserialization only accepts numeric enums and rejects "SubmitNow" etc.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddSpeakerPipelineStorage(builder.Configuration, builder.Environment.EnvironmentName);
builder.Services.AddPipelineMaintenanceStorage(builder.Configuration);
builder.Services.AddSpeakerPipelineAuth(builder.Configuration, builder.Environment);
builder.Services.AddDashboardAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddPipelineOperations(builder.Configuration);
builder.Services.AddCatalogStorage(builder.Configuration);
builder.Services.AddSpeakerPipelineObservability(builder.Configuration, builder.Environment);

builder.Services.AddValidatorsFromAssemblyContaining<EventUpsertValidator>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseAuthentication();
app.UseAuthorization();

if (app.Configuration.GetValue<bool>("Dashboard:Enabled"))
{
    app.UseDashboardCsrf();
    app.UseBlazorFrameworkFiles("/dashboard");
    app.UseStaticFiles();
}

app.Use(async (context, next) =>
{
    var writes = !HttpMethods.IsGet(context.Request.Method) &&
        !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method);
    if (app.Configuration.GetValue<bool>("Maintenance:FreezeWrites") && writes &&
        !context.Request.Path.StartsWithSegments("/v1/admin/data"))
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "300";
        await context.Response.WriteAsJsonAsync(new { error = "Production maintenance: business writes are paused." });
        return;
    }
    await next(context);
});

app.MapHealth();
app.MapEventsApi();
app.MapSubmissionsApi();
app.MapTalksApi();
app.MapScoringApi();
app.MapTopicsApi();
app.MapBlackoutsApi();
app.MapPipelineApi();
app.MapNotificationsApi();
app.MapMaintenanceApi();
app.MapOpportunitiesApi();
app.MapOpportunityMigrationApi();
app.MapCatalogApi();
app.MapOperationsApi();
app.MapDashboard();

app.Run();

public partial class Program;
