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

    /// <summary>
    /// A <see cref="EventCategory.SubmitNow"/> event whose CFP deadline falls
    /// within this many days is flagged urgent. The daily run gives day-level
    /// coverage instead of waiting for the weekly scoring digest.
    /// </summary>
    public int UrgentDeadlineDays { get; set; } = 7;

    /// <summary>
    /// Committed engagements whose start falls within this many days of an event
    /// (either side) count toward its prep congestion. Mirrors the rubric's
    /// "surrounding 4 weeks".
    /// </summary>
    public int PrepWindowDays { get; set; } = 28;

    /// <summary>
    /// The number of nearby committed engagements at or above which an event is
    /// flagged for prep congestion (<see cref="EventRecord.PrepConflictFlag"/>).
    /// </summary>
    public int PrepCongestionThreshold { get; set; } = 2;
}
