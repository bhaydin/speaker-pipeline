namespace SpeakerPipeline.Telegram;

/// <summary>
/// The subset of Telegram's Update object the bot reads. Deserialized with a
/// snake_case naming policy, so C# <c>UpdateId</c> binds to JSON <c>update_id</c>.
/// The full Update schema is large; we take only what the command router needs.
/// </summary>
public sealed record TelegramUpdate
{
    public long UpdateId { get; init; }
    public TelegramMessage? Message { get; init; }
}

public sealed record TelegramMessage
{
    public long MessageId { get; init; }
    public TelegramChat? Chat { get; init; }
    public TelegramUser? From { get; init; }
    public string? Text { get; init; }
}

public sealed record TelegramChat
{
    public long Id { get; init; }
}

public sealed record TelegramUser
{
    public long Id { get; init; }
    public string? Username { get; init; }
}

/// <summary>A message the bot should send back, addressed to a chat.</summary>
public sealed record TelegramReply(long ChatId, string Text);
