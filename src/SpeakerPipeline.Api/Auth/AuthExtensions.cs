using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Auth;

/// <summary>
/// Entra ID JwtBearer auth, wired into the pipeline.
///
/// In <see cref="IHostEnvironment.IsDevelopment"/> we keep validation loose
/// so the dev loop is not blocked on issuing real tokens. Any non-empty
/// bearer is accepted via the <see cref="DevAuthenticationHandler"/>.
///
/// Production callers require the Pipeline.Access app role. Maintenance also
/// requires the separate operator-only Pipeline.Maintenance role and a bounded
/// maintenance window. Azure hosts reject Development configuration at startup.
/// </summary>
public static class AuthExtensions
{
    public const string PolicyName = "SpeakerPipelineUser";
    public const string MaintenancePolicyName = "PipelineMaintenance";
    public const string AccessRole = "Pipeline.Access";
    public const string MaintenanceRole = "Pipeline.Maintenance";

    public static IServiceCollection AddSpeakerPipelineAuth(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        RuntimeSafety.ValidateHost(environment.EnvironmentName, RuntimeSafety.IsAzureHost);
        var authSection = configuration.GetSection("Authentication");

        var builder = services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme);

        if (environment.IsDevelopment())
        {
            // Local development only; the host guard above rejects this in Azure.
            builder.AddScheme<DevAuthenticationOptions, DevAuthenticationHandler>(
                JwtBearerDefaults.AuthenticationScheme,
                options => { /* defaults */ });
        }
        else
        {
            var authority = authSection["Authority"];
            var audience = authSection["Audience"];

            // Fail fast at startup rather than letting JwtBearer throw from its
            // PostConfigure on the first request. A missing/placeholder/non-https
            // Authority otherwise produces an app that boots green but 500s every
            // request — including AllowAnonymous /health, because UseAuthentication
            // runs the default scheme on all requests. Surface it as a boot failure.
            if (!IsValidHttpsAuthority(authority))
            {
                throw new InvalidOperationException(
                    "Authentication:Authority must be an absolute https URL outside Development " +
                    "(e.g. https://login.microsoftonline.com/<tenant-id>/v2.0). Current value: " +
                    $"'{authority ?? "<null>"}'. Set the App Service app setting " +
                    "Authentication__Authority (ideally a Key Vault reference).");
            }

            if (string.IsNullOrWhiteSpace(audience))
            {
                throw new InvalidOperationException(
                    "Authentication:Audience must be set outside Development (the API's client ID " +
                    "or api://<client-id> App ID URI). Set the App Service app setting " +
                    "Authentication__Audience (ideally a Key Vault reference).");
            }

            var validIssuers = ExpandIssuers(authority);

            builder.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Authority = authority;
                options.MapInboundClaims = false;
                var parameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    RoleClaimType = "roles",
                    NameClaimType = "name",
                    // Accept both the client-ID GUID and the api://<client-id> App ID
                    // URI. v2.0 access tokens carry the GUID in `aud`; v1.0 tokens carry
                    // the URI. Accepting both avoids 401s that hinge on how the caller
                    // requested its token.
                    ValidAudiences = ExpandAudiences(audience),
                };

                // Managed Identity access tokens are v1.0 — issuer
                // https://sts.windows.net/<tenant>/ — but a v2.0 Authority otherwise
                // validates only the v2.0 issuer (https://login.microsoftonline.com/
                // <tenant>/v2.0), so MI callers (the Functions host) get 401s.
                // Accept both issuers for the tenant; signing keys still come from
                // the Authority's metadata. Leave unset if the tenant can't be
                // parsed so metadata-derived validation applies instead of rejecting all.
                if (validIssuers.Length > 0)
                {
                    parameters.ValidIssuers = validIssuers;
                }

                options.TokenValidationParameters = parameters;
            });
        }

        services.AddAuthorization(options =>
        {
            options.AddPolicy(PolicyName, policy => policy.RequireAuthenticatedUser().RequireRole(AccessRole));
            options.AddPolicy(MaintenancePolicyName, policy =>
                policy.RequireAuthenticatedUser().RequireRole(AccessRole).RequireRole(MaintenanceRole));
            options.DefaultPolicy = options.GetPolicy(PolicyName)!;
        });

        return services;
    }

    /// <summary>
    /// True when <paramref name="authority"/> is an absolute https URL, the only
    /// form JwtBearer accepts for metadata discovery outside development.
    /// </summary>
    internal static bool IsValidHttpsAuthority(string? authority)
    {
        authority = authority?.Trim();
        return Uri.TryCreate(authority, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps;
    }

    /// <summary>
    /// Expands a single configured audience into the set actually seen across
    /// token versions: the bare client-ID GUID and the api://&lt;client-id&gt;
    /// App ID URI. Returns both regardless of which form was configured.
    /// </summary>
    internal static string[] ExpandAudiences(string? audience)
    {
        if (string.IsNullOrWhiteSpace(audience))
        {
            return [];
        }

        audience = audience.Trim();

        const string apiPrefix = "api://";
        var audiences = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { audience };
        if (audience.StartsWith(apiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            audiences.Add(audience[apiPrefix.Length..]);
        }
        else
        {
            audiences.Add($"{apiPrefix}{audience}");
        }

        return [.. audiences];
    }

    /// <summary>
    /// Derives the accepted token issuers for the tenant in <paramref name="authority"/>:
    /// the v2.0 issuer (https://login.microsoftonline.com/&lt;tenant&gt;/v2.0) and the
    /// v1.0 issuer (https://sts.windows.net/&lt;tenant&gt;/) that Managed Identity tokens
    /// carry. Returns empty when the tenant can't be parsed, so the caller can fall
    /// back to Authority-metadata validation rather than rejecting everything.
    /// </summary>
    internal static string[] ExpandIssuers(string? authority)
    {
        if (!Uri.TryCreate(authority?.Trim(), UriKind.Absolute, out var uri))
        {
            return [];
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !string.Equals(segments[1], "v2.0", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var tenant = segments[0];
        if (!Guid.TryParse(tenant, out _))
        {
            return [];
        }

        return
        [
            $"{uri.Scheme}://{uri.Host}/{tenant}/v2.0",
            $"https://sts.windows.net/{tenant}/",
        ];
    }
}
