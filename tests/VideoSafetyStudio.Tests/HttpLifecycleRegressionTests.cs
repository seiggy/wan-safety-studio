using System.Net;
using System.Net.ServerSentEvents;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Azure.Core;
using GitHub.Copilot;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoSafetyStudio.Infrastructure.Agent;
using VideoSafetyStudio.Infrastructure.Events;
using VideoSafetyStudio.Server;
using VideoSafetyStudio.Server.Auth;
using VideoSafetyStudio.Server.Endpoints;

namespace VideoSafetyStudio.Tests;

public sealed class HttpLifecycleRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposing_sse_at_an_event_or_ping_settles_waits_and_unsubscribes(bool ping)
    {
        var hub = new EventHub();
        var time = new PingTimeProvider();
        var stream = Assert.IsAssignableFrom<IAsyncEnumerable<SseItem<string>>>(
            typeof(GenerationEndpoints).GetMethod("Stream", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [hub, time, CancellationToken.None]));
        await using var events = stream.GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("ready", events.Current.EventType);
        var pending = events.MoveNextAsync().AsTask();
        Assert.Equal(1, hub.Subscribers);
        if (ping) time.Fire();
        else hub.Publish("progress", new { value = 1 });
        Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ping ? "ping" : "progress", events.Current.EventType);

        await events.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, hub.Subscribers);
    }

    [Theory]
    [InlineData("/api/submit", "text/plain", "not a form", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("/api/vision/projects/missing/videos", "text/plain", "not a form", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("/api/submit", "multipart/form-data", "missing boundary", HttpStatusCode.BadRequest)]
    [InlineData("/api/vision/projects/missing/videos", "multipart/form-data", "missing boundary", HttpStatusCode.BadRequest)]
    [InlineData("/api/vision/projects", "application/json", "{broken", HttpStatusCode.BadRequest)]
    public async Task Invalid_http_inputs_return_json_before_use_cases(string route, string contentType, string body, HttpStatusCode expected)
    {
        await using var host = new HttpRegressionHost();
        using var client = host.CreateClient();
        using var content = new StringContent(body, Encoding.UTF8, contentType);
        using var response = await client.PostAsync(route, content);
        await AssertErrorAsync(response, expected);
    }

    [Fact]
    public async Task Missing_media_and_rate_rejections_return_json()
    {
        await using var host = new HttpRegressionHost();
        using var client = host.CreateClient();
        using var missing = await client.GetAsync("/api/media/library/missing.mp4");
        await AssertErrorAsync(missing, HttpStatusCode.NotFound);
        using var rejected = await client.PostAsync("/api/submit", new StringContent("invalid"));
        await AssertErrorAsync(rejected, HttpStatusCode.UnsupportedMediaType);
        using var limited = await client.PostAsync("/api/submit", new StringContent("invalid"));
        await AssertErrorAsync(limited, HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Request_exception_preserves_413()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        using var services = Assert.IsType<ServiceProvider>(context.RequestServices);
        var middleware = new ErrorMappingMiddleware(
            _ => throw new BadHttpRequestException("Too large.", StatusCodes.Status413PayloadTooLarge),
            NullLogger<ErrorMappingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        body.Position = 0;
        using var json = await JsonDocument.ParseAsync(body);
        Assert.Equal("Too large.", json.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("/api/submit", BodyLimits.Submit)]
    [InlineData("/api/vision/projects/missing/videos", BodyLimits.Video)]
    public async Task Kestrel_enforces_upload_limit_and_raises_the_global_ceiling(string route, long limit)
    {
        await using var host = new HttpRegressionHost(new() { ["RateLimits:SubmitPermits"] = "10" });
        host.UseKestrel(0);
        using var client = host.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var oversized = await SendDeclaredUploadAsync(client.BaseAddress!, route, limit + 1, timeout.Token);
        Assert.Contains("413", oversized.Split("\r\n")[0]);
        Assert.Contains("\"error\"", oversized);

        var allowed = await SendDeclaredUploadAsync(client.BaseAddress!, route, limit, timeout.Token);
        Assert.Contains("400", allowed.Split("\r\n")[0]);
        Assert.Contains("\"error\"", allowed);
    }

    [Fact]
    public async Task Creator_quota_uses_object_identity_and_preserves_public_session_shape()
    {
        await using var host = new HttpRegressionHost(new()
        {
            ["Auth:Mode"] = "Entra",
            ["Auth:ClientId"] = "test-client",
            ["Auth:TenantId"] = "test-tenant",
            ["Auth:ClientSecret"] = "test-only",
            ["Auth:PublicOrigin"] = "https://studio.example.test",
        });
        var firstId = Guid.NewGuid().ToString();
        using var first = CreatorClient(host, firstId, "Same name");
        using var second = CreatorClient(host, Guid.NewGuid().ToString(), "Same name");
        using var renamed = CreatorClient(host, firstId, "New name");

        using var a = await first.PostAsync("/api/submit", new StringContent("invalid"));
        using var b = await second.PostAsync("/api/submit", new StringContent("invalid"));
        using var c = await renamed.PostAsync("/api/submit", new StringContent("invalid"));
        await AssertErrorAsync(a, HttpStatusCode.UnsupportedMediaType);
        await AssertErrorAsync(b, HttpStatusCode.UnsupportedMediaType);
        await AssertErrorAsync(c, HttpStatusCode.TooManyRequests);

        using var response = await first.GetAsync("/api/session");
        using var session = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["csrfToken", "expires", "name"], session.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    [Fact]
#pragma warning disable GHCP001 // The provider-token callback is the production SDK's experimental API.
    public async Task Helper_credential_acquisition_observes_run_cancellation_without_disabling_shared_credential()
    {
        var credential = new RunCredential();
        using var cancelledRun = new CancellationTokenSource();
        var options = new VideoHelperOptions { Endpoint = "https://unit-test.example/v1", Model = "model", Auth = "AzureCli" };
        var method = typeof(VideoHelperAgent).GetMethod("CreateProvider", BindingFlags.Static | BindingFlags.NonPublic)!;
        var first = Assert.IsType<ProviderConfig>(method.Invoke(null, [options, credential, cancelledRun.Token]));
        var acquisition = first.BearerTokenProvider!(new ProviderTokenArgs { ProviderName = "openai", SessionId = "first" });
        await credential.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancelledRun.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition.WaitAsync(TimeSpan.FromSeconds(5)));

        var second = Assert.IsType<ProviderConfig>(method.Invoke(null, [options, credential, CancellationToken.None]));
        Assert.Equal("next-run-token", await second.BearerTokenProvider!(new ProviderTokenArgs { ProviderName = "openai", SessionId = "second" }));
    }
#pragma warning restore GHCP001

    [Theory]
    [InlineData("file:///tmp/model", "completions", "Agent:Endpoint")]
    [InlineData("ftp://unit-test.example", "responses", "Agent:Endpoint")]
    [InlineData("https://unit-test.example/v1", "typo", "Agent:WireApi")]
    public async Task Invalid_helper_configuration_disables_status_and_does_not_map_stream(string endpoint, string wireApi, string key)
    {
        await using var host = new HttpRegressionHost(new()
        {
            ["Agent:Endpoint"] = endpoint,
            ["Agent:Model"] = "model",
            ["Agent:ApiKey"] = "test-only",
            ["Agent:WireApi"] = wireApi,
        });
        using var client = host.CreateClient();
        using var response = await client.GetAsync("/api/agent/status");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Contains(key, json.RootElement.GetProperty("hint").GetString());
        Assert.DoesNotContain(host.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText == "/api/agent");
    }

    [Theory]
    [InlineData("http://localhost:8000/v1", "completions")]
    [InlineData("http://localhost:8000/v1", "responses")]
    [InlineData("https://unit-test.example/v1", "completions")]
    [InlineData("https://unit-test.example/v1", "responses")]
    public void Supported_helper_endpoints_and_wire_apis_are_enabled(string endpoint, string wireApi)
    {
        var options = new VideoHelperOptions { Endpoint = endpoint, Model = "model", ApiKey = "test-only", WireApi = wireApi };
        Assert.True(options.Enabled, options.ConfigurationError);
    }

    private static HttpClient CreatorClient(HttpRegressionHost host, string id, string name)
    {
        var client = host.CreateClient(new() { BaseAddress = new Uri("https://studio.example.test"), HandleCookies = false });
        var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthExtensions.CookieScheme);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("oid", id), new Claim("name", name), new Claim(CreatorClaims.Csrf, "test-csrf")], AuthExtensions.CookieScheme));
        var ticket = new AuthenticationTicket(principal,
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) }, AuthExtensions.CookieScheme);
        client.DefaultRequestHeaders.Add("Cookie", options.Cookie.Name + "=" + options.TicketDataFormat.Protect(ticket));
        client.DefaultRequestHeaders.Add("Origin", "https://studio.example.test");
        client.DefaultRequestHeaders.Add("X-CSRF-Token", "test-csrf");
        return client;
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("error").GetString()));
    }

    private static async Task<string> SendDeclaredUploadAsync(Uri address, string route, long length, CancellationToken ct)
    {
        using var socket = new TcpClient();
        await socket.ConnectAsync(address.Host, address.Port, ct);
        await using var stream = socket.GetStream();
        var request = $"POST {route} HTTP/1.1\r\nHost: localhost:{address.Port}\r\nConnection: close\r\nContent-Type: multipart/form-data; boundary=test\r\nContent-Length: {length}\r\n\r\n--test\r\ninvalid-header\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var status = await reader.ReadLineAsync(ct);
        Assert.NotNull(status);
        var chunked = false;
        var contentLength = 0;
        while (await reader.ReadLineAsync(ct) is { Length: > 0 } header)
        {
            if (header.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase))
                chunked = header.Contains("chunked", StringComparison.OrdinalIgnoreCase);
            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(header.Split(':')[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        }

        var body = new StringBuilder();
        if (chunked)
        {
            while (true)
            {
                var chunk = await reader.ReadLineAsync(ct);
                Assert.NotNull(chunk);
                var lengthOfChunk = int.Parse(chunk.Split(';')[0], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                if (lengthOfChunk == 0) break;
                body.Append(await ReadCharactersAsync(lengthOfChunk));
                Assert.Equal("", await reader.ReadLineAsync(ct));
            }
        }
        else
        {
            body.Append(await ReadCharactersAsync(contentLength));
        }

        return status + "\r\n" + body;

        async Task<string> ReadCharactersAsync(int count)
        {
            var characters = new char[count];
            Assert.Equal(count, await reader.ReadBlockAsync(characters.AsMemory(), ct));
            return new string(characters);
        }
    }

    private sealed class RunCredential : TokenCredential
    {
        private int _calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new AccessToken("next-run-token", DateTimeOffset.MaxValue);
        }
    }

    private sealed class PingTimeProvider : TimeProvider
    {
        private PingTimer? _timer;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _timer = new PingTimer(callback, state);
        public void Fire() => _timer!.Fire();

        private sealed class PingTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            public void Fire() { if (!_disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

internal sealed class HttpRegressionHost(Dictionary<string, string?>? overrides = null) : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "studio-http-regression-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["Auth:Mode"] = "None",
                ["Generation:Provider"] = "comfyui",
                ["Storage:Backend"] = "local",
                ["Storage:Path"] = _root,
                ["FEATURE_DATASET_BUILDER_ENABLED"] = "true",
                ["RateLimits:SubmitPermits"] = "1",
                ["Agent:Endpoint"] = "",
            };
            if (overrides is not null)
                foreach (var entry in overrides) values[entry.Key] = entry.Value;
            configuration.AddInMemoryCollection(values);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
