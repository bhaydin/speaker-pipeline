using FluentValidation;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Validation;

public sealed class EventUpsertValidator : AbstractValidator<EventRecord>
{
    public EventUpsertValidator()
    {
        RuleFor(e => e.Slug).NotEmpty().Must(SlugSanitizer.IsValid)
            .WithMessage("Slug must not contain '/', '\\', '#', '?', or control characters.");
        RuleFor(e => e.Name).NotEmpty();
        RuleFor(e => e.SchemaVersion).GreaterThan(0);
    }
}

public sealed class SubmissionUpsertValidator : AbstractValidator<SubmissionRecord>
{
    public SubmissionUpsertValidator()
    {
        RuleFor(s => s.EventSlug).NotEmpty().Must(SlugSanitizer.IsValid);
        RuleFor(s => s.SubmissionId).NotEmpty().Must(SlugSanitizer.IsValid);
        RuleFor(s => s.EventName).NotEmpty();
        RuleFor(s => s.TalkSlug).NotEmpty();
        RuleFor(s => s.TalkTitleUsed).NotEmpty();
        RuleFor(s => s.AbstractUsed).NotEmpty();
        RuleFor(s => s.SubmittedOnUtc).GreaterThan(DateTimeOffset.MinValue);
    }
}

public sealed class TalkUpsertValidator : AbstractValidator<TalkRecord>
{
    public TalkUpsertValidator()
    {
        RuleFor(t => t.Slug).NotEmpty().Must(SlugSanitizer.IsValid);
        RuleFor(t => t.CanonicalTitle).NotEmpty();
    }
}

public sealed class TopicUpsertValidator : AbstractValidator<TopicRecord>
{
    public TopicUpsertValidator()
    {
        RuleFor(t => t.TopicId).NotEmpty().Must(SlugSanitizer.IsValid)
            .WithMessage("TopicId must not contain '/', '\\', '#', '?', or control characters.");
        RuleFor(t => t.Title).NotEmpty();
        RuleFor(t => t.SchemaVersion).GreaterThan(0);
    }
}

public sealed class BlackoutUpsertValidator : AbstractValidator<BlackoutRecord>
{
    public BlackoutUpsertValidator()
    {
        RuleFor(b => b.BlackoutId).NotEmpty().Must(SlugSanitizer.IsValid)
            .WithMessage("BlackoutId must not contain '/', '\\', '#', '?', or control characters.");
        RuleFor(b => b.Reason).NotEmpty();
        RuleFor(b => b.EndDate).GreaterThanOrEqualTo(b => b.StartDate)
            .WithMessage("EndDate must be on or after StartDate.");
        RuleFor(b => b.SchemaVersion).GreaterThan(0);
    }
}

public sealed class ScoringDecisionValidator : AbstractValidator<ScoringDecision>
{
    public ScoringDecisionValidator()
    {
        RuleFor(d => d.EventSlug).NotEmpty().Must(SlugSanitizer.IsValid);
        RuleFor(d => d.Rationale).NotEmpty();
        RuleFor(d => d.FitScore).InclusiveBetween(1, 10);
        RuleFor(d => d.EffortScore).InclusiveBetween(1, 10);
        RuleFor(d => d.ConfidenceScore).InclusiveBetween(1, 10);
    }
}
