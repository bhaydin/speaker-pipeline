using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace SpeakerPipeline.Api.Observability;

/// <summary>
/// OpenTelemetry wiring. Exporters are config-driven:
///   - Console (default) and File (optional) for dev/offline.
///   - Azure Monitor when ApplicationInsights:ConnectionString is set.
/// </summary>
public static class ObservabilityExtensions
{
    public const string ServiceName = "SpeakerPipeline.Api";
    public static readonly System.Diagnostics.ActivitySource ApiActivitySource = new(ServiceName);

    public static IServiceCollection AddSpeakerPipelineObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var serviceVersion = typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var environmentName = environment.EnvironmentName;

        void ConfigureResource(ResourceBuilder rb) => rb
            .AddService(serviceName: ServiceName, serviceVersion: serviceVersion)
            .AddAttributes([
                new KeyValuePair<string, object>("deployment.environment", environmentName),
            ]);

        var aiConnection = configuration["ApplicationInsights:ConnectionString"];

        var otel = services.AddOpenTelemetry()
            .ConfigureResource(ConfigureResource)
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(ServiceName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (environment.IsDevelopment())
                {
                    tracing.AddConsoleExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (environment.IsDevelopment())
                {
                    metrics.AddConsoleExporter();
                }
            });

        if (!string.IsNullOrWhiteSpace(aiConnection))
        {
            otel.UseAzureMonitor(o => o.ConnectionString = aiConnection);
        }

        services.AddLogging(logging =>
        {
            logging.AddOpenTelemetry(o =>
            {
                var rb = ResourceBuilder.CreateDefault();
                ConfigureResource(rb);
                o.SetResourceBuilder(rb);
                if (environment.IsDevelopment())
                {
                    o.AddConsoleExporter();
                }
                o.IncludeScopes = true;
                o.IncludeFormattedMessage = true;
            });
        });

        return services;
    }
}
