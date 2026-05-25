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
