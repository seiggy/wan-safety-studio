using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using VideoSafetyStudio.Server;
using VideoSafetyStudio.Server.Auth;
using VideoSafetyStudio.Tests.Support;

namespace VideoSafetyStudio.Tests;

public sealed class AuthTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Client = "22222222-2222-2222-2222-222222222222";
    private const string Origin = "https://studio.example.net";

    private sealed class Factory(string environment, Dictionary<string, string?> settings) : ReliabilityStorageFactory
    {
        public ReliabilityTimeProvider Time { get; } = new();
        public int BackchannelRequests { get; private set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Storage:Backend"] = "local",
                    ["Storage:Path"] = StorageFolder,
                    ["Auth:KeysPath"] = Path.Combine(StorageFolder, "keys"),
                    ["ComfyUi:Url"] = "http://127.0.0.1:1",
                    ["Generation:Provider"] = "comfyui",
                });
                configuration.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<CookieAuthenticationOptions>(AuthExtensions.CookieScheme, options => options.TimeProvider = Time);
                services.PostConfigure<OpenIdConnectOptions>("oidc", options =>
                {
                    var metadata = new OpenIdConnectConfiguration
                    {
                        Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0",
                        AuthorizationEndpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize",
                        TokenEndpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token",
                    };
                    options.Configuration = metadata;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
                    options.Backchannel = Own(new HttpClient(new OfflineBackchannel(() => BackchannelRequests++)));
                });
            });
        }

        public HttpClient Hosted(string? cookie = null, string host = "studio.example.net")
        {
            var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Host = host;
            if (cookie is not null)
            {
                client.DefaultRequestHeaders.Add("Cookie", "__Host-wan_session=" + cookie);
            }

            return client;
        }

        public string SignIn(string csrf = "csrf-token", TimeSpan? lifetime = null)
        {
            var options = Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthExtensions.CookieScheme);
            var identity = new ClaimsIdentity(
                [new Claim("oid", "33333333-3333-3333-3333-333333333333"), new Claim("name", "Ada"), new Claim(CreatorClaims.Csrf, csrf)], "wan-session");
            var properties = new AuthenticationProperties { ExpiresUtc = Time.GetUtcNow() + (lifetime ?? TimeSpan.FromMinutes(30)), IssuedUtc = Time.GetUtcNow() };
            return options.TicketDataFormat.Protect(new AuthenticationTicket(new ClaimsPrincipal(identity), properties, AuthExtensions.CookieScheme));
        }

        private sealed class OfflineBackchannel(Action requested) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                requested();
                throw new InvalidOperationException("The hosted login test must not access the OIDC network.");
            }
        }
    }

    private static Dictionary<string, string?> HostedSettings() => new()
    {
        ["Auth:PublicOrigin"] = Origin,
        ["Auth:ClientId"] = Client,
        ["Auth:TenantId"] = Tenant,
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
        using var client = factory.CreateClient();
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
        using var client = factory.Hosted();
        using var response = await client.GetAsync("/api/session");
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
        using var client = factory.Hosted();
        var options = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>>().Get("oidc");
        Assert.Equal("query", options.ResponseMode);
        Assert.Equal("code", options.ResponseType);
    }

    [Fact]
    public async Task Hosted_accepts_a_session_and_returns_its_csrf_token()
    {
        await using var factory = new Factory("Production", HostedSettings());
        using var client = factory.Hosted(factory.SignIn("tok-1"));
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.Equal("Ada", session.GetProperty("name").GetString());
        Assert.Equal("tok-1", session.GetProperty("csrfToken").GetString());
        Assert.Equal(factory.Time.GetUtcNow().AddMinutes(30).ToUnixTimeSeconds(), session.GetProperty("expires").GetDouble());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/features")).StatusCode);
    }

    [Fact]
    public async Task Hosted_rejects_an_expired_session()
    {
        await using var factory = new Factory("Production", HostedSettings());
        using var client = factory.Hosted(factory.SignIn());
        factory.Time.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/session")).StatusCode);
        factory.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/session")).StatusCode);
    }

    [Fact]
    public async Task Hosted_enforces_csrf_token_and_origin_on_writes()
    {
        await using var factory = new Factory("Production", HostedSettings());
        using var client = factory.Hosted(factory.SignIn("tok-1"));
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
        using var client = factory.Hosted();
        using var request = Post("/api/agent", null, Origin);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Hosted_rejects_a_foreign_host_except_on_healthz()
    {
        await using var factory = new Factory("Production", HostedSettings());
        using var client = factory.Hosted(host: "other.example.net");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task Hosted_login_redirects_to_Entra_with_the_public_callback()
    {
        await using var factory = new Factory("Production", HostedSettings());
        using var client = factory.Hosted();
        client.DefaultRequestHeaders.Add("Accept", "text/html");
        using var response = await client.GetAsync("/auth/login");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var authorization = Assert.IsType<Uri>(response.Headers.Location);
        Assert.Equal("https", authorization.Scheme);
        Assert.Equal("login.microsoftonline.com", authorization.Host);
        Assert.Equal($"/{Tenant}/oauth2/v2.0/authorize", authorization.AbsolutePath);
        var query = QueryHelpers.ParseQuery(authorization.Query);
        Assert.Equal(Origin + "/auth/callback", query["redirect_uri"].ToString());
        Assert.Equal(Client, query["client_id"].ToString());
        Assert.Equal("code", query["response_type"].ToString());
        // The OIDC handler omits the query mode when it is the protocol default for code.
        var responseMode = query.TryGetValue("response_mode", out var explicitMode)
            ? explicitMode.ToString()
            : query["response_type"] == "code" ? "query" : "fragment";
        Assert.Equal("query", responseMode);
        Assert.Equal("select_account", query["prompt"].ToString());
        Assert.Equal(0, factory.BackchannelRequests);
    }

    [Fact]
    public async Task Explicit_mode_none_outside_Development_acts_as_one_fixed_user()
    {
        await using var factory = new Factory("Production", new() { ["Auth:Mode"] = "None" });
        using var client = factory.CreateClient();
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.Equal("Local developer", session.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post("/auth/logout", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public void Startup_is_refused_outside_Development_without_auth()
    {
        using var factory = new Factory("Production", []);
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Entra sign-in needs", error.ToString());
    }

    [Fact]
    public void Startup_is_refused_when_hosted_sign_in_is_incomplete()
    {
        var settings = HostedSettings();
        settings.Remove("Auth:ClientSecret");
        using var factory = new Factory("Production", settings);
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    private static AuthSettings Resolve(string environment, params (string Key, string? Value)[] values) =>
        AuthSettings.Resolve(AzureMlSamples.Config(values), new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = environment });

    [Fact]
    public void Mode_defaults_to_none_in_Development_and_entra_elsewhere()
    {
        Assert.Equal(AuthMode.None, Resolve("Development").Mode);
        Assert.Throws<InvalidOperationException>(() => Resolve("Production"));
        Assert.Throws<InvalidOperationException>(() => Resolve("Staging"));
    }

    [Fact]
    public void Explicit_mode_wins_in_both_directions()
    {
        Assert.Equal(AuthMode.None, Resolve("Production", ("Auth:Mode", "none")).Mode);
        Assert.Throws<InvalidOperationException>(() => Resolve("Development", ("Auth:Mode", "Entra")));
        Assert.Throws<InvalidOperationException>(() => Resolve("Production", ("Auth:Mode", "Open")));
    }

    [Fact]
    public void Entra_settings_validate_origin_tenant_and_credential()
    {
        (string, string?)[] Valid(params (string, string?)[] more) =>
            [.. new (string, string?)[] { ("Auth:Mode", "Entra"), ("Auth:ClientId", Client), ("Auth:PublicOrigin", Origin), ("AzureMl:TenantId", Tenant), ("Auth:ClientSecret", "s") }
                .Where(item => more.All(m => m.Item1 != item.Item1)), .. more];

        var settings = Resolve("Production", Valid());
        Assert.Equal(Tenant, settings.TenantId); // Auth:TenantId defaults to AzureMl:TenantId
        Assert.Equal("22222222-2222-2222-2222-222222222223", Resolve("Production", Valid(("Auth:TenantId", "22222222-2222-2222-2222-222222222223"))).TenantId);
        Assert.Throws<InvalidOperationException>(() => Resolve("Production", Valid(("Auth:PublicOrigin", "http://evil.example.net"))));
        Assert.Throws<InvalidOperationException>(() => Resolve("Production", Valid(("Auth:ClientId", ""))));
        Assert.Throws<InvalidOperationException>(() => Resolve("Production", Valid(("Auth:PublicOrigin", ""))));
        Assert.Throws<InvalidOperationException>(() => Resolve("Production", Valid(("Auth:ClientSecret", ""))));
        Assert.Equal(Client, Resolve("Production", Valid(("Auth:ClientSecret", ""), ("AZURE_CLIENT_ID", Client))).ManagedIdentityClientId); // federated, no secret
    }

    [Fact]
    public void Key_vault_is_skipped_when_unset_and_validated_when_set()
    {
        var empty = new ConfigurationBuilder();
        var credential = new Azure.Identity.DefaultAzureCredential();
        Assert.False(empty.AddStudioKeyVault(AzureMlSamples.Config(), credential));
        Assert.False(empty.AddStudioKeyVault(AzureMlSamples.Config(("KeyVault:Uri", " ")), credential));
        Assert.Empty(empty.Sources);
        Assert.Throws<InvalidOperationException>(() => empty.AddStudioKeyVault(AzureMlSamples.Config(("KeyVault:Uri", "http://kv.vault.azure.net/")), credential));
        Assert.True(empty.AddStudioKeyVault(AzureMlSamples.Config(("KeyVault:Uri", "https://kv-wan.vault.azure.net/")), credential));
        Assert.Single(empty.Sources);
    }
    private static readonly AuthSettings Settings = new(AuthMode.Entra, Origin, Tenant, Client, null, "s");
    private static readonly DateTimeOffset ClaimsNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ClaimsPrincipal Token(string tenant = Tenant, string audience = Client, string[]? roles = null, long? exp = null, string? oid = null)
    {
        var claims = new List<Claim>
        {
            new("tid", tenant), new("aud", audience), new("name", "Ada"),
            new("oid", oid ?? Guid.NewGuid().ToString()),
            new("exp", (exp ?? ClaimsNow.AddHours(2).ToUnixTimeSeconds()).ToString()),
        };
        claims.AddRange((roles ?? [AuthSettings.RequiredRole]).Select(role => new Claim("roles", role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public void Claims_validator_accepts_a_creator_and_caps_the_session_at_one_hour()
    {
        var verdict = CreatorClaims.Validate(Token(), Settings, ClaimsNow, out var session, out var expires);
        Assert.Equal(ClaimsVerdict.Accepted, verdict);
        Assert.Equal("Ada", session!.FindFirst("name")!.Value);
        Assert.False(string.IsNullOrEmpty(session.FindFirst(CreatorClaims.Csrf)!.Value));
        Assert.Equal(ClaimsNow + CreatorClaims.SessionLifetime, expires);
    }

    [Fact]
    public void Claims_validator_ends_the_session_with_the_token()
    {
        var exp = ClaimsNow.AddMinutes(10).ToUnixTimeSeconds();
        CreatorClaims.Validate(Token(exp: exp), Settings, ClaimsNow, out _, out var expires);
        Assert.Equal(exp, expires.ToUnixTimeSeconds());
    }

    [Fact]
    public void Claims_validator_rejects_wrong_tenant_audience_role_or_identity()
    {
        Assert.Equal(ClaimsVerdict.Forbidden, CreatorClaims.Validate(Token(tenant: Client), Settings, ClaimsNow, out _, out _));
        Assert.Equal(ClaimsVerdict.Forbidden, CreatorClaims.Validate(Token(audience: Tenant), Settings, ClaimsNow, out _, out _));
        Assert.Equal(ClaimsVerdict.Forbidden, CreatorClaims.Validate(Token(roles: ["Reader"]), Settings, ClaimsNow, out _, out _));
        Assert.Equal(ClaimsVerdict.Forbidden, CreatorClaims.Validate(Token(roles: []), Settings, ClaimsNow, out _, out _));
        Assert.Equal(ClaimsVerdict.Forbidden, CreatorClaims.Validate(Token(oid: "not-a-guid"), Settings, ClaimsNow, out _, out _));
        Assert.Equal(ClaimsVerdict.Forbidden, CreatorClaims.Validate(null, Settings, ClaimsNow, out _, out _));
    }

    [Fact]
    public void Claims_validator_fails_an_expired_token()
    {
        var time = new ReliabilityTimeProvider();
        var token = Token(exp: time.GetUtcNow().AddMinutes(10).ToUnixTimeSeconds());
        time.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        Assert.Equal(ClaimsVerdict.Accepted, CreatorClaims.Validate(token, Settings, time.GetUtcNow(), out _, out _));
        time.Advance(TimeSpan.FromSeconds(1));
        var verdict = CreatorClaims.Validate(token, Settings, time.GetUtcNow(), out _, out _);
        Assert.Equal(ClaimsVerdict.Failed, verdict);
    }
}
