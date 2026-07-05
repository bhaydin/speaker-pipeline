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
        var result = AuthExtensions.ExpandAudiences("11111111-1111-1111-1111-111111111111");

        Assert.Contains("11111111-1111-1111-1111-111111111111", result);
        Assert.Contains("api://11111111-1111-1111-1111-111111111111", result);
    }

    [Fact]
    public void ExpandAudiences_from_api_uri_adds_bare_guid()
    {
        var result = AuthExtensions.ExpandAudiences("api://11111111-1111-1111-1111-111111111111");

        Assert.Contains("api://11111111-1111-1111-1111-111111111111", result);
        Assert.Contains("11111111-1111-1111-1111-111111111111", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void ExpandAudiences_empty_input_yields_empty(string? audience)
        => Assert.Empty(AuthExtensions.ExpandAudiences(audience));

    [Fact]
    public void ExpandIssuers_from_v2_authority_accepts_both_v1_and_v2()
    {
        var result = AuthExtensions.ExpandIssuers(
            "https://login.microsoftonline.com/22222222-2222-2222-2222-222222222222/v2.0");

        // v2.0 issuer (interactive / v2 tokens)
        Assert.Contains("https://login.microsoftonline.com/22222222-2222-2222-2222-222222222222/v2.0", result);
        // v1.0 issuer (Managed Identity tokens)
        Assert.Contains("https://sts.windows.net/22222222-2222-2222-2222-222222222222/", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not-a-uri")]
    [InlineData("https://login.microsoftonline.com/")] // no tenant segment
    public void ExpandIssuers_unparseable_yields_empty(string? authority)
        => Assert.Empty(AuthExtensions.ExpandIssuers(authority));
}
