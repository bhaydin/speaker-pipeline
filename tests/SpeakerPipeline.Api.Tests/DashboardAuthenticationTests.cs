using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using SpeakerPipeline.Api.Auth;

namespace SpeakerPipeline.Api.Tests;

public sealed class DashboardAuthenticationTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Owner = "22222222-2222-2222-2222-222222222222";

    [Theory]
    [InlineData(Tenant, Owner, true, true)]
    [InlineData("33333333-3333-3333-3333-333333333333", Owner, true, false)]
    [InlineData(Tenant, "33333333-3333-3333-3333-333333333333", true, false)]
    [InlineData(Tenant, Owner, false, false)]
    public void Exact_authenticated_tenant_and_owner_are_required(string tenant, string owner, bool authenticated, bool expected)
    {
        var identity = new ClaimsIdentity([new Claim("tid", tenant), new Claim("oid", owner)], authenticated ? "test" : null);
        Assert.Equal(expected, DashboardAuthentication.IsOwner(new ClaimsPrincipal(identity), Tenant, Owner));
    }

    [Fact]
    public async Task Cookie_mutations_require_csrf_and_logout_clears_the_session()
    {
        await using var basis = new TestWebApplicationFactory();
        await using var app = Configure(basis);
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var initial = await client.GetFromJsonAsync<Session>("/v1/dashboard/session");
        Assert.False(initial!.Authenticated);
        using var login = await client.GetAsync("/dashboard/login");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var session = await client.GetFromJsonAsync<Session>("/v1/dashboard/session");
        Assert.True(session!.Authenticated);
        Assert.False(string.IsNullOrWhiteSpace(session.RequestToken));
        using var rejected = await client.PostAsync("/dashboard/logout", null);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/dashboard/logout");
        logoutRequest.Headers.Add("X-CSRF-TOKEN", session.RequestToken);
        using var logout = await client.SendAsync(logoutRequest);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.False((await client.GetFromJsonAsync<Session>("/v1/dashboard/session"))!.Authenticated);
    }

    [Fact]
    public async Task Existing_machine_bearer_api_keeps_working_with_dashboard_enabled()
    {
        await using var basis = new TestWebApplicationFactory();
        await using var app = Configure(basis);
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/events");
        request.Headers.Authorization = new("Bearer", "dev");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await client.GetFromJsonAsync<Session>("/v1/dashboard/session"))!.Authenticated);
    }

    [Theory]
    [InlineData(true, "https")]
    [InlineData(false, "http")]
    public async Task Proxied_oidc_challenge_preserves_https_without_trusting_forwarded_host(bool forwarding, string expectedScheme)
    {
        await using var basis = new TestWebApplicationFactory();
        await using var app = basis.WithWebHostBuilder(builder => builder
            .UseSetting("ForwardedHeaders_Enabled", forwarding.ToString())
            .UseSetting("Dashboard:Enabled", "true")
            .UseSetting("Dashboard:AllowLocalSignIn", "false")
            .UseSetting("Dashboard:TenantId", Tenant)
            .UseSetting("Dashboard:OwnerObjectId", Owner)
            .UseSetting("Dashboard:ClientId", "synthetic-client")
            .UseSetting("Dashboard:ClientSecret", "synthetic-secret")
            .ConfigureServices(services => services.PostConfigure<OpenIdConnectOptions>(DashboardAuthentication.OidcScheme, options =>
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(new()
                {
                    Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0",
                    AuthorizationEndpoint = "https://identity.example.org/authorize",
                    TokenEndpoint = "https://identity.example.org/token",
                }))));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://pipeline.example.org"), AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/dashboard/login");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "attacker.example.org");
        request.Headers.Add("X-Forwarded-For", "198.51.100.10");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var authorization = response.Headers.Location!;
        Assert.Equal("identity.example.org", authorization.Host);
        var query = QueryHelpers.ParseQuery(authorization.Query);
        Assert.Equal($"{expectedScheme}://pipeline.example.org/signin-dashboard", query["redirect_uri"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        if (forwarding)
        {
            var options = app.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
            Assert.Equal(ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
            Assert.Equal(1, options.ForwardLimit);
        }
    }

    [Fact]
    public async Task Anonymous_dashboard_api_never_exposes_pipeline_data()
    {
        await using var basis = new TestWebApplicationFactory();
        await using var app = Configure(basis);
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/v1/opportunities");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/dashboard")]
    [InlineData("/dashboard/")]
    public async Task Dashboard_shell_does_not_redirect_to_itself(string path)
    {
        await using var basis = new TestWebApplicationFactory();
        await using var app = Configure(basis);
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<base href=\"/dashboard/\"", await response.Content.ReadAsStringAsync());
    }

    private static WebApplicationFactory<Program> Configure(TestWebApplicationFactory basis)
        => basis.WithWebHostBuilder(builder => builder
            .UseSetting("Dashboard:Enabled", "true")
            .UseSetting("Dashboard:AllowLocalSignIn", "true")
            .UseSetting("Dashboard:TenantId", Tenant)
            .UseSetting("Dashboard:OwnerObjectId", Owner));

    private sealed record Session(bool Authenticated, string? RequestToken);
}
