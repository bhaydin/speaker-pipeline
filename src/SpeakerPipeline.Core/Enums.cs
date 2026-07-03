namespace SpeakerPipeline.Core;

public enum EventType
{
    Conference,
    CodeCamp,
    UserGroup,
    CommunityChapter,
    Workshop,
    Virtual,
}

public enum EventCategory
{
    Delivered,
    Accepted,
    SubmitNow,
    Submitted,
    Outreach,
    Monitor,
    Pass,
    Skip,
}

public enum Priority
{
    Committed,
    High,
    MediumHigh,
    Medium,
    Low,
    NA,
}

public enum EventFormat
{
    InPerson,
    Virtual,
    Hybrid,
}

public enum TalkFormat
{
    Talk,
    Workshop,
    Lightning,
    Panel,
}

public enum TravelBurden
{
    None,
    Low,
    Medium,
    High,
}

public enum SubmissionStatus
{
    Submitted,
    InReview,
    Accepted,
    Rejected,
    Withdrawn,
}

public enum Lane
{
    AgentOps,
    HybridAgents,
    M365Governance,
    PracticalEnterpriseAI,
}

public enum SourceSeenOn
{
    Sessionize,
    PaperCall,
    CfpNinja,
    Meetup,
    GlobalAI,
    Direct,
    Manual,
}

public enum Recommendation
{
    SubmitNow,
    Outreach,
    Monitor,
    Pass,
    Skip,
}

/// <summary>Lifecycle of a topic idea in the funnel. Built promotes into Talks.</summary>
public enum TopicStage
{
    Idea,
    Validated,
    Built,
}

/// <summary>Where a topic idea came from.</summary>
public enum TopicSource
{
    Claude,
    Copilot,
    Manual,
    Telegram,
    TechTrekkerPost,
}

/// <summary>How much work a topic implies before it can be delivered.</summary>
public enum EffortClass
{
    /// <summary>~1 week: new angles, demos, a fresh deck.</summary>
    NewTopic,

    /// <summary>Light: adapt an existing deck.</summary>
    DeckAdapt,
}

/// <summary>Whether a blackout is a hard block or a soft preference.</summary>
public enum BlackoutHardness
{
    Hard,
    Soft,
}

/// <summary>Outbound notification channel.</summary>
public enum NotificationChannel
{
    Telegram,
    Email,
    Teams,
}

/// <summary>Routing class for a notification.</summary>
public enum NotificationUrgency
{
    Urgent,
    Digest,
}

/// <summary>
/// The explicit pipeline transitions a consumer (MCP, Steward) can request.
/// The value set is deliberately closed: there is no ambiguous "submit" — the
/// caller must choose <see cref="Intend"/> (declaring intent) vs.
/// <see cref="Confirmed"/> (already submitted). This is the structural half of
/// the submit-intent-vs-confirmation ambiguity guard (BUILD_PLAN §3.2/§5).
/// </summary>
public enum PipelineAction
{
    /// <summary>Drop from the pipeline and never resurface.</summary>
    Skip,

    /// <summary>Keep watching, no action yet.</summary>
    Monitor,

    /// <summary>Brian intends to submit — not yet submitted.</summary>
    Intend,

    /// <summary>Brian has confirmed the submission was made.</summary>
    Confirmed,
}
