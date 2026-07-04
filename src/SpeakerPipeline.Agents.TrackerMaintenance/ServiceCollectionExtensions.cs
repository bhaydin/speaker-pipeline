using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SpeakerPipeline.Agents.TrackerMaintenance;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTrackerMaintenanceAgent(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TrackerMaintenanceOptions>()
            .Bind(configuration.GetSection(TrackerMaintenanceOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<TrackerMaintenanceAgent>();
        return services;
    }
}
