using System.Net;
using System.Text;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Notifications;

/// <summary>
/// Formats a discovery run's outcome into a notification — what entered or
/// changed in the tracker on this run. Takes generic <see cref="DigestItem"/>s
/// so this stays decoupled from the discovery agent. Always returns a
/// notification (a run is a heartbeat worth confirming even when nothing changed).
/// </summary>
public static class DiscoveryDigest
{
    public static Notification Build(IReadOnlyList<DigestItem> items)
    {
        var newCount = items.Count(i => i.IsNew);
        var updatedCount = items.Count - newCount;

        var body = new StringBuilder();
        body.Append("<h2>Discovery run</h2>");

        if (items.Count == 0)
        {
            body.Append("<p>No new or changed events this run.</p>");
        }
        else
        {
            body.Append($"<p><strong>{newCount}</strong> new, <strong>{updatedCount}</strong> updated.</p>");
            body.Append("<ul>");
            foreach (var item in items.OrderByDescending(i => i.IsNew).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase))
            {
                var tag = item.IsNew ? "NEW" : "updated";
                body.Append($"<li><strong>{Encode(item.Title)}</strong> ({tag}) — {Encode(item.Detail)}</li>");
            }
            body.Append("</ul>");
        }

        var tail = items.Count == 0 ? "nothing new" : $"{newCount} new, {updatedCount} updated";

        return new Notification
        {
            Subject = $"Speaker pipeline — discovery: {tail}",
            HtmlBody = body.ToString(),
            Urgency = NotificationUrgency.Digest,
            // No dedupe key: each run's outcome is worth its own confirmation.
        };
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
