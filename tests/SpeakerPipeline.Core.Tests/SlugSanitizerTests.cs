using SpeakerPipeline.Core;

namespace SpeakerPipeline.Core.Tests;

public class SlugSanitizerTests
{
    [Theory]
    [InlineData("Northwoods Tech Summit 2027", "northwoods-tech-summit-2027")]
    [InlineData("  Data-Driven WI 2026  ", "data-driven-wi-2026")]
    [InlineData("CFP.ninja / Cloud #Conf", "cfp-ninja-cloud-conf")]
    [InlineData("Already-A-Slug", "already-a-slug")]
    public void Sanitize_normalizes_into_a_safe_slug(string raw, string expected)
    {
        Assert.Equal(expected, SlugSanitizer.Sanitize(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("///")]
    public void Sanitize_throws_for_unrecoverable_input(string raw)
    {
        Assert.Throws<ArgumentException>(() => SlugSanitizer.Sanitize(raw));
    }

    [Theory]
    [InlineData("northwoods-tech-summit-2027", true)]
    [InlineData("data-driven-wi-2026", true)]
    [InlineData("has/slash", false)]
    [InlineData("has?question", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_rejects_forbidden_characters(string? candidate, bool expected)
    {
        Assert.Equal(expected, SlugSanitizer.IsValid(candidate));
    }
}
