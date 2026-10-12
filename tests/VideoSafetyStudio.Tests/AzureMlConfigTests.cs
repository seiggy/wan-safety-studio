using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using VideoSafetyStudio.Infrastructure.AzureMl;

namespace VideoSafetyStudio.Tests;

internal static class AzureMlSamples
{
    public const string Sub = "11111111-1111-1111-1111-111111111111";
    public const string Tenant = "22222222-2222-2222-2222-222222222222";
    public const string Identity = "33333333-3333-3333-3333-333333333333";
    public const string ComputeId = $"/subscriptions/{Sub}/resourceGroups/rg-wan/providers/Microsoft.MachineLearningServices/workspaces/mlw-wan/computes/gpu1";

    /// <summary>The flat configuration keys of a prepared studio (what environment variables or Key Vault would carry).</summary>
    public static (string Key, string? Value)[] Settings(params (string Key, string? Value)[] overrides) =>
    [
        .. new (string Key, string? Value)[]
        {
            ("AzureMl:SubscriptionId", Sub), ("AzureMl:TenantId", Tenant), ("AzureMl:ResourceGroup", "rg-wan"), ("AzureMl:Workspace", "mlw-wan"),
            ("AzureMl:Compute", "gpu1"), ("AzureMl:ComputeIdentityClientId", Identity), ("AzureMl:StorageAccount", "stwan"), ("AzureMl:Container", "azureml-blobstore"),
        }.Where(item => overrides.All(o => o.Key != item.Key)),
        .. overrides,
    ];

    public static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    public static AzureMlOptions Options(params (string Key, string? Value)[] overrides) =>
        Config(Settings(overrides)).GetSection(AzureMlOptions.Section).Get<AzureMlOptions>()!;

    public static AzureMlFoundation Foundation() => AzureMlConfig.Foundation(Options());

    public static AzureMlRelease Release() => new("azureml:comfyui-wan-cu128:v7", "azureml://datastores/workspaceblobstore/paths/models/wan/", "data",
        "azureml://datastores/workspaceblobstore/paths/code/v7-wan/", "v7");

    public static AzureMlJobSpec Spec(string name = "wan-abc123abc123") => AzureMlJobSpecBuilder.Build(Foundation(), Release(), name, "comfyui-wan22-video-20260101000000-s1t1",
        "azureml://datastores/workspaceblobstore/paths/studio/web-inputs/20260101/x-img.png", "azureml://datastores/workspaceblobstore/paths/studio/web-inputs/20260101/x/workflow.json",
        new AzureMlRunSettings("a \"quoted\" $(rm -rf /) prompt", "", 42, 20, 3.5, "euler", 768, 768, 5, 81, 16, "img.png"));

    public static byte[] Png { get; } = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 0, 0];

    /// <summary>A gate over live-reloadable configuration; set AzureMl:Armed on <paramref name="source"/> and call Reload to flip it.</summary>
    public static AzureMlGate Gate(bool armed, out Action<bool> set)
    {
        var root = new ConfigurationBuilder().AddInMemoryCollection([new("AzureMl:Armed", armed.ToString())]).Build();
        set = value =>
        {
            root["AzureMl:Armed"] = value.ToString();
            root.Reload();
        };
        return new AzureMlGate(new Monitor(root));
    }

    private sealed class Monitor(IConfigurationRoot root) : IOptionsMonitor<AzureMlOptions>
    {
        public AzureMlOptions CurrentValue => root.GetSection(AzureMlOptions.Section).Get<AzureMlOptions>() ?? new AzureMlOptions();

        public AzureMlOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<AzureMlOptions, string?> listener) => null;
    }
}

public sealed class AzureMlConfigTests
{
    [Fact]
    public void Foundation_binds_from_flat_keys_and_derives_the_compute_id()
    {
        var foundation = AzureMlConfig.Foundation(AzureMlSamples.Options());
        Assert.Equal("gpu1", foundation.ComputeName);
        Assert.Equal("workspaceblobstore", foundation.DatastoreName);
        Assert.Equal(AzureMlSamples.ComputeId, foundation.ComputeId);
    }

