using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using VideoSafetyStudio.Tests.Support;

namespace VideoSafetyStudio.Tests;

public sealed class ReliabilityFixtureTests
{
    private sealed class Factory : ReliabilityStorageFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Backend"] = "local",
                ["Storage:Path"] = StorageFolder,
                ["Generation:Provider"] = "comfyui",
                ["ComfyUi:Url"] = "http://127.0.0.1:1",
            }));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hosted_fixture_deletes_its_storage_when_an_assertion_fails(bool asyncDisposal)
    {
        var factory = new Factory();
        var folder = factory.StorageFolder;
        var failure = await Record.ExceptionAsync(async () =>
        {
            try
            {
                using var client = factory.CreateClient();
                using var response = await client.GetAsync("/healthz");
                response.EnsureSuccessStatusCode();
                await File.WriteAllTextAsync(Path.Combine(folder, "assertion-failure-marker"), "owned");
                Assert.Fail("Intentional assertion failure exercises fixture teardown.");
            }
            finally
            {
                if (asyncDisposal)
                {
                    await factory.DisposeAsync();
                }
                else
                {
                    factory.Dispose();
                }
            }
        });

        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(failure);
        Assert.Contains("Intentional assertion failure", failure.Message);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Manual_clock_fires_changed_timers_at_their_deadline_and_disposes_them()
    {
        var time = new ReliabilityTimeProvider();
        var started = time.GetUtcNow();
        var calls = 0;
        using var timer = time.CreateTimer(_ => calls++, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1));
        Assert.Equal(0, calls);
        time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(1, calls);

        timer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, calls);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, calls);
        timer.Dispose();
        Assert.False(timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan));
        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(2, calls);
        Assert.Equal(started + TimeSpan.FromSeconds(3) + TimeSpan.FromDays(1), time.GetUtcNow());
    }
}
