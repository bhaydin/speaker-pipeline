using System.Diagnostics;

namespace SpeakerPipeline.Telegram;

/// <summary>Keep credentials and provider exception text out of dependency telemetry.</summary>
public static class OutboundHttpPrivacy
{
    public static void EnrichRequest(Activity activity, HttpRequestMessage request)
    {
        if (request.RequestUri is not { } uri) return;
        var safeUri = TelegramTokenRedactor.Redact(uri.GetLeftPart(UriPartial.Path));
        activity.SetTag("url.full", safeUri);
        activity.SetTag("http.url", safeUri);
        activity.SetTag("url.query", uri.Query.Length > 0 ? "[redacted]" : null);
        // Activity names must never be composed from a credential-bearing URI.
        activity.DisplayName = $"{request.Method} {uri.Host}";
    }

    public static void EnrichException(Activity activity, Exception exception)
    {
        // HttpClient instrumentation records no raw exception event. Retain only type.
        activity.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
        activity.SetTag("exception.message", null);
        activity.SetTag("exception.stacktrace", null);
        activity.SetTag("error.type", exception.GetType().FullName);
    }
}
