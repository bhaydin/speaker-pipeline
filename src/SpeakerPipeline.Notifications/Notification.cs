using SpeakerPipeline.Core;

namespace SpeakerPipeline.Notifications;

/// <summary>
/// A channel-agnostic outbound notification. Callers build one and hand it to
/// the <see cref="INotifier"/>; lanes render it for their medium.
/// </summary>
public sealed record Notification
{
    public required string Subject { get; init; }

    /// <summary>HTML body (lanes that can't render HTML strip it to text).</summary>
    public required string HtmlBody { get; init; }

    public NotificationUrgency Urgency { get; init; } = NotificationUrgency.Digest;

    /// <summary>Idempotency key — the Notifier skips a send when this key already fired this period.</summary>
    public string? DedupeKey { get; init; }

    /// <summary>Slug of the Event/Topic this refers to, for the log.</summary>
    public string? EntityRef { get; init; }

    public string? DeliveryKey { get; init; }
    public IReadOnlyList<NotificationGroup> Groups { get; init; } = [];
}

public sealed record NotificationGroup(string Title, IReadOnlyList<NotificationItem> Items);
public sealed record NotificationItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Url { get; init; }
    public required string Detail { get; init; }
    public bool Actions { get; init; }
    public string? EventSlug { get; init; }
    public string? ExpectedETag { get; init; }
    public string? DecisionId { get; init; }
}

public sealed record NotificationPart(string Id, Notification Notification, string? Text = null,
    IReadOnlyList<NotificationItem>? Items = null);
public sealed record NotificationDelivery(string Status, IReadOnlyList<string>? MessageIds = null);

/// <summary>One line of a digest — a generic shape so the Notifier stays decoupled from any agent.</summary>
public sealed record DigestItem(string Title, bool IsNew, string Detail);
