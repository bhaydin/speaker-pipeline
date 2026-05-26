using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace SpeakerPipeline.Api.Auth;

/// <summary>
/// Development-only auth handler. Accepts any non-empty Authorization header
/// that starts with "Bearer ". Never load this in non-development environments.
/// </summary>
internal sealed class DevAuthenticationHandler(
    IOptionsMonitor<DevAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<DevAuthenticationOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var raw = header.ToString();
        if (!raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.Fail("Authorization header must be a Bearer token in dev."));
        }

        var token = raw["Bearer ".Length..].Trim();
        if (token.Length == 0)
        {
            return Task.FromResult(AuthenticateResult.Fail("Empty bearer token."));
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "dev-user"),
                new Claim(ClaimTypes.Name, "Local Dev"),
            ],
            Scheme.Name);

        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

internal sealed class DevAuthenticationOptions : AuthenticationSchemeOptions;
