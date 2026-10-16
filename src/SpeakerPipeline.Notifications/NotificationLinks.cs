using SpeakerPipeline.Core;

namespace SpeakerPipeline.Notifications;

public static class NotificationLinks
{
    public static string? ForEvent(EventRecord record)
        => new[] { record.CfpUrl, record.EventUrl }.FirstOrDefault(value =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https");
}
