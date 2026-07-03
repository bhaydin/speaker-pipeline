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
    /// </summary>
    public string? TableEndpoint { get; set; }

    public string EventsTableName { get; set; } = "Events";
    public string SubmissionsTableName { get; set; } = "Submissions";
    public string TalksTableName { get; set; } = "Talks";
    public string TopicsTableName { get; set; } = "Topics";
    public string BlackoutsTableName { get; set; } = "Blackouts";
    public string NotificationLogTableName { get; set; } = "NotificationLog";
}