    [Fact]
    public void Foundation_reads_environment_style_double_underscore_keys()
    {
        Environment.SetEnvironmentVariable("AzureMl__Workspace", "mlw-env");
        Environment.SetEnvironmentVariable("AzureMl__Release__EnvironmentId", "azureml:comfy:v2");
        try
        {
            var options = new ConfigurationBuilder().AddEnvironmentVariables().Build().GetSection("AzureMl").Get<AzureMlOptions>()!;
            Assert.Equal("mlw-env", options.Workspace);
            Assert.Equal("azureml:comfy:v2", options.Release.EnvironmentId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AzureMl__Workspace", null);
            Environment.SetEnvironmentVariable("AzureMl__Release__EnvironmentId", null);
        }
    }

    [Theory]
    [InlineData("AzureMl:SubscriptionId")]
    [InlineData("AzureMl:TenantId")]
    [InlineData("AzureMl:ResourceGroup")]
    [InlineData("AzureMl:Workspace")]
    [InlineData("AzureMl:Compute")]
    [InlineData("AzureMl:ComputeIdentityClientId")]
    [InlineData("AzureMl:StorageAccount")]
    [InlineData("AzureMl:Container")]
    [InlineData("AzureMl:Datastore")]
    public void Foundation_rejects_each_missing_setting_by_name(string key)
    {
        var error = Assert.Throws<InvalidOperationException>(() => AzureMlConfig.Foundation(AzureMlSamples.Options((key, ""))));
        Assert.Contains(key, error.Message);
    }

    [Theory]
    [InlineData("AzureMl:SubscriptionId", "not-a-uuid")]
    [InlineData("AzureMl:TenantId", "22222222222222222222222222222222")]
    [InlineData("AzureMl:ComputeIdentityClientId", "{33333333-3333-3333-3333-333333333333}")]
    [InlineData("AzureMl:Workspace", "bad name")]
    [InlineData("AzureMl:Compute", "gpu1/../x")]
    [InlineData("AzureMl:ResourceGroup", "rg/evil")]
    [InlineData("AzureMl:Datastore", "-lead")]
    [InlineData("AzureMl:StorageAccount", "st wan")]
    [InlineData("AzureMl:Container", "c?x")]
    [InlineData("AzureMl:MaxJobMinutes", "0")]
    [InlineData("AzureMl:MaxJobMinutes", "121")]
    [InlineData("AzureMl:MaxUploadMb", "0")]
    public void Foundation_rejects_invalid_values(string key, string value) =>
        Assert.Throws<InvalidOperationException>(() => AzureMlConfig.Foundation(AzureMlSamples.Options((key, value))));

    [Fact]
    public void Limits_default_to_120_minutes_and_64_mb()
    {
        var options = AzureMlSamples.Options();
        Assert.Equal(120, options.MaxJobMinutes);
        Assert.Equal(64, options.MaxUploadMb);
        Assert.False(options.Armed);
    }

    [Fact]
    public void Release_defaults_models_and_code_from_the_datastore_and_version()
    {
        var foundation = AzureMlSamples.Foundation();
        var release = AzureMlConfig.Release(AzureMlSamples.Options(("AzureMl:Release:EnvironmentId", "azureml:comfyui-wan-cu128:v7")), foundation)!;
        Assert.Equal("v7", release.Version);
        Assert.Equal("azureml://datastores/workspaceblobstore/paths/models/wan/", release.ModelsRef);
        Assert.Equal("azureml://datastores/workspaceblobstore/paths/code/v7-wan/", release.CodeUri);
    }

    [Fact]
    public void Release_defaults_follow_a_custom_datastore()
    {
        var options = AzureMlSamples.Options(("AzureMl:Datastore", "studiostore"), ("AzureMl:Release:EnvironmentId", "azureml:comfy:v3"));
        var release = AzureMlConfig.Release(options, AzureMlConfig.Foundation(options))!;
        Assert.Equal("azureml://datastores/studiostore/paths/models/wan/", release.ModelsRef);
        Assert.Equal("azureml://datastores/studiostore/paths/code/v3-wan/", release.CodeUri);
    }

    [Fact]
    public void Release_accepts_an_asset_models_ref_and_an_arm_environment_id()
    {
        var foundation = AzureMlSamples.Foundation();
        var arm = $"{foundation.WorkspaceArmId}/environments/comfy/versions/9";
        var release = AzureMlConfig.Release(AzureMlSamples.Options(("AzureMl:Release:EnvironmentId", arm), ("AzureMl:Release:ModelsRef", "azureml:wan-models:3")), foundation)!;
        Assert.Equal("9", release.Version);
        Assert.Equal("model", release.ModelsKind);
        Assert.Equal(arm, AzureMlJobSpecBuilder.EnvironmentArmId(foundation, release));
    }

    [Theory]
    [InlineData("docker.io/evil:1", null, null)]
    [InlineData("azureml:a:1", null, "azureml://datastores/other/paths/code/")]
    [InlineData("azureml:a:1", null, "azureml://datastores/workspaceblobstore/paths/../x/")]
    [InlineData("azureml:a:1", "https://evil/models", null)]
    [InlineData("azureml:a:1", "azureml://datastores/other/paths/models/", null)]
    public void Release_rejects_unsafe_pointers(string environment, string? models, string? code) =>
        Assert.Throws<InvalidOperationException>(() => AzureMlConfig.Release(AzureMlSamples.Options(
            ("AzureMl:Release:EnvironmentId", environment), ("AzureMl:Release:ModelsRef", models), ("AzureMl:Release:CodeUri", code)), AzureMlSamples.Foundation()));

    [Fact]
    public void No_environment_id_means_not_prepared()
    {
        Assert.Null(AzureMlConfig.Release(AzureMlSamples.Options(), AzureMlSamples.Foundation()));
        Assert.Null(AzureMlConfig.Release(AzureMlSamples.Options(("AzureMl:Release:ModelsRef", "azureml:m:1")), AzureMlSamples.Foundation()));
    }

    [Fact]
    public void Gate_follows_armed_through_the_options_monitor_and_needs_a_release()
    {
        var gate = AzureMlSamples.Gate(false, out var set);
        var release = AzureMlSamples.Release();
        Assert.False(gate.IsArmed(release));
        set(true);
        Assert.True(gate.IsArmed(release));
        Assert.False(gate.IsArmed(null));
        set(false);
        Assert.False(gate.IsArmed(release));
    }
}
