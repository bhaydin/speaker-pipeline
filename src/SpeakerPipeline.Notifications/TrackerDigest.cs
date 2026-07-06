using System.Net;
using System.Text;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Notifications;

/// <summary>
/// Formats a tracker-maintenance run into a notification — the category
/// transitions it applied. Takes generic <see cref="DigestItem"/>s (Title = event
/// slug, Detail = "From → To") so this stays decoupled from the tracker agent.
/// Always returns a notification so every run confirms itself.
/// </summary>
public static class TrackerDigest
{
    public static Notification Build(IReadOnlyList<DigestItem> items, IReadOnlyList<DigestItem>? conflicts = null)
    {
        conflicts ??= [];
        var body = new StringBuilder();
        body.Append("<h2>Tracker maintenance run</h2>");

        if (items.Count == 0)
        {
            body.Append("<p>No category changes this run.</p>");
        }
        else
        {
            body.Append($"<p><strong>{items.Count}</strong> event(s) re-categorized.</p>");
            body.Append("<ul>");
            foreach (var item in items.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase))
            {
                body.Append($"<li><strong>{Encode(item.Title)}</strong> — {Encode(item.Detail)}</li>");
            }
            body.Append("</ul>");
        }

        if (conflicts.Count > 0)
        {
            body.Append($"<h3>Conflict flags ({conflicts.Count})</h3><ul>");
            foreach (var item in conflicts.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase))
            {
                body.Append($"<li><strong>{Encode(item.Title)}</strong> — {Encode(item.Detail)}</li>");
            }
            body.Append("</ul>");
        }

        var parts = new List<string>();
        if (items.Count > 0)
        {
            parts.Add($"{items.Count} changed");
        }
        if (conflicts.Count > 0)
        {
            parts.Add($"{conflicts.Count} conflict{(conflicts.Count == 1 ? "" : "s")}");
        }
        var tail = parts.Count == 0 ? "no changes" : string.Join(", ", parts);

        return new Notification
        {
            Subject = $"Speaker pipeline — tracker: {tail}",
            HtmlBody = body.ToString(),
            Urgency = NotificationUrgency.Digest,
        };
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
