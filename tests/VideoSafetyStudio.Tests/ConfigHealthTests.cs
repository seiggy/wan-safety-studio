using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using VideoSafetyStudio.Server.Health;
using VideoSafetyStudio.Tests.Support;

namespace VideoSafetyStudio.Tests;

public sealed class ConfigHealthTests
{
    private static readonly HostingEnvironmentStub Production = new("Production");

    private sealed class HostingEnvironmentStub(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static ConfigReport Evaluate(Dictionary<string, string?> settings) =>
        ConfigHealth.Evaluate(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), Production);

    private static Dictionary<string, string?> Complete()
    {
        var settings = StudioSettingsFixture.Flat();
        settings["Auth:ClientId"] = "99999999-9999-9999-9999-999999999999";
        settings["Auth:PublicOrigin"] = "https://studio.example.net";
        settings["AZURE_CLIENT_ID"] = "77777777-7777-7777-7777-777777777777";
        return settings;
    }

    [Fact]
    public void An_empty_production_configuration_lists_what_to_set_by_its_app_setting_name()
    {
        var report = Evaluate([]);
        Assert.False(report.Ready);
        Assert.Equal("misconfigured", report.Status);
        Assert.Contains(report.Issues, issue => issue.Setting == "Auth__ClientId");
        Assert.Contains(report.Issues, issue => issue.Setting == "Studio__Regions__<region>__Workspace__Id");
        Assert.All(report.Issues, issue => Assert.False(string.IsNullOrWhiteSpace(issue.Where)));
    }

    [Fact]
    public void A_complete_configuration_is_ready()
    {
        var report = Evaluate(Complete());
        Assert.True(report.Ready, string.Join("; ", report.Issues.Select(issue => issue.Setting + ": " + issue.Problem)));
        Assert.Equal("hosted-multi-region", report.Mode);
    }

    [Fact]
    public void Every_missing_studio_setting_is_reported_together_with_its_region_path()
    {
        var settings = Complete();
        settings.Remove("Studio:Regions:eastus2:Compute:IdentityClientId");
        settings["Studio:Regions:westeurope:Storage:AccountId"] = "";
        settings["Studio:Regions:westeurope:Netwrok:GpuSubnetId"] = "typo";
        var report = Evaluate(settings);
        Assert.False(report.Ready);
        var names = report.Issues.Select(issue => issue.Setting).ToArray();
        Assert.Contains("Studio__Regions__eastus2__Compute__IdentityClientId", names);
        Assert.Contains("Studio__Regions__westeurope__Storage__AccountId", names);
        Assert.Contains("Studio__Regions__westeurope__Netwrok", names);
    }

    [Fact]
    public void The_report_never_repeats_a_configured_value()
    {
        var settings = Complete();
        settings["Studio:TenantId"] = "tenant-value-that-must-not-leak";
        settings["Studio:Regions:eastus2:Storage:AccountId"] = "storage-value-that-must-not-leak";
        settings["Auth:ClientSecret"] = "secret-value-that-must-not-leak";
        var json = JsonSerializer.Serialize(Evaluate(settings));
        Assert.DoesNotContain("must-not-leak", json);
    }

    [Fact]
    public void Entra_sign_in_without_a_credential_is_reported()
    {
        var settings = Complete();
        settings.Remove("AZURE_CLIENT_ID");
        Assert.Contains(Evaluate(settings).Issues, issue => issue.Setting == "AZURE_CLIENT_ID");
    }

    private sealed class Factory : ReliabilityStorageFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Backend"] = "local",
                ["Storage:Path"] = StorageFolder,
                ["Auth:KeysPath"] = Path.Combine(StorageFolder, "keys"),
            }));
        }
    }

    [Fact]
    public async Task A_misconfigured_host_starts_serves_the_report_anonymously_and_refuses_everything_else()
    {
        await using var factory = new Factory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var report = await client.GetAsync("/api/health/config");
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        var body = await report.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("misconfigured", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("issues").GetArrayLength() > 0);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/session")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/auth/login")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
    }
}
