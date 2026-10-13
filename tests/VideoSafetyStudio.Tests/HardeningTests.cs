using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Domain.Vision;
using VideoSafetyStudio.Infrastructure.Storage;
using VideoSafetyStudio.Server.Auth;

namespace VideoSafetyStudio.Tests;

public sealed class HardeningTests
{
    private sealed class Factory(string environment, Dictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Storage:Backend"] = "local",
                    ["Storage:Path"] = Path.Combine(Path.GetTempPath(), "vss-hard-" + Guid.NewGuid().ToString("N")),
                    ["ComfyUi:Url"] = "http://127.0.0.1:1",
                    ["Generation:Provider"] = "comfyui",
                });
                configuration.AddInMemoryCollection(settings ?? []);
            });
        }
    }

    private static Dictionary<string, string?> Hosted() => new()
    {
        ["Auth:PublicOrigin"] = "https://studio.example.net",
        ["Auth:ClientId"] = "22222222-2222-2222-2222-222222222222",
        ["Auth:TenantId"] = "11111111-1111-1111-1111-111111111111",
        ["Auth:ClientSecret"] = "not-a-real-secret",
    };

    [Fact]
    public async Task Security_headers_are_on_every_response_including_401()
    {
        await using var factory = new Factory("Production", Hosted());
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Host = "studio.example.net";
        foreach (var path in new[] { "/healthz", "/api/session" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
            var csp = response.Headers.GetValues("Content-Security-Policy").Single();
            Assert.Contains("script-src 'self';", csp);
            Assert.Contains("frame-ancestors 'none'", csp);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/session")).StatusCode);
    }

    [Fact]
    public void Csp_names_the_configured_storage_host_only()
    {
        var local = Hardening.ContentSecurityPolicy(new ConfigurationBuilder().Build());
        Assert.Contains("media-src 'self' blob:;", local);
        var azure = Hardening.ContentSecurityPolicy(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Backend"] = "azure",
            ["Storage:Account"] = "stwan",
        }).Build());
        Assert.Contains("media-src 'self' blob: https://stwan.blob.core.windows.net;", azure);
    }

    [Fact]
    public async Task Hosted_unknown_paths_need_a_session_but_static_assets_do_not()
    {
        await using var factory = new Factory("Production", Hosted());
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Host = "studio.example.net";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/new-admin-route")).StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/assets/app.js")).StatusCode);
    }

    [Fact]
    public async Task Hosted_session_cookie_uses_the_host_prefix()
    {
        await using var factory = new Factory("Production", Hosted());
        _ = factory.CreateClient();
        var options = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>().Get(AuthExtensions.CookieScheme);
        Assert.Equal("__Host-wan_session", options.Cookie.Name);
        Assert.Equal(CookieSecurePolicy.Always, options.Cookie.SecurePolicy);
    }

    [Fact]
    public async Task Local_mode_refuses_cross_origin_writes_and_foreign_hosts()
    {
        await using var factory = new Factory("Development");
        var client = factory.CreateClient();

        var foreign = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
        foreign.Headers.Add("Origin", "https://evil.example.net");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(foreign)).StatusCode);

        var same = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
        same.Headers.Add("Origin", "http://localhost");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(same)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/auth/logout", null)).StatusCode);

        client.DefaultRequestHeaders.Host = "rebind.example.net";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/session")).StatusCode);
    }

    [Fact]
    public async Task Submit_is_rate_limited_per_creator()
    {
        await using var factory = new Factory("Development", new() { ["RateLimits:SubmitPermits"] = "1" });
        var client = factory.CreateClient();
        var first = await client.PostAsync("/api/submit", new MultipartFormDataContent());
        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        var second = await client.PostAsync("/api/submit", new MultipartFormDataContent());
        Assert.True(second.StatusCode == HttpStatusCode.TooManyRequests, await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Media_streams_with_range_support_and_rejects_other_prefixes()
    {
        await using var factory = new Factory("Development");
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<IObjectStore>();
        await store.WriteAsync("video-library/clip.mp4", new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 });

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/media/video-library/clip.mp4") { Headers = { Range = new RangeHeaderValue(2, 4) } };
        var partial = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(new byte[] { 2, 3, 4 }, await partial.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/media/video-library/missing.mp4")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/media/other/clip.mp4")).StatusCode);
    }

    [Fact]
    public async Task Store_rejects_drive_letter_keys_and_streams_blobs()
    {
        var root = Path.Combine(Path.GetTempPath(), "vss-key-" + Guid.NewGuid().ToString("N"));
        var store = new LocalFolderObjectStore(root);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ExistsAsync("C:/x"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.OpenReadAsync("a/b:c"));
        Assert.Null(await store.OpenReadAsync("a/none.bin"));
        await store.WriteAsync("a/b.bin", new byte[] { 7, 8 });
        await using (var stream = (await store.OpenReadAsync("a/b.bin"))!)
        {
            Assert.True(stream.CanSeek);
            Assert.Equal(2, stream.Length);
        }
        Directory.Delete(root, true);
    }

    [Theory]
    [InlineData("v0123456789ab", true)]
    [InlineData("V0123456789ab", false)]
    [InlineData("../v0123456789ab", false)]
    [InlineData("v0123456789", false)]
    [InlineData("C:", false)]
    public void Video_ids_are_validated(string id, bool valid)
    {
        if (valid) Assert.Equal(id, Names.CheckVideo(id));
        else Assert.Throws<ValidationException>(() => Names.CheckVideo(id));
    }
}
