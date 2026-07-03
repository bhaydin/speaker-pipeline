using System.Text.Json.Serialization;
using FluentValidation;
using SpeakerPipeline.Api.Auth;
using SpeakerPipeline.Api.Endpoints;
using SpeakerPipeline.Api.Observability;
using SpeakerPipeline.Api.Validation;
using SpeakerPipeline.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// Enums travel as strings on the wire — matching the samples, the architecture
// docs, and every ISpeakerPipelineApiClient consumer. Without this, minimal-API
// deserialization only accepts numeric enums and rejects "SubmitNow" etc.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddSpeakerPipelineStorage(builder.Configuration);
builder.Services.AddSpeakerPipelineAuth(builder.Configuration, builder.Environment);
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

app.MapHealth();
app.MapEventsApi();
app.MapSubmissionsApi();
app.MapTalksApi();
app.MapScoringApi();
app.MapTopicsApi();
app.MapBlackoutsApi();
app.MapPipelineApi();

app.Run();

public partial class Program;
