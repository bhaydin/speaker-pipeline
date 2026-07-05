namespace SpeakerPipeline.Notifications;

/// <summary>
/// Configuration bound from the "Notifications" section.
/// </summary>
public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    public EmailLaneOptions Email { get; set; } = new();

    /// <summary>
    /// When true, a <b>scheduled</b> (timer) run that changed nothing sends no
    /// notification — cutting the daily "nothing new" heartbeat noise. Manual
    /// (HTTP) runs always confirm regardless. Default false (every run notifies).
    /// </summary>
    public bool SuppressEmptyScheduledRuns { get; set; }
}

/// <summary>
/// Email lane over Microsoft Graph <c>sendMail</c>. The mailbox lives in the
/// Haydin.ai tenant (separate from where the pipeline runs), so this
/// authenticates with an app registration in THAT tenant — client-credentials,
/// ClientSecret from Key Vault, never in code. A certificate is the hardening path.
/// </summary>
public sealed class EmailLaneOptions
{
    public bool Enabled { get; set; }

    /// <summary>Haydin.ai tenant id.</summary>
    public string? TenantId { get; set; }

    /// <summary>App registration (with Mail.Send application permission + admin consent).</summary>
    public string? ClientId { get; set; }

    /// <summary>Client secret — Key Vault reference.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Mailbox to send as, e.g. sender@example.com.</summary>
    public string? Sender { get; set; }

    /// <summary>Where notifications go.</summary>
    public string? Recipient { get; set; }

    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(TenantId)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(Sender)
        && !string.IsNullOrWhiteSpace(Recipient);
}
