using FluentValidation;
using SpeakerPipeline.Api.Auth;
using SpeakerPipeline.Api.Endpoints;
using SpeakerPipeline.Api.Observability;
using SpeakerPipeline.Api.Validation;
using SpeakerPipeline.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

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

app.Run();

public partial class Program;
