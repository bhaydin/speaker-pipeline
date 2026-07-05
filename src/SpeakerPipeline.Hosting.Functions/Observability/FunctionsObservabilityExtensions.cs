using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SpeakerPipeline.Telegram;

namespace SpeakerPipeline.Hosting.Functions.Observability;

/// <summary>
/// OpenTelemetry wiring for the Functions host — the equivalent of the API's
/// observability, which this host had been missing (so its agents were invisible
/// in App Insights). Traces + logs export to Azure Monitor when a connection
/// string is present, and to the console in development.
/// </summary>
public static class FunctionsObservabilityExtensions
{
    public const string ServiceName = "SpeakerPipeline.Hosting.Functions";

    public static IServiceCollection AddFunctionsObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment)
    {
        // Functions supplies the connection string under this key.
        var aiConnection = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];

        void ConfigureResource(ResourceBuilder rb) => rb.AddService(ServiceName);

        // Hardening: redact the Telegram bot token from outbound HTTP spans. The
        // Bot API carries the token in the URL path, so without this it would land
        // in dependency telemetry. Configuring the options here applies the
        // enrichment regardless of who registers the HTTP-client instrumentation.
        services.Configure<HttpClientTraceInstrumentationOptions>(options =>
        {
            options.EnrichWithHttpRequestMessage = (activity, request) =>
            {
                if (request.RequestUri?.Host == "api.telegram.org")
                {
                    var redacted = TelegramTokenRedactor.Redact(request.RequestUri.AbsoluteUri);
                    activity.SetTag("url.full", redacted);
                    activity.SetTag("http.url", redacted);
                    activity.DisplayName = $"{request.Method} api.telegram.org";
                }
            };
        });

        var otel = services.AddOpenTelemetry()
            .ConfigureResource(ConfigureResource)
            .WithTracing(tracing =>
            {
                tracing.AddHttpClientInstrumentation();
                if (isDevelopment)
                {
                    tracing.AddConsoleExporter();
                }
            });

        // Azure Monitor export covers traces AND ILogger logs (the distro wires
        // the OpenTelemetry logging provider). In dev, the Functions host's
        // default console logging already surfaces the same logs.
        if (!string.IsNullOrWhiteSpace(aiConnection))
        {
            otel.UseAzureMonitor(o => o.ConnectionString = aiConnection);
        }

        return services;
    }
}
