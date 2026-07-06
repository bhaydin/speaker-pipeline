using System.Net;
using System.Text;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Notifications;

/// <summary>
/// Formats a discovery run's outcome into a notification — what entered or
/// changed in the tracker, what was quarantined for review, and a funnel that
/// accounts for every candidate (so a quiet run reads as "here's where they
/// went", never "the system is dead"). Takes generic shapes so this stays
/// decoupled from the discovery agent. Always returns a notification.
/// </summary>
public static class DiscoveryDigest
{
    public static Notification Build(
        IReadOnlyList<DigestItem> items,
        IReadOnlyList<DigestItem>? quarantined = null,
        DiscoveryFunnelView? funnel = null)
    {
        quarantined ??= [];
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

        if (quarantined.Count > 0)
        {
            body.Append($"<h3>Quarantine ({quarantined.Count}) — review</h3>");
            body.Append("<ul>");
            foreach (var item in quarantined.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase))
            {
                body.Append($"<li><strong>{Encode(item.Title)}</strong> — {Encode(item.Detail)}</li>");
            }
            body.Append("</ul>");
        }

        if (funnel is not null)
        {
            body.Append(RenderFunnel(funnel));
        }

        var tail = items.Count == 0 ? "nothing new" : $"{newCount} new, {updatedCount} updated";
        if (quarantined.Count > 0)
        {
            tail += $", {quarantined.Count} to review";
        }

        return new Notification
        {
            Subject = $"Speaker pipeline — discovery: {tail}",
            HtmlBody = body.ToString(),
            Urgency = NotificationUrgency.Digest,
            // No dedupe key: each run's outcome is worth its own confirmation.
        };
    }

    private static string RenderFunnel(DiscoveryFunnelView f)
    {
        var body = new StringBuilder();
        body.Append("<h3>Funnel</h3>");
        body.Append($"<p>{f.Targets} candidates → {f.Extracted} extracted → {f.PassedFloor} passed floor → " +
                    $"{f.New} new / {f.Updated} updated / {f.Quarantined} quarantined.</p>");

        if (f.Dropped.Count > 0)
        {
            var drops = string.Join(", ", f.Dropped.OrderByDescending(kv => kv.Value).Select(kv => $"{Encode(kv.Key)}: {kv.Value}"));
            body.Append($"<p><em>Dropped</em> — {drops}</p>");
        }

        if (f.CandidatesBySource.Count > 0)
        {
            var sources = string.Join(", ", f.CandidatesBySource.OrderByDescending(kv => kv.Value).Select(kv => $"{Encode(kv.Key)}: {kv.Value}"));
            body.Append($"<p><em>Sources</em> — {sources}</p>");
        }

        body.Append($"<p><em>Spend</em> — {f.TotalTokens:N0} tokens ({f.InputTokens:N0} in / {f.OutputTokens:N0} out), {f.SearchQueries} search queries.</p>");
        return body.ToString();
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}

/// <summary>
/// A decoupled view of the discovery funnel for rendering — mirrors the agent's
/// funnel without the Notifications project referencing the discovery agent.
/// </summary>
public sealed record DiscoveryFunnelView(
    int Targets,
    int Extracted,
    int PassedFloor,
    int New,
    int Updated,
    int Quarantined,
    IReadOnlyDictionary<string, int> Dropped,
    IReadOnlyDictionary<string, int> CandidatesBySource,
    long InputTokens,
    long OutputTokens,
    int SearchQueries)
{
    public long TotalTokens => InputTokens + OutputTokens;
}
