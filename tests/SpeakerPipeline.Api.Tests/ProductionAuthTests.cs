using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using SpeakerPipeline.Api.Auth;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Tests;

public sealed class ProductionAuthTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("dev")]
    public async Task Production_rejects_missing_and_development_tokens(string? token)
    {
        using var factory = new ProductionFactory();
        using var client = factory.CreateClient();
        if (token is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/events")).StatusCode);
    }

    [Fact]
    public async Task Valid_signature_without_access_role_is_forbidden()
    {
        using var factory = new ProductionFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token([]));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/v1/events")).StatusCode);
    }

    [Theory]
    [InlineData("wrong-audience", false)]
    [InlineData(ProductionFactory.Audience, true)]
    public async Task Invalid_audience_or_expiry_is_rejected(string audience, bool expired)
    {
        using var factory = new ProductionFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token([AuthExtensions.AccessRole], audience, expired));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/events")).StatusCode);
    }

    [Fact]
    public async Task Intended_role_can_read_but_maintenance_blocks_business_writes()
    {
        using var factory = new ProductionFactory(freeze: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token([AuthExtensions.AccessRole]));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/events")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await client.DeleteAsync("/v1/events/a-real-event")).StatusCode);
    }

    [Fact]
    public async Task Maintenance_role_is_separate_from_normal_access()
    {
        using var factory = new ProductionFactory();
        using var scope = factory.Services.CreateScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        static ClaimsPrincipal Principal(params string[] roles) => new(new ClaimsIdentity(
            roles.Select(r => new Claim("roles", r)), "test", "name", "roles"));
        Assert.False((await authorization.AuthorizeAsync(Principal(AuthExtensions.AccessRole), null,
            AuthExtensions.MaintenancePolicyName)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(Principal(AuthExtensions.MaintenanceRole), null,
            AuthExtensions.MaintenancePolicyName)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(Principal(AuthExtensions.AccessRole, AuthExtensions.MaintenanceRole), null,
            AuthExtensions.MaintenancePolicyName)).Succeeded);
    }

    private sealed class ProductionFactory(bool freeze = false) : WebApplicationFactory<Program>
    {
        internal const string Audience = "test-production-api";
        private const string Issuer = "https://issuer.example.org";
        private readonly RSA _rsa = RSA.Create(2048);

        protected override IHost CreateHost(IHostBuilder builder)
        {
            // Minimal hosting reads authentication configuration before the
            // WebHost configuration callbacks. Supply startup values early.
            builder.ConfigureHostConfiguration(cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Authority"] = Issuer,
                ["Authentication:Audience"] = Audience,
            }));
            return base.CreateHost(builder);
        }

        internal string Token(string[] roles, string audience = Audience, bool expired = false)
        {
            var now = DateTime.UtcNow;
            var key = new RsaSecurityKey(_rsa) { KeyId = "test-key" };
            var jwt = new JwtSecurityToken(Issuer, audience, roles.Select(r => new Claim("roles", r)),
                now.AddHours(-2), expired ? now.AddHours(-1) : now.AddMinutes(5),
                new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
            return new JwtSecurityTokenHandler().WriteToken(jwt);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Authority"] = Issuer,
                ["Authentication:Audience"] = Audience,
                ["Storage:TableEndpoint"] = "https://storage.example.org",
                ["ApplicationInsights:ConnectionString"] = "",
                ["Maintenance:FreezeWrites"] = freeze.ToString(),
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEventRepository>();
                services.AddSingleton<IEventRepository, InMemoryEventRepository>();
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                    configuration.SigningKeys.Add(new RsaSecurityKey(_rsa) { KeyId = "test-key" });
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                    options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _rsa.Dispose();
        }
    }
}
