using System.Globalization;
using System.Text;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Telegram;

public static class TelegramMessageFormatter
{
    public const int Limit = 4096;
    public static IReadOnlyList<NotificationPart> Parts(Notification notification)
    {
        if (notification.Groups.Count == 0)
            return SplitItem($"<b>{TelegramText.Escape(notification.Subject)}</b>\n\n", TelegramText.StripHtml(notification.HtmlBody), Limit)
                .Select((part, i) => new NotificationPart(i.ToString(CultureInfo.InvariantCulture), notification, part)).ToArray();
        var parts = new List<NotificationPart>();
        var summary = $"<b>{TelegramText.Escape(notification.Subject)}</b>\n\n";
        if (VisibleLength(summary) > Limit / 2)
        {
            foreach (var chunk in Chunks(notification.Subject, Limit - 4))
                parts.Add(new(parts.Count.ToString(CultureInfo.InvariantCulture), notification, $"<b>{TelegramText.Escape(chunk)}</b>"));
            summary = "";
        }
        var number = 0;
        foreach (var group in notification.Groups)
        {
            var heading = summary + $"<b>{TelegramText.Escape(group.Title)}</b>\n\n";
            var current = heading;
            var included = new List<NotificationItem>();
            foreach (var item in group.Items)
            {
                number++;
                var itemLimit = Limit - VisibleLength(heading) - (item.Actions ? 60 : 0);
                var prefix = $"{number}. {Link(item.Name, item.Url)}\n";
                var blocks = new List<string>();
                if (VisibleLength(prefix) > itemLimit - 44)
                {
                    // A source can supply an unusually long name. Continue the linked
                    // name itself, then its detail; never truncate it or break HTML.
                    blocks.AddRange(Chunks(item.Name, itemLimit - 44)
                        .Select(name => $"{number}. {Link(name, item.Url)}\n\n"));
                    prefix = $"{number}. Continued\n";
                }
                blocks.AddRange(SplitItem(prefix, item.Detail, itemLimit));
                if (item.Actions) blocks[^1] += $"<i>Item {number}: Awaiting action</i>\n\n";
                for (var index = 0; index < blocks.Count; index++)
                {
                    var block = blocks[index];
                    // A stale callback replaces its short pending label with a longer resolution.
                    // Reserve room per actionable item, including when many short items share a part.
                    var actionCount = included.Count(i => i.Actions) + (index == blocks.Count - 1 && item.Actions ? 1 : 0);
                    if (VisibleLength(current) + VisibleLength(block) + actionCount * 32 > Limit - 4) AddPart();
                    current += block;
                    if (index == blocks.Count - 1) included.Add(item with { Name = $"{number}." });
                }
            }
            if (current != heading) AddPart();
            void AddPart()
            {
                if (current != heading) parts.Add(new(parts.Count.ToString(CultureInfo.InvariantCulture), notification, current.TrimEnd(), included.ToArray()));
                current = heading;
                included.Clear();
            }
        }
        return parts;
    }

    public static string Link(string name, string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        ? $"<a href=\"{System.Net.WebUtility.HtmlEncode(uri.AbsoluteUri)}\">{TelegramText.Escape(name)}</a>" : TelegramText.Escape(name);

    public static int VisibleLength(string html) => System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", "")).Length;

    private static IReadOnlyList<string> SplitItem(string prefix, string detail, int limit)
    {
        var output = new List<string>();
        var room = limit - VisibleLength(prefix) - 12;
        if (room < 32) throw new InvalidOperationException("Item heading exceeds the Telegram message limit.");
        output.AddRange(Chunks(detail, room).Select(chunk => prefix + TelegramText.Escape(chunk) + "\n\n"));
        return output;
    }

    private static IReadOnlyList<string> Chunks(string value, int room)
    {
        var output = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        var text = new StringBuilder();
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (text.Length + element.Length > room)
            {
                if (text.Length > 0) output.Add(text.ToString());
                text.Clear();
            }
            if (element.Length <= room) text.Append(element);
            else foreach (var rune in element.EnumerateRunes())
            {
                if (text.Length + rune.Utf16SequenceLength > room) { output.Add(text.ToString()); text.Clear(); }
                text.Append(rune.ToString());
            }
        }
        if (text.Length > 0 || output.Count == 0) output.Add(text.ToString());
        return output;
    }
}
