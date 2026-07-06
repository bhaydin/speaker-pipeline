using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Telegram;

/// <summary>
/// Turns an inbound Telegram message into a pipeline action against the API and
/// produces the reply text. Pure of HTTP — the webhook Function owns sending the
/// reply — so the full command surface is unit-testable against a fake API client.
/// </summary>
public sealed class TelegramCommandRouter(
    ISpeakerPipelineApiClient api,
    IEventIngestService ingest,
    IOptions<TelegramOptions> options,
    ILogger<TelegramCommandRouter> logger)
{
    private readonly TelegramOptions _options = options.Value;

    public async Task<TelegramReply?> RouteAsync(TelegramUpdate update, CancellationToken ct = default)
    {
        if (update.Message is not { } message || message.Chat is not { } chat)
        {
            return null; // edited messages, channel posts, etc. — nothing to do
        }

        if (string.IsNullOrWhiteSpace(message.Text))
        {
            return null;
        }

        // Authorization: only the configured chat may drive the pipeline.
        if (!_options.Enabled || _options.ChatId == 0)
        {
            logger.LogWarning("Telegram: rejecting message because Telegram is disabled or ChatId is not configured.");
            return new TelegramReply(chat.Id, "Not authorized.");
        }

        if (chat.Id != _options.ChatId)
        {
            logger.LogWarning("Telegram: ignoring message from unauthorized chat {ChatId}.", chat.Id);
            return new TelegramReply(chat.Id, "Not authorized.");
        }

        var (command, argument) = Parse(message.Text);
        logger.LogInformation("Telegram command '{Command}' from chat {ChatId}.", command, chat.Id);

        var body = command switch
        {
            "/start" or "/help" => HelpText,
            "/status" => await StatusAsync(ct),
            "/topic" => await AddTopicAsync(argument, ct),
            "/track" => await TrackAsync(argument, ct),
            "/promote" => await PromoteAsync(argument, ct),
            "/approve" => await ApplyAsync(argument, PipelineAction.Intend, "Intent to submit — via Telegram", ct),
            "/pass" => await ApplyAsync(argument, PipelineAction.Skip, "Passed — via Telegram", ct),
            _ => $"Unknown command <code>{TelegramText.Escape(command)}</code>.\n\n{HelpText}",
        };

        return new TelegramReply(chat.Id, body);
    }

    // ---- commands ----------------------------------------------------------

    private async Task<string> StatusAsync(CancellationToken ct)
    {
        var events = await api.GetEventsAsync(ct: ct);
        if (events.Count == 0)
        {
            return "📊 <b>Pipeline</b>\nNo events tracked yet.";
        }

        var counts = events
            .GroupBy(e => e.Category)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key} {g.Count()}");

        var actionable = events
            .Where(e => e.Category is EventCategory.SubmitNow or EventCategory.Outreach)
            .OrderBy(e => e.CfpDeadline ?? DateTimeOffset.MaxValue)
            .Take(8)
            .ToList();

        var sb = new StringBuilder();
        sb.Append("📊 <b>Pipeline</b>\n");
        sb.Append(string.Join("  ·  ", counts));

        if (actionable.Count > 0)
        {
            sb.Append("\n\n<b>Actionable</b>\n");
            foreach (var e in actionable)
            {
                sb.Append("• ")
                  .Append(TelegramText.Escape(e.Name))
                  .Append(" <code>")
                  .Append(TelegramText.Escape(e.Slug))
                  .Append("</code> — ")
                  .Append(e.Category)
                  .Append(", ")
                  .Append(FormatDeadline(e.CfpDeadline))
                  .Append('\n');
            }
            sb.Append("\nReply <code>/approve &lt;slug&gt;</code> or <code>/pass &lt;slug&gt;</code>.");
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<string> AddTopicAsync(string argument, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            return "Usage: <code>/topic &lt;your idea&gt;</code>";
        }

        var slug = TelegramText.Slugify(argument);
        if (slug.Length == 0)
        {
            return "That topic didn't produce a usable id — try a few words.";
        }

        await api.UpsertTopicAsync(new TopicRecord
        {
            TopicId = slug,
            Title = argument.Trim(),
            Stage = TopicStage.Idea,
            Source = TopicSource.Telegram,
            CreatedUtc = DateTimeOffset.UtcNow,
        }, ct);

        return $"✅ Topic queued: \"{TelegramText.Escape(argument.Trim())}\"\n<code>{slug}</code> · stage Idea";
    }

    private async Task<string> TrackAsync(string argument, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            return "Usage: <code>/track &lt;url&gt; [note]</code>";
        }

        // First whitespace-delimited token is the URL; the remainder is an optional note.
        var space = argument.IndexOf(' ', StringComparison.Ordinal);
        var url = space < 0 ? argument.Trim() : argument[..space].Trim();
        var note = space < 0 ? null : argument[(space + 1)..].Trim();

        try
        {
            var result = await ingest.IngestAsync(url, note, ct);
            var mark = result.Status switch
            {
                IngestStatus.Created => "✅",
                IngestStatus.Updated => "♻️",
                IngestStatus.AlreadyTracked => "ℹ️",
                _ => "⚠️",
            };
            var slug = string.IsNullOrEmpty(result.Slug) ? "" : $"\n<code>{TelegramText.Escape(result.Slug)}</code>";
            return $"{mark} {TelegramText.Escape(result.Message)}{slug}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Telegram: /track failed for '{Url}'.", url);
            return "⚠️ Couldn't ingest that URL. Try again in a bit.";
        }
    }

    private async Task<string> PromoteAsync(string argument, CancellationToken ct)
    {
        var slug = argument.Trim();
        if (string.IsNullOrWhiteSpace(slug))
        {
            return "Usage: <code>/promote &lt;event-slug&gt;</code>  (promotes a quarantined candidate to scoring)";
        }

        try
        {
            var updated = await api.ApplyPipelineActionAsync(
                slug, new PipelineActionRequest { Action = PipelineAction.Monitor, Note = "Promoted from quarantine — via Telegram" }, ct);
            return $"⬆️ {TelegramText.Escape(updated.Name)} → <b>{updated.Category}</b> (queued for scoring)";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Telegram: /promote failed for '{Slug}'.", slug);
            return $"⚠️ Couldn't promote <code>{TelegramText.Escape(slug)}</code>. Check the slug (from the digest).";
        }
    }

    private async Task<string> ApplyAsync(string argument, PipelineAction action, string note, CancellationToken ct)
    {
        var slug = argument.Trim();
        if (string.IsNullOrWhiteSpace(slug))
        {
            var verb = action == PipelineAction.Intend ? "/approve" : "/pass";
            return $"Usage: <code>{verb} &lt;event-slug&gt;</code>  (get slugs from /status)";
        }

        try
        {
            var updated = await api.ApplyPipelineActionAsync(slug, new PipelineActionRequest { Action = action, Note = note }, ct);
            var mark = action == PipelineAction.Intend ? "✅" : "🚫";
            return $"{mark} {TelegramText.Escape(updated.Name)} → <b>{updated.Category}</b>";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Telegram: pipeline action {Action} failed for '{Slug}'.", action, slug);
            return $"⚠️ Couldn't apply to <code>{TelegramText.Escape(slug)}</code>. Check the slug (from /status).";
        }
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>Splits "/cmd@bot rest of text" into ("/cmd", "rest of text").</summary>
    internal static (string Command, string Argument) Parse(string text)
    {
        var trimmed = text.Trim();
        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        var head = space < 0 ? trimmed : trimmed[..space];
        var argument = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();

        // Strip a "@botname" suffix Telegram appends in group chats.
        var at = head.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            head = head[..at];
        }

        return (head.ToLowerInvariant(), argument);
    }

    internal static string FormatDeadline(DateTimeOffset? deadline)
    {
        if (deadline is not { } d)
        {
            return "no deadline";
        }

        var now = DateTimeOffset.UtcNow;
        var date = d.ToString("yyyy-MM-dd");
        if (d < now)
        {
            return $"closed {date}";
        }

        var days = (int)Math.Ceiling((d - now).TotalDays);
        return days switch
        {
            0 => $"due today ({date})",
            1 => $"1 day ({date})",
            _ => $"{days} days ({date})",
        };
    }

    internal const string HelpText =
        "🤖 <b>Speaker Pipeline</b>\n" +
        "<code>/status</code> — pipeline counts + what's actionable\n" +
        "<code>/track &lt;url&gt; [note]</code> — ingest a CFP/event URL\n" +
        "<code>/promote &lt;slug&gt;</code> — send a quarantined candidate to scoring\n" +
        "<code>/topic &lt;idea&gt;</code> — queue a talk idea\n" +
        "<code>/approve &lt;slug&gt;</code> — mark intent to submit\n" +
        "<code>/pass &lt;slug&gt;</code> — drop an event\n" +
        "<code>/help</code> — this message";
}
