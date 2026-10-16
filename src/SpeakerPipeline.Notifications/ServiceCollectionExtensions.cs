using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SpeakerPipeline.Notifications;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<NotificationOptions>()
            .Bind(configuration.GetSection(NotificationOptions.SectionName));
        services.AddOptions<PipelineNotificationOptions>().Bind(configuration.GetSection("Pipeline"));

        // Email lane over Graph. A typed HttpClient for the Graph call.
        services.AddHttpClient<EmailLane>(c => c.Timeout = TimeSpan.FromSeconds(20));
        services.AddTransient<INotificationLane>(sp => sp.GetRequiredService<EmailLane>());

        services.AddTransient<INotifier, Notifier>();
        services.AddSingleton(TimeProvider.System);
        services.AddTransient<DeliveryCoordinator>();
        services.AddTransient<NotificationOutbox>();
        services.AddTransient<RunReporter>();
        services.AddTransient<PipelineHealth>();
        return services;
    }
}
