using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Telegram;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Telegram client, the outbound lane (joins the Notifier's
    /// lanes), and the inbound command router. Call after AddNotifications so the
    /// Telegram lane is part of the same INotificationLane set.
    /// </summary>
    public static IServiceCollection AddTelegram(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TelegramOptions>()
            .Bind(configuration.GetSection(TelegramOptions.SectionName));

        services.AddHttpClient<TelegramClient>(c => c.Timeout = TimeSpan.FromSeconds(20));

        // Outbound: expose the lane through the shared abstraction so the Notifier
        // fans out to it without knowing Telegram exists.
        services.AddTransient<INotificationLane, TelegramLane>();

        // Inbound: the webhook Function resolves the router + client directly.
        services.AddTransient<TelegramCommandRouter>();

        return services;
    }
}
