using SpeakerPipeline.Api.Auth;

namespace SpeakerPipeline.Api.Tests;

/// <summary>
/// Guards the auth config validation that keeps a misconfigured Authority from
/// producing an app that boots green but 500s every request (the JwtBearer
/// PostConfigure trap), and the audience expansion that accepts both v1.0 and
/// v2.0 token `aud` forms.
/// </summary>
public class AuthExtensionsTests
{
    [Theory]
    [InlineData("https://login.microsoftonline.com/tenant/v2.0", true)]
    [InlineData("https://example.org", true)]
    [InlineData("http://login.microsoftonline.com/tenant/v2.0", false)] // not https
    [InlineData("<placeholder>", false)]                                 // literal fallback that caused the outage
    [InlineData("login.microsoftonline.com/tenant/v2.0", false)]         // no scheme
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidHttpsAuthority_requires_absolute_https(string? authority, bool expected)
        => Assert.Equal(expected, AuthExtensions.IsValidHttpsAuthority(authority));

    [Fact]
    public void ExpandAudiences_from_guid_adds_api_uri()
    {
        var result = AuthExtensions.ExpandAudiences("bace4d6a-9ebd-4fac-a152-d1c67d593a41");

        Assert.Contains("bace4d6a-9ebd-4fac-a152-d1c67d593a41", result);
        Assert.Contains("api://bace4d6a-9ebd-4fac-a152-d1c67d593a41", result);
    }

    [Fact]
    public void ExpandAudiences_from_api_uri_adds_bare_guid()
    {
        var result = AuthExtensions.ExpandAudiences("api://bace4d6a-9ebd-4fac-a152-d1c67d593a41");

        Assert.Contains("api://bace4d6a-9ebd-4fac-a152-d1c67d593a41", result);
        Assert.Contains("bace4d6a-9ebd-4fac-a152-d1c67d593a41", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void ExpandAudiences_empty_input_yields_empty(string? audience)
        => Assert.Empty(AuthExtensions.ExpandAudiences(audience));
}
