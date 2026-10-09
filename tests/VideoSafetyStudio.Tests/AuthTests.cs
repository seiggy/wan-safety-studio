using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VideoSafetyStudio.Server.Auth;

namespace VideoSafetyStudio.Tests;

public sealed class AuthTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Client = "22222222-2222-2222-2222-222222222222";
    private const string Origin = "https://studio.example.net";

    private sealed class Factory(string environment, Dictionary<string, string?> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Storage:Backend"] = "local",
                    ["Storage:Path"] = Path.Combine(Path.GetTempPath(), "vss-auth-" + Guid.NewGuid().ToString("N")),
                    ["ComfyUi:Url"] = "http://127.0.0.1:1",
                    ["Generation:Provider"] = "comfyui",
                });
                configuration.AddInMemoryCollection(settings);
            });
        }

        public HttpClient Hosted(string? cookie = null, string host = "studio.example.net")
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Host = host;
            if (cookie is not null)
            {
                client.DefaultRequestHeaders.Add("Cookie", "wan_session=" + cookie);
            }

            return client;
        }

        public string SignIn(string csrf = "csrf-token", TimeSpan? lifetime = null)
        {
            var options = Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthExtensions.CookieScheme);
            var identity = new ClaimsIdentity([new Claim("name", "Ada"), new Claim(CreatorClaims.Csrf, csrf)], "wan-session");
            var properties = new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow + (lifetime ?? TimeSpan.FromMinutes(30)), IssuedUtc = DateTimeOffset.UtcNow };
            return options.TicketDataFormat.Protect(new AuthenticationTicket(new ClaimsPrincipal(identity), properties, AuthExtensions.CookieScheme));
        }
    }

    private static Dictionary<string, string?> HostedSettings() => new()
    {
        ["WAN_STUDIO_PUBLIC_ORIGIN"] = Origin,
        ["WAN_STUDIO_PORTAL_CLIENT_ID"] = Client,
        ["WAN_STUDIO_FOUNDATION_JSON"] = $$"""{"tenantId":"{{Tenant}}"}""",
        ["Auth:ClientSecret"] = "not-a-real-secret",
    };

    private static HttpRequestMessage Post(string path, string? csrf, string? origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new { }) };
        if (csrf is not null) request.Headers.Add("X-CSRF-Token", csrf);
        if (origin is not null) request.Headers.Add("Origin", origin);
        return request;
    }

    [Fact]
    public async Task Local_dev_needs_no_sign_in_and_returns_the_local_identity()
    {
        await using var factory = new Factory("Development", []);
        var client = factory.CreateClient();
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.Equal("Local developer", session.GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(session.GetProperty("csrfToken").GetString()));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/features")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/auth/logout", null)).StatusCode);
    }

    [Fact]
    public async Task Hosted_rejects_anonymous_api_calls_but_serves_public_paths()
    {
        await using var factory = new Factory("Production", HostedSettings());
        var client = factory.Hosted();
        var response = await client.GetAsync("/api/session");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(string.IsNullOrEmpty((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/events")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/features")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/videos")).StatusCode);
    }

    [Fact]
    public async Task Hosted_login_asks_entra_for_a_get_callback()
    {
        await using var factory = new Factory("Production", HostedSettings());
        factory.Hosted();
        var options = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>>().Get("oidc");
        Assert.Equal("query", options.ResponseMode);
        Assert.Equal("code", options.ResponseType);
    }

    [Fact]
    public async Task Hosted_accepts_a_session_and_returns_its_csrf_token()
    {
        await using var factory = new Factory("Production", HostedSettings());
        var client = factory.Hosted(factory.SignIn("tok-1"));
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.Equal("Ada", session.GetProperty("name").GetString());
        Assert.Equal("tok-1", session.GetProperty("csrfToken").GetString());
        Assert.True(session.GetProperty("expires").GetDouble() > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/features")).StatusCode);
    }

    [Fact]
    public async Task Hosted_rejects_an_expired_session()
    {
        await using var factory = new Factory("Production", HostedSettings());
        var client = factory.Hosted(factory.SignIn(lifetime: TimeSpan.FromMinutes(-1)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/session")).StatusCode);
    }

    [Fact]
    public async Task Hosted_enforces_csrf_token_and_origin_on_writes()
    {
        await using var factory = new Factory("Production", HostedSettings());
        var client = factory.Hosted(factory.SignIn("tok-1"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Post("/auth/logout", null, Origin))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Post("/auth/logout", "wrong", Origin))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Post("/auth/logout", "tok-1", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Post("/auth/logout", "tok-1", "https://evil.example.net"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post("/auth/logout", "tok-1", Origin))).StatusCode);
    }

    [Fact]
    public async Task Hosted_agent_endpoint_requires_a_session()
    {
        await using var factory = new Factory("Production", HostedSettings());
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Hosted().SendAsync(Post("/api/agent", null, Origin))).StatusCode);
    }

    [Fact]
    public async Task Hosted_rejects_a_foreign_host_except_on_healthz()
    {
        await using var factory = new Factory("Production", HostedSettings());
        var client = factory.Hosted(host: "other.example.net");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task Hosted_login_redirects_to_Entra_with_the_public_callback()
    {
        await using var factory = new Factory("Production", HostedSettings());
        var client = factory.Hosted();
        client.DefaultRequestHeaders.Add("Accept", "text/html");
        var handler = factory.Server.Services.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>>().Get("oidc");
        Assert.Equal(Client, handler.ClientId);
        Assert.Equal("/auth/callback", handler.CallbackPath.Value);
        Assert.Contains(Tenant, handler.Authority);
    }

    [Fact]
    public async Task Disable_auth_acts_as_one_fixed_user_but_keeps_the_host_check()
    {
        await using var factory = new Factory("Production", new()
        {
            ["WAN_STUDIO_PUBLIC_ORIGIN"] = Origin,
            ["WAN_STUDIO_DISABLE_AUTH"] = "true",
        });
        var client = factory.Hosted();
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.Equal("Open access (sign-in disabled)", session.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post("/auth/logout", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.Hosted(host: "other.example.net").GetAsync("/api/session")).StatusCode);
    }

    [Fact]
    public void Startup_is_refused_outside_Development_without_auth()
    {
        using var factory = new Factory("Production", []);
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("no sign-in configured", error.ToString());
    }

    [Fact]
    public void Startup_is_refused_when_hosted_sign_in_is_incomplete()
    {
        var settings = HostedSettings();
        settings.Remove("Auth:ClientSecret");
        using var factory = new Factory("Production", settings);
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Theory]
    [InlineData("Server:AllowAnonymous")]
    [InlineData("WAN_STUDIO_DISABLE_AUTH")]
    public async Task Anonymous_opt_outs_allow_a_non_Development_start(string key)
    {
        await using var factory = new Factory("Production", new() { [key] = "true" });
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/healthz")).StatusCode);
    }

    private static readonly AuthSettings Settings = new(AuthMode.Entra, Origin, Tenant, Client, null, "s");

    private static ClaimsPrincipal Token(string tenant = Tenant, string audience = Client, string[]? roles = null, long? exp = null, string? oid = null)
    {
        var claims = new List<Claim>
        {
            new("tid", tenant), new("aud", audience), new("name", "Ada"),
            new("oid", oid ?? Guid.NewGuid().ToString()),
            new("exp", (exp ?? DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds()).ToString()),
        };
        claims.AddRange((roles ?? [AuthSettings.RequiredRole]).Select(role => new Claim("roles", role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public void Claims_validator_accepts_a_creator_and_caps_the_session_at_one_hour()
    {
        var verdict = CreatorClaims.Validate(Token(), Settings, DateTimeOffset.UtcNow, out var session, out var expires);
        Assert.Equal(ClaimsVerdict.Accepted, verdict);
        Assert.Equal("Ada", session!.FindFirst("name")!.Value);
        Assert.False(string.IsNullOrEmpty(session.FindFirst(CreatorClaims.Csrf)!.Value));
        Assert.InRange(expires - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));
    }

    [Fact]
    public void Claims_validator_ends_the_session_with_the_token()
    {
        var exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        CreatorClaims.Validate(Token(exp: exp), Settings, DateTimeOffset.UtcNow, out _, out var expires);
        Assert.Equal(exp, expires.ToUnixTimeSeconds());
    }

    [Fact]
    public void Claims_validator_rejects_wrong_tenant_audience_role_or_identity()
    {
        Assert.Equal(ClaimsVerdict.Forbidden, CreatorClaims.Validate(Token(tenant: Client), Settings, DateTimeOffset.UtcNow, out _, out _));
        Assert.Equal(ClaimsVerdict.Forbidden, CreatorClaims.Validate(Token(audience: Tenant), Settings, DateTimeOffset.UtcNow, out _, out _));
        Assert.Equal(ClaimsVerdict.Forbidden, CreatorClaims.Validate(Token(roles: ["Reader"]), Settings, DateTimeOffset.UtcNow, out _, out _));
        Assert.Equal(ClaimsVerdict.Forbidden, CreatorClaims.Validate(Token(roles: []), Settings, DateTimeOffset.UtcNow, out _, out _));
        Assert.Equal(ClaimsVerdict.Forbidden, CreatorClaims.Validate(Token(oid: "not-a-guid"), Settings, DateTimeOffset.UtcNow, out _, out _));
        Assert.Equal(ClaimsVerdict.Forbidden, CreatorClaims.Validate(null, Settings, DateTimeOffset.UtcNow, out _, out _));
    }

    [Fact]
    public void Claims_validator_fails_an_expired_token()
    {
        var verdict = CreatorClaims.Validate(Token(exp: DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()), Settings, DateTimeOffset.UtcNow, out _, out _);
        Assert.Equal(ClaimsVerdict.Failed, verdict);
    }
}
