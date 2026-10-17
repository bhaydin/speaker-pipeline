using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Auth;

public static class DashboardAuthentication
{
    public const string CookieScheme = "DashboardCookie";
    public const string OidcScheme = "DashboardEntra";
    public const string OwnerPolicy = "DashboardOwner";
    private const string CombinedScheme = "PipelineAuthentication";

    public static IServiceCollection AddDashboardAuthentication(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        if (!configuration.GetValue<bool>("Dashboard:Enabled")) return services;
        var section = configuration.GetSection("Dashboard");
        var tenant = section["TenantId"];
        var owner = section["OwnerObjectId"];
        if (!Guid.TryParse(tenant, out _) || !Guid.TryParse(owner, out _))
            throw new InvalidOperationException("Dashboard requires its personal tenant and owner object IDs.");
        var localSignIn = environment.IsDevelopment() && section.GetValue<bool>("AllowLocalSignIn");
        if (RuntimeSafety.IsAzureHost && !configuration.GetValue<bool>("ForwardedHeaders_Enabled"))
            throw new InvalidOperationException("The App Service dashboard requires ASPNETCORE_FORWARDEDHEADERS_ENABLED=true for HTTPS sign-in redirects.");
        if (!localSignIn && (string.IsNullOrWhiteSpace(section["ClientId"]) || string.IsNullOrWhiteSpace(section["ClientSecret"])))
            throw new InvalidOperationException("Dashboard requires an Entra web client ID and a Key Vault-backed client credential.");

        // The documented App Service switch installs forwarding before authentication. Kestrel is
        // reachable only through the platform ingress; consume its final scheme hop, never an
        // arbitrary X-Forwarded-Host or client-address header when constructing auth redirects.
        services.PostConfigure<ForwardedHeadersOptions>(options =>
        {
            if (!configuration.GetValue<bool>("ForwardedHeaders_Enabled")) return;
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
        });

        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "SpeakerPipeline.Csrf";
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = localSignIn ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = CombinedScheme;
            options.DefaultChallengeScheme = CombinedScheme;
        }).AddPolicyScheme(CombinedScheme, CombinedScheme, options =>
        {
            options.ForwardDefaultSelector = context => HasBearer(context)
                ? JwtBearerDefaults.AuthenticationScheme : CookieScheme;
        }).AddCookie(CookieScheme, options =>
        {
            options.Cookie.Name = "SpeakerPipeline.Owner";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = localSignIn ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
            options.Events.OnValidatePrincipal = async context =>
            {
                if (!IsOwner(context.Principal, tenant!, owner!))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieScheme);
                }
            };
        });
        if (!localSignIn)
        {
            services.AddAuthentication().AddOpenIdConnect(OidcScheme, options =>
            {
                options.SignInScheme = CookieScheme;
                options.Authority = $"https://login.microsoftonline.com/{tenant}/v2.0";
                options.ClientId = section["ClientId"];
                options.ClientSecret = section["ClientSecret"];
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.Prompt = "select_account";
                options.UsePkce = true;
                options.MapInboundClaims = false;
                options.SaveTokens = false;
                options.CallbackPath = "/signin-dashboard";
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.TokenValidationParameters.NameClaimType = "name";
                options.TokenValidationParameters.RoleClaimType = "roles";
                options.Events.OnTokenValidated = context =>
                {
                    if (!IsOwner(context.Principal, tenant!, owner!)) context.Fail("This dashboard is restricted to its owner.");
                    else if (context.Principal?.Identity is ClaimsIdentity identity)
                        identity.AddClaim(new Claim("roles", AuthExtensions.AccessRole));
                    return Task.CompletedTask;
                };
                options.Events.OnRemoteFailure = context =>
                {
                    context.HandleResponse();
                    context.Response.Redirect("/dashboard/access-denied");
                    return Task.CompletedTask;
                };
            });
        }
        services.AddAuthorization(options => options.AddPolicy(OwnerPolicy, policy =>
            policy.AddAuthenticationSchemes(CookieScheme).RequireAuthenticatedUser()
                .RequireAssertion(context => IsOwner(context.User, tenant!, owner!))));
        return services;
    }

    internal static bool IsOwner(ClaimsPrincipal? principal, string tenant, string owner)
        => principal?.Identity?.IsAuthenticated == true
           && string.Equals(principal.FindFirstValue("tid"), tenant, StringComparison.OrdinalIgnoreCase)
           && string.Equals(principal.FindFirstValue("oid"), owner, StringComparison.OrdinalIgnoreCase);

    private static bool HasBearer(HttpContext context)
        => context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

    public static IApplicationBuilder UseDashboardCsrf(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            var unsafeMethod = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
                && !HttpMethods.IsOptions(context.Request.Method);
            if (unsafeMethod && context.User.Identity?.IsAuthenticated == true && !HasBearer(context)
                && (context.Request.Path.StartsWithSegments("/v1") || context.Request.Path == "/dashboard/logout"))
            {
                try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new { error = "A valid request verification token is required." });
                    return;
                }
            }
            await next(context);
        });

    public static void MapDashboard(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Dashboard:Enabled")) return;
        app.MapGet("/v1/dashboard/session", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var section = app.Configuration.GetSection("Dashboard");
            var authenticated = IsOwner(context.User, section["TenantId"]!, section["OwnerObjectId"]!);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                authenticated,
                name = authenticated ? context.User.Identity?.Name : null,
                requestToken = authenticated ? antiforgery.GetAndStoreTokens(context).RequestToken : null,
                loginUrl = "/dashboard/login",
            });
        }).AllowAnonymous();
        app.MapGet("/dashboard/login", async (HttpContext context) =>
        {
            var section = app.Configuration.GetSection("Dashboard");
            if (app.Environment.IsDevelopment() && section.GetValue<bool>("AllowLocalSignIn"))
            {
                if (context.Connection.RemoteIpAddress is { } remote && !System.Net.IPAddress.IsLoopback(remote))
                { context.Response.StatusCode = 403; return; }
                var identity = new ClaimsIdentity([
                    new Claim("tid", section["TenantId"]!), new Claim("oid", section["OwnerObjectId"]!),
                    new Claim("name", "Local owner"), new Claim("roles", AuthExtensions.AccessRole),
                ], CookieScheme, "name", "roles");
                await context.SignInAsync(CookieScheme, new ClaimsPrincipal(identity));
                context.Response.Redirect("/dashboard/");
            }
            else await context.ChallengeAsync(OidcScheme, new AuthenticationProperties { RedirectUri = "/dashboard/" });
        }).AllowAnonymous();
        app.MapPost("/dashboard/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieScheme);
            return Results.NoContent();
        }).RequireAuthorization(OwnerPolicy);
        app.MapGet("/dashboard/access-denied", () => Results.Content(
            "This dashboard is restricted to its owner. Sign in with the configured personal account.", "text/plain")).AllowAnonymous();
        app.MapFallbackToFile("/dashboard/{*path:nonfile}", "dashboard/index.html");
    }
}
