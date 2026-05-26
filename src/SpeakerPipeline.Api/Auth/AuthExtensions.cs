using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace SpeakerPipeline.Api.Auth;

/// <summary>
/// Entra ID JwtBearer auth, wired into the pipeline.
///
/// In <see cref="IHostEnvironment.IsDevelopment"/> we keep validation loose
/// so the dev loop is not blocked on issuing real tokens. Any non-empty
/// bearer is accepted via the <see cref="DevAuthenticationHandler"/>.
///
/// PRODUCTION CHECKLIST — see CLAUDE.md "API as the only data boundary":
///   1. Set Authentication:Authority and Authentication:Audience from Key Vault.
///      Authority example: https://login.microsoftonline.com/&lt;tenant-id&gt;/v2.0
///   2. Register an Entra ID app for the API and one per consumer
///      (Functions host, OpenClaw runtime). Grant scope or app-role
///      assignments accordingly.
///   3. Assign managed identities to the Function App and the OpenClaw host;
///      grant them the API's scope.
///   4. Remove or feature-flag the DevAuthenticationHandler so it cannot
///      load in production environments.
/// </summary>
public static class AuthExtensions
{
    public const string PolicyName = "SpeakerPipelineUser";

    public static IServiceCollection AddSpeakerPipelineAuth(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var authSection = configuration.GetSection("Authentication");

        var builder = services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme);

        if (environment.IsDevelopment())
        {
            // Dev: accept any bearer token so contributors can exercise endpoints
            // without configuring Entra. Replace before any non-dev deploy.
            builder.AddScheme<DevAuthenticationOptions, DevAuthenticationHandler>(
                JwtBearerDefaults.AuthenticationScheme,
                options => { /* defaults */ });
        }
        else
        {
            builder.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Authority = authSection["Authority"];
                options.Audience = authSection["Audience"];
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                };
            });
        }

        services.AddAuthorization(options =>
        {
            options.AddPolicy(PolicyName, policy => policy.RequireAuthenticatedUser());
            options.DefaultPolicy = options.GetPolicy(PolicyName)!;
        });

        return services;
    }
}
