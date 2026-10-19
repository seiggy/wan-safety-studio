using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using VideoSafetyStudio.Application.Generation;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Infrastructure.Storage;
using VideoSafetyStudio.Tests.Support;

namespace VideoSafetyStudio.Tests;

public sealed class ArchiveTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private ArchiveService Service() => new(new LocalFolderObjectStore(_temp.Path));

    [Fact]
    public async Task Starts_empty_and_remembers_archived_videos_across_instances()
    {
        Assert.Empty(await Service().ListAsync());

        await Service().SetAsync("wan-b", true);
        await Service().SetAsync("wan-a", true);

        Assert.Equal(["wan-a", "wan-b"], await Service().ListAsync());
    }

    [Fact]
    public async Task Unarchiving_removes_only_that_video_and_is_idempotent()
    {
        var service = Service();
        await service.SetAsync("wan-a", true);
        await service.SetAsync("wan-b", true);

        await service.SetAsync("wan-a", false);
        var result = await service.SetAsync("wan-a", false);

        Assert.Equal(["wan-b"], result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../evil")]
    [InlineData("has space")]
    [InlineData("wan-a\n")]
    public async Task Rejects_names_that_are_not_job_names(string? name) =>
        await Assert.ThrowsAsync<ValidationException>(() => Service().SetAsync(name, true));

    [Fact]
    public async Task Concurrent_changes_in_one_process_are_all_kept()
    {
        var service = Service();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => service.SetAsync($"wan-{i:D2}", true)));

        Assert.Equal(20, (await service.ListAsync()).Count);
    }

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

    [Fact]
    public async Task Archive_api_round_trips_and_rejects_a_bad_name()
    {
        await using var factory = new Factory();
        using var client = factory.CreateClient();

        using var archived = await client.PostAsJsonAsync("/api/archived", new { jobName = "wan-a", archived = true });
        Assert.Equal(["wan-a"], (await archived.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("archived").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>("/api/archived")).GetProperty("archived").GetArrayLength());

        using var rejected = await client.PostAsJsonAsync("/api/archived", new { jobName = "../x", archived = true });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }
}
