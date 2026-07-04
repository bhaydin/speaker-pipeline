namespace SpeakerPipeline.Agents.TrackerMaintenance;

/// <summary>
/// Configuration bound from the "TrackerMaintenance" section.
/// </summary>
public sealed class TrackerMaintenanceOptions
{
    public const string SectionName = "TrackerMaintenance";

    public string AgentName { get; set; } = "tracker-maintenance";
    public string AgentVersion { get; set; } = "v1";

    /// <summary>
    /// Maximum number of events reconciled in one run. Keeps a single run bounded;
    /// the table is small (&lt;200 rows) so the default covers the whole set.
    /// </summary>
    public int MaxEventsPerRun { get; set; } = 200;
}
