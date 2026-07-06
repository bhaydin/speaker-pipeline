using System.Net;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Notifications;

/// <summary>
/// Builds the urgent notification for a SubmitNow event whose CFP deadline is
/// imminent (BUILD_PLAN.GO_FORWARD B3). Carries an <see cref="NotificationUrgency.Urgent"/>
/// urgency and a dedupe key scoped to the event + deadline date, so the same
/// closing CFP alerts once per period rather than every daily run.
/// </summary>
public static class UrgentDeadlineNotice
{
    public static Notification Build(string eventSlug, string eventName, DateTimeOffset deadline, int daysRemaining)
    {
        var days = daysRemaining <= 0
            ? "closes today"
            : daysRemaining == 1 ? "closes tomorrow" : $"closes in {daysRemaining} days";
        var date = deadline.ToString("yyyy-MM-dd");

        return new Notification
        {
            Subject = $"⏰ CFP {days}: {eventName}",
            HtmlBody =
                $"<h2>CFP deadline approaching</h2>" +
                $"<p><strong>{Encode(eventName)}</strong> is marked <strong>SubmitNow</strong> and its CFP {Encode(days)} ({date}).</p>" +
                $"<p>Slug: <code>{Encode(eventSlug)}</code></p>",
            Urgency = NotificationUrgency.Urgent,
            // One alert per event + deadline date per period; the daily sweep won't re-nag.
            DedupeKey = $"deadline-{eventSlug}-{date}",
            EntityRef = eventSlug,
        };
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
