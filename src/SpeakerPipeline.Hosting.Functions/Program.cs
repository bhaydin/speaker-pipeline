using System.Net.Http.Headers;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpeakerPipeline.Agents.Scoring;
using SpeakerPipeline.Client;

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

        services.AddTransient<BearerTokenHandler>();
        services
            .AddSpeakerPipelineApiClient(ctx.Configuration)
            .AddHttpMessageHandler<BearerTokenHandler>();

        // IChatClient — provider chosen by configuration.
        //
        // Phase 2 leaves the concrete IChatClient registration as a TODO.
        // The intended production wiring uses Microsoft.Agents.AI.OpenAI's
        // Foundry binding with DefaultAzureCredential. The eval suite injects
        // its own IChatClient so the runtime registration is not needed to
        // exercise the agent in tests.
        services.AddSingleton<IChatClient>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<BearerTokenHandler>>();
            logger.LogWarning(
                "IChatClient is not yet wired for production. Configure ScoringAgent:Provider " +
                "(foundry|azureopenai|openai) and bind a real IChatClient before deploying.");
            throw new NotImplementedException(
                "IChatClient registration is TODO. See SpeakerPipeline.Hosting.Functions/Program.cs.");
        });
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
