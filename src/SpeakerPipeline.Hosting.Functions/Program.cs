using System.Net.Http.Headers;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SpeakerPipeline.Agents.Discovery;
using SpeakerPipeline.Agents.Scoring;
using SpeakerPipeline.Agents.TrackerMaintenance;
using SpeakerPipeline.Client;
using SpeakerPipeline.Catalog;
using SpeakerPipeline.Core;
using SpeakerPipeline.Hosting.Functions.Observability;
using SpeakerPipeline.Hosting.Functions.Functions;
using SpeakerPipeline.Notifications;
using SpeakerPipeline.Telegram;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration((_, cfg) =>
    {
        cfg.AddEnvironmentVariables();
    })
    .ConfigureServices((ctx, services) =>
    {
        RuntimeSafety.ValidateConsumer(ctx.HostingEnvironment.EnvironmentName, RuntimeSafety.IsAzureHost,
            ctx.Configuration["SpeakerPipelineApi:BaseUrl"], ctx.Configuration["SpeakerPipelineApi:Scope"]);
        // OpenTelemetry → Azure Monitor. Without this the isolated worker's dependency
        // telemetry never reached App Insights, leaving the agents blind.
        // The Telegram bot token is redacted from HTTP spans here (see the extension).
        services.AddFunctionsObservability(ctx.Configuration, ctx.HostingEnvironment.IsDevelopment());

        services.AddScoringAgent(ctx.Configuration);
        services.AddTrackerMaintenanceAgent(ctx.Configuration);
        services.AddDiscoveryAgent(ctx.Configuration);
        services.AddNotifications(ctx.Configuration);
        services.AddTelegram(ctx.Configuration);
        services.AddTalkCatalog(ctx.Configuration);
        services.AddTransient<PipelineRuns>();

        services.AddTransient<BearerTokenHandler>();
        services
            .AddSpeakerPipelineApiClient(ctx.Configuration)
            .AddHttpMessageHandler<BearerTokenHandler>();

        services.AddOpportunityApiClient(ctx.Configuration).AddHttpMessageHandler<BearerTokenHandler>();
        services.AddCatalogApiClient(ctx.Configuration).AddHttpMessageHandler<BearerTokenHandler>();
        services.AddPipelineOperationsClient(ctx.Configuration).AddHttpMessageHandler<BearerTokenHandler>();

        // IChatClient — Azure AI Foundry via its Azure OpenAI endpoint, chosen by
        // ScoringAgent:Provider and authenticated with the app's managed identity.
        // The eval suite injects its own deterministic IChatClient, so this
        // runtime binding is not exercised by the goldens.
        services.AddScoringChatClient(ctx.Configuration);
    })
    .Build();

await host.RunAsync();

// ---------------------------------------------------------------------------
// BearerTokenHandler: attaches Authorization: Bearer <token> to API calls.
// In dev, sends the literal "dev" token (accepted by the API's
// DevAuthenticationHandler). In deployed environments, acquires a token
// via the Function App's managed identity for the API's Entra scope.
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
            throw new InvalidOperationException("SpeakerPipelineApi:Scope is required outside local Development.");
        }

        var token = await _credential.Value.GetTokenAsync(
            new Azure.Core.TokenRequestContext([scope]),
            ct);
        return token.Token;
    }
}
