using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using SpeakerPipeline.Api.Auth;

namespace SpeakerPipeline.Api.Tests;

public sealed class DashboardOidcCallbackTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Owner = "22222222-2222-2222-2222-222222222222";
    private const string Other = "33333333-3333-3333-3333-333333333333";
    private const string ClientId = "44444444-4444-4444-4444-444444444444";
    private const string Issuer = "https://login.microsoftonline.com/" + Tenant + "/v2.0";

    [Fact]
    public async Task Owner_completes_code_redemption_and_receives_authenticated_cookie_session()
    {
        using var provider = new FakeIdentityProvider();
        await using var basis = new TestWebApplicationFactory();
        await using var app = Configure(basis, provider);
        using var browser = Browser(app);
        Assert.False((await browser.GetFromJsonAsync<Session>("/v1/dashboard/session"))!.Authenticated);
        var challenge = await ChallengeAsync(browser, provider);

        using var callback = await browser.PostAsync("/signin-dashboard", Callback(challenge.State));

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/dashboard/", callback.Headers.Location!.OriginalString);
        Assert.Equal(1, provider.Redemptions);
        Assert.True(provider.PkceVerified);
        Assert.Contains(callback.Headers.GetValues("Set-Cookie"), value => value.StartsWith("SpeakerPipeline.Owner=", StringComparison.Ordinal));
        var session = await browser.GetFromJsonAsync<Session>("/v1/dashboard/session");
        Assert.True(session!.Authenticated);
        Assert.Equal("Fictional owner", session.Name);
        Assert.False(string.IsNullOrWhiteSpace(session.RequestToken));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/v1/events")).StatusCode);
    }

    [Theory]
    [InlineData(Tenant, Other)]
    [InlineData(Other, Owner)]
    public async Task Signed_token_for_wrong_owner_or_tenant_cannot_create_owner_session(string tenant, string owner)
    {
        using var provider = new FakeIdentityProvider { TokenTenant = tenant, TokenOwner = owner };
        await using var basis = new TestWebApplicationFactory();
        await using var app = Configure(basis, provider);
        using var browser = Browser(app);
        var challenge = await ChallengeAsync(browser, provider);

        using var callback = await browser.PostAsync("/signin-dashboard", Callback(challenge.State));

        Assert.Equal(1, provider.Redemptions);
        Assert.True(provider.PkceVerified);
        await AssertRejectedAsync(browser, callback);
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("signature")]
    [InlineData("audience")]
    [InlineData("issuer")]
    public async Task Invalid_ID_token_cannot_create_owner_session(string invalid)
    {
        using var provider = new FakeIdentityProvider { InvalidToken = invalid };
        await using var basis = new TestWebApplicationFactory();
        await using var app = Configure(basis, provider);
        using var browser = Browser(app);
        var challenge = await ChallengeAsync(browser, provider);

        using var callback = await browser.PostAsync("/signin-dashboard", Callback(challenge.State));

        Assert.Equal(1, provider.Redemptions);
        await AssertRejectedAsync(browser, callback);
    }

    [Theory]
    [InlineData(".AspNetCore.Correlation.", 0)]
    [InlineData(".AspNetCore.OpenIdConnect.Nonce.", 1)]
    public async Task Callback_requires_original_correlation_and_nonce_cookies(string missingCookiePrefix, int expectedRedemptions)
    {
        using var provider = new FakeIdentityProvider();
        await using var basis = new TestWebApplicationFactory();
        await using var app = Configure(basis, provider);
        using var browser = Browser(app);
        var challenge = await ChallengeAsync(browser, provider);
        using var callbackClient = Browser(app, handleCookies: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/signin-dashboard") { Content = Callback(challenge.State) };
        request.Headers.Add("Cookie", string.Join("; ", challenge.Cookies
            .Where(cookie => !cookie.StartsWith(missingCookiePrefix, StringComparison.Ordinal))
            .Select(cookie => cookie.Split(';', 2)[0])));

        using var callback = await callbackClient.SendAsync(request);

        Assert.Equal(expectedRedemptions, provider.Redemptions);
        await AssertRejectedAsync(callbackClient, callback);
    }

    private static async Task AssertRejectedAsync(HttpClient browser, HttpResponseMessage callback)
    {
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/dashboard/access-denied", callback.Headers.Location!.OriginalString);
        Assert.DoesNotContain(callback.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            value => value.StartsWith("SpeakerPipeline.Owner=", StringComparison.Ordinal));
        Assert.False((await browser.GetFromJsonAsync<Session>("/v1/dashboard/session"))!.Authenticated);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/v1/events")).StatusCode);
    }

    private static async Task<Challenge> ChallengeAsync(HttpClient browser, FakeIdentityProvider provider)
    {
        using var response = await browser.GetAsync("/dashboard/login");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("identity.example.org", response.Headers.Location!.Host);
        var query = QueryHelpers.ParseQuery(response.Headers.Location.Query);
        Assert.Equal("code", query["response_type"].ToString());
        Assert.Equal("select_account", query["prompt"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.Equal("https://pipeline.example.org/signin-dashboard", query["redirect_uri"].ToString());
        provider.Nonce = query["nonce"].ToString();
        provider.Challenge = query["code_challenge"].ToString();
        Assert.False(string.IsNullOrWhiteSpace(provider.Nonce));
        Assert.False(string.IsNullOrWhiteSpace(provider.Challenge));
        var cookies = response.Headers.GetValues("Set-Cookie").ToArray();
        Assert.Contains(cookies, cookie => cookie.StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal));
        Assert.Contains(cookies, cookie => cookie.StartsWith(".AspNetCore.OpenIdConnect.Nonce.", StringComparison.Ordinal));
        return new(query["state"].ToString(), cookies);
    }

    private static FormUrlEncodedContent Callback(string state) => new(new Dictionary<string, string>
    { ["state"] = state, ["code"] = "fictional-authorization-code" });

    private static HttpClient Browser(WebApplicationFactory<Program> app, bool handleCookies = true)
        => app.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://pipeline.example.org"), AllowAutoRedirect = false, HandleCookies = handleCookies });

    private static WebApplicationFactory<Program> Configure(TestWebApplicationFactory basis, FakeIdentityProvider provider)
        => basis.WithWebHostBuilder(builder => builder.UseEnvironment("Production")
            .UseSetting("Authentication:Authority", Issuer)
            .UseSetting("Authentication:Audience", "fictional-api")
            .UseSetting("Dashboard:Enabled", "true")
            .UseSetting("Dashboard:AllowLocalSignIn", "false")
            .UseSetting("Dashboard:TenantId", Tenant)
            .UseSetting("Dashboard:OwnerObjectId", Owner)
            .UseSetting("Dashboard:ClientId", ClientId)
            .UseSetting("Dashboard:ClientSecret", "fictional-client-secret")
            .ConfigureServices(services => services.PostConfigure<OpenIdConnectOptions>(DashboardAuthentication.OidcScheme, options =>
            {
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(provider.Configuration());
                options.Backchannel = new HttpClient(provider, disposeHandler: false);
                options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
            })));

    private sealed record Challenge(string State, string[] Cookies);
    private sealed record Session(bool Authenticated, string? Name, string? RequestToken);

    /// <summary>Replaces only the remote provider transport; middleware, cookies, signatures and PKCE remain real.</summary>
    private sealed class FakeIdentityProvider : HttpMessageHandler
    {
        private readonly RSA _signingKey = RSA.Create(2048);
        private readonly RSA _wrongKey = RSA.Create(2048);
        public string TokenTenant { get; init; } = Tenant;
        public string TokenOwner { get; init; } = Owner;
        public string? InvalidToken { get; init; }
        public string Nonce { get; set; } = "";
        public string Challenge { get; set; } = "";
        public int Redemptions { get; private set; }
        public bool PkceVerified { get; private set; }

        public OpenIdConnectConfiguration Configuration()
        {
            var configuration = new OpenIdConnectConfiguration
            {
                Issuer = DashboardOidcCallbackTests.Issuer,
                AuthorizationEndpoint = "https://identity.example.org/authorize",
                TokenEndpoint = "https://identity.example.org/token",
            };
            configuration.SigningKeys.Add(new RsaSecurityKey(_signingKey.ExportParameters(false)) { KeyId = "fictional-signing-key" });
            return configuration;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://identity.example.org/token", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("authorization_code", form["grant_type"].ToString());
            Assert.Equal("fictional-authorization-code", form["code"].ToString());
            Assert.Equal(ClientId, form["client_id"].ToString());
            Assert.Equal("fictional-client-secret", form["client_secret"].ToString());
            Assert.Equal("https://pipeline.example.org/signin-dashboard", form["redirect_uri"].ToString());
            var verifier = form["code_verifier"].ToString();
            Assert.InRange(verifier.Length, 43, 128);
            Assert.Equal(Challenge, Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
            PkceVerified = true;
            Redemptions++;
            var now = DateTime.UtcNow;
            var claims = new[]
            {
                new Claim("tid", TokenTenant), new Claim("oid", TokenOwner), new Claim("sub", "fictional-subject"),
                new Claim("name", "Fictional owner"), new Claim("nonce", InvalidToken == "nonce" ? "wrong-nonce" : Nonce),
                new Claim("iat", EpochTime.GetIntDate(now).ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            };
            var key = new RsaSecurityKey(InvalidToken == "signature" ? _wrongKey : _signingKey) { KeyId = "fictional-signing-key" };
            var token = new JwtSecurityToken(InvalidToken == "issuer" ? "https://wrong-issuer.example.org" : Issuer,
                InvalidToken == "audience" ? "wrong-audience" : ClientId, claims, now.AddMinutes(-1), now.AddMinutes(5),
                new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
            var body = JsonSerializer.Serialize(new { token_type = "Bearer", access_token = "fictional-access-token", expires_in = 300,
                id_token = new JwtSecurityTokenHandler().WriteToken(token) });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _signingKey.Dispose(); _wrongKey.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
