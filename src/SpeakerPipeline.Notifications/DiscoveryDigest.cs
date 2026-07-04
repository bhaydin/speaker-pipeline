using System.Net;
using System.Text;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Notifications;

/// <summary>
/// Formats a discovery run's outcome into a notification — what entered or
/// changed in the tracker on this run. Takes generic <see cref="DigestItem"/>s
/// so this stays decoupled from the discovery agent.
/// </summary>
public static class DiscoveryDigest
{
    /// <summary>
    /// Builds a notification for the run, or <c>null</c> when nothing changed
    /// (so the caller sends nothing rather than an empty "0 new" email).
    /// </summary>
    public static Notification? Build(IReadOnlyList<DigestItem> items)
    {
        if (items.Count == 0)
        {
            return null;
        }

        var newCount = items.Count(i => i.IsNew);
        var updatedCount = items.Count - newCount;

        var body = new StringBuilder();
        body.Append("<h2>Discovery run</h2>");
        body.Append($"<p><strong>{newCount}</strong> new, <strong>{updatedCount}</strong> updated.</p>");
        body.Append("<ul>");
        foreach (var item in items.OrderByDescending(i => i.IsNew).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase))
        {
            var tag = item.IsNew ? "NEW" : "updated";
            body.Append($"<li><strong>{Encode(item.Title)}</strong> ({tag}) — {Encode(item.Detail)}</li>");
        }
        body.Append("</ul>");

        return new Notification
        {
            Subject = $"Speaker pipeline — discovery: {newCount} new, {updatedCount} updated",
            HtmlBody = body.ToString(),
            Urgency = NotificationUrgency.Digest,
            // No dedupe key: each run's change-set is distinct and worth sending.
        };
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
