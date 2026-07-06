using System.Net.Http.Headers;
using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Client;
using SpeakerPipeline.Core;
using SpeakerPipeline.Mcp.Ingest;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// The six MCP tools are thin adapters over the pipeline API. This host talks
// to SpeakerPipeline.Api over HTTP (never Storage) via the shared client, with
// a bearer token attached per environment. See ADR 0001.
builder.Services.AddTransient<BearerTokenHandler>();
builder.Services
    .AddSpeakerPipelineApiClient(builder.Configuration)
    .AddHttpMessageHandler<BearerTokenHandler>();

// The ingest_event tool posts to the discovery host's agent-tier ingest endpoint
// (extraction is an agent concern), not to the data API. Function-key auth.
builder.Services.Configure<IngestOptions>(builder.Configuration.GetSection(IngestOptions.SectionName));
builder.Services.AddHttpClient<IEventIngestService, HttpEventIngestService>(c => c.Timeout = TimeSpan.FromSeconds(30));

builder.Build().Run();

// ---------------------------------------------------------------------------
// BearerTokenHandler: attaches Authorization: Bearer <token> to API calls.
// In dev, sends the literal "dev" token (accepted by the API's
// DevAuthenticationHandler). In deployed environments, acquires a token via the
// Function App's managed identity for the API's Entra scope.
//
// NOTE: mirrors the handler in SpeakerPipeline.Hosting.Functions. A later
// cleanup could hoist a single copy into SpeakerPipeline.Client.
// ---------------------------------------------------------------------------

internal sealed class BearerTokenHandler(IConfiguration configuration, IHostEnvironment env) : DelegatingHandler
{
    private readonly Lazy<DefaultAzureCredential> _credential = new(() => new DefaultAzureCredential());

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await AcquireTokenAsync(cancellationToken);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return await base.SendAsync(request, cancellationToken);
    }

    private async Task<string?> AcquireTokenAsync(CancellationToken ct)
    {
        if (env.IsDevelopment())
        {
            return "dev";
        }

        var scope = configuration["SpeakerPipelineApi:Scope"];
        if (string.IsNullOrWhiteSpace(scope))
        {
            return null;
        }

        var token = await _credential.Value.GetTokenAsync(new TokenRequestContext([scope]), ct);
        return token.Token;
    }
}
