using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using VideoSafetyStudio.Tests.Support;

namespace VideoSafetyStudio.Tests;

public sealed class FeatureGateTests
{
    private sealed class Factory(string? flag) : ReliabilityStorageFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Backend"] = "local",
                ["Generation:Provider"] = "comfyui",
                ["Storage:Path"] = StorageFolder,
                ["ComfyUi:Url"] = "http://127.0.0.1:1",
                ["FEATURE_DATASET_BUILDER_ENABLED"] = flag,
            }));
        }
    }

    [Fact]
    public async Task Dataset_builder_is_hidden_by_default()
    {
        await using var factory = new Factory(null);
        using var client = factory.CreateClient();
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/features")).GetProperty("datasetBuilder").GetBoolean());
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/api/vision/projects")).StatusCode);
    }

    [Fact]
    public async Task Dataset_builder_is_available_when_the_flag_is_true()
    {
        await using var factory = new Factory("true");
        using var client = factory.CreateClient();
        Assert.True((await client.GetFromJsonAsync<JsonElement>("/api/features")).GetProperty("datasetBuilder").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/vision/projects")).StatusCode);
    }
}
