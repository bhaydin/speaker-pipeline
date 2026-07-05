using System.Net.Http.Headers;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SpeakerPipeline.Agents.Discovery;
using SpeakerPipeline.Agents.Scoring;
using SpeakerPipeline.Agents.TrackerMaintenance;
using SpeakerPipeline.Client;
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
        // Application Insights is auto-wired when APPLICATIONINSIGHTS_CONNECTION_STRING
        // is set in configuration; no explicit telemetry call needed here.

        services.AddScoringAgent(ctx.Configuration);
        services.AddTrackerMaintenanceAgent(ctx.Configuration);
        services.AddDiscoveryAgent(ctx.Configuration);
        services.AddNotifications(ctx.Configuration);
        services.AddTelegram(ctx.Configuration);

        services.AddTransient<BearerTokenHandler>();
        services
            .AddSpeakerPipelineApiClient(ctx.Configuration)
            .AddHttpMessageHandler<BearerTokenHandler>();

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
            return null;
        }

        var token = await _credential.Value.GetTokenAsync(
            new Azure.Core.TokenRequestContext([scope]),
            ct);
        return token.Token;
    }
}
