namespace SpeakerPipeline.Storage;

/// <summary>
/// Configuration bound from the "Storage" section. The TableEndpoint is the
/// only required field; DefaultAzureCredential handles auth.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// Table Storage endpoint URI, e.g. "https://saexample.table.core.windows.net".
    /// Used with managed identity in deployed environments.
    /// </summary>
    public string? TableEndpoint { get; set; }

    /// <summary>
    /// Local-development / emulator escape hatch. When set, the client is built
    /// from this connection string instead of <see cref="TableEndpoint"/> +
    /// managed identity — intended only for Azurite. Never set this in a
    /// deployed environment; production authenticates with managed identity.
    /// Supply it at runtime (env var / user-secrets), never in committed config.
    /// </summary>
    public string? ConnectionString { get; set; }

    public string EventsTableName { get; set; } = "Events";
    public string SubmissionsTableName { get; set; } = "Submissions";
    public string TalksTableName { get; set; } = "Talks";
    public string TopicsTableName { get; set; } = "Topics";
    public string BlackoutsTableName { get; set; } = "Blackouts";
    public string NotificationLogTableName { get; set; } = "NotificationLog";
}
