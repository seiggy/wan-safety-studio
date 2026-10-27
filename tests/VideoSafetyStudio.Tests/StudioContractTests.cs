using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using VideoSafetyStudio.Application.Operations;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Tests.Support;

namespace VideoSafetyStudio.Tests;

/// <summary>The one explicit settings document (<c>contracts\fixtures\studio-settings.json</c>, shaped like appsettings.json) every test and pipeline tool shares.</summary>
internal static class StudioSettingsFixture
{
    public static string Path { get; } = System.IO.Path.Combine(TestPaths.RepoRoot, "contracts", "fixtures", "studio-settings.json");

    public static JsonObject Document() => JsonNode.Parse(File.ReadAllText(Path))!.AsObject();

    public static StudioOptions Options(JsonObject? document = null, Action<StudioOptions>? change = null)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes((document ?? Document()).ToJsonString()));
        var options = new ConfigurationBuilder().AddJsonStream(stream).Build().GetSection(StudioOptions.Section).Get<StudioOptions>()!;
        change?.Invoke(options);
        return options;
    }

    /// <summary>The document as flat configuration keys (<c>Studio:Regions:eastus2:Compute:Id</c>), as App Service application settings supply them.</summary>
    public static Dictionary<string, string?> Flat(JsonObject? document = null)
    {
        var flat = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        void Walk(string prefix, JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj: foreach (var (key, value) in obj) Walk(prefix + ":" + key, value); break;
                case JsonArray array: for (var i = 0; i < array.Count; i++) Walk($"{prefix}:{i}", array[i]); break;
                default: flat[prefix] = node!.ToString(); break;
            }
        }
        Walk("Studio", (document ?? Document())["Studio"]);
        return flat;
    }

    public static StudioFoundation Foundation(Action<StudioOptions>? change = null) => StudioOptionsMapper.ToFoundation(Options(null, change));

    /// <summary>The same document with one more region cloned from westeurope, to show the contracts have no fixed region count.</summary>
    public static JsonObject WithThirdRegion()
    {
        var document = Document();
        var regions = document["Studio"]!["Regions"]!.AsObject();
        var clone = JsonNode.Parse(regions["westeurope"]!.ToJsonString().Replace("westeurope", "northeurope").Replace("c9d87636", "aaaa4444").Replace("customer-west", "customer-north").Replace("10.43.1.0/26", "10.44.1.0/26"))!;
        clone["Compute"]!["MaxJobMinutes"] = 60;
        regions["northeurope"] = clone;
        return document;
    }
}

public sealed class StudioContractTests
{
    [Fact]
    public void The_shared_settings_document_binds_through_configuration_and_maps_to_the_runtime_model()
    {
        var options = StudioSettingsFixture.Options();
        Assert.True(options.Configured);
        var foundation = StudioOptionsMapper.ToFoundation(options);
        Assert.Equal(2, foundation.Regions.Count);
        Assert.Equal("eastus2", foundation.ControlRegion);
        Assert.Equal(30, foundation.Regions["westeurope"].Policy.MaxJobMinutes);
        Assert.Equal("wan-control", foundation.Regions["eastus2"].ControlContainer);
        Assert.Equal("terraform", foundation.Regions["eastus2"].OwnershipTags["managedBy"]);
        Assert.False(StudioSettingsFixture.Options(change: o => o.Regions.Clear()).Configured);
    }

    [Fact]
    public void App_Service_style_flat_keys_bind_the_same_nested_settings_including_dictionaries_and_arrays()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(StudioSettingsFixture.Flat()).Build();
        var bound = configuration.GetSection(StudioOptions.Section).Get<StudioOptions>()!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(StudioContracts.Write(StudioOptionsMapper.ToFoundation(bound))),
            JsonNode.Parse(StudioContracts.Write(StudioSettingsFixture.Foundation()))));
    }

    [Theory]
    [InlineData("TenantId", "not-a-guid", "Studio:TenantId")]
    [InlineData("DeploymentName", "Bad Name", "Studio:DeploymentName")]
    [InlineData("ControlRegion", "unconfigured", "Studio:ControlRegion")]
    [InlineData("Regions:BAD-KEY", null, "region key")]
    [InlineData("Regions:eastus2:Workspace:Id", "/subscriptions/33333333-3333-3333-3333-333333333333/resourceGroups/x/providers/Microsoft.MachineLearningServices/workspaces/w", "Workspace:Id")]
    [InlineData("Regions:eastus2:Compute:Id", "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/x/providers/Microsoft.MachineLearningServices/workspaces/w/computes/c", "Compute:Id")]
    [InlineData("Regions:eastus2:Compute:IdentityClientId", "00000000-0000-0000-0000-000000000000", "IdentityClientId")]
    [InlineData("Regions:eastus2:Compute:MaxJobMinutes", "121", "MaxJobMinutes")]
    [InlineData("Regions:eastus2:Compute:MaxNodes", "0", "MaxNodes")]
    [InlineData("Regions:eastus2:Compute:MaxPaygHourlyUsd", "0", "MaxPaygHourlyUsd")]
    [InlineData("Regions:eastus2:Storage:AccountId", "", "Storage:AccountId")]
    [InlineData("Regions:eastus2:Platform:RegistryId", "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/x/providers/Microsoft.KeyVault/vaults/v", "RegistryId")]
    [InlineData("Regions:eastus2:Network:GpuSubnetCidr", "10.42.1.1/26", "GpuSubnetCidr")]
    [InlineData("Regions:eastus2:Network:PrivateConnectivityHosts", "x", "PrivateConnectivityHosts")]
    [InlineData("Regions:eastus2:OwnershipTags:managedBy", null, "OwnershipTags")]
    public void Invalid_settings_are_rejected_with_the_setting_named(string path, string? value, string expected)
    {
        var document = StudioSettingsFixture.Document();
        JsonNode node = document["Studio"]!;
        var parts = path.Split(':');
        foreach (var part in parts[..^1]) node = node[part]!;
        if (parts[^1] == "PrivateConnectivityHosts") node[parts[^1]] = new JsonArray(value);
        else if (path == "Regions:BAD-KEY") node[parts[^1]] = JsonNode.Parse(document["Studio"]!["Regions"]!["eastus2"]!.ToJsonString());
        else if (value is null) node.AsObject().Remove(parts[^1]);
        else node[parts[^1]] = int.TryParse(value, out var number) && !path.EndsWith("Id") && !path.EndsWith("ClientId") ? JsonValue.Create(number) : path.EndsWith("MaxPaygHourlyUsd") ? JsonValue.Create(0m) : JsonValue.Create(value);
        var error = Assert.Throws<ValidationException>(() => StudioOptionsMapper.ToFoundation(StudioSettingsFixture.Options(document)));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void A_misspelled_setting_is_reported_with_its_full_path_instead_of_silently_keeping_a_default()
    {
        var clean = new ConfigurationBuilder().AddInMemoryCollection(StudioSettingsFixture.Flat()).Build();
        Assert.Empty(VideoSafetyStudio.Infrastructure.Operations.StudioOptionsKeys.Unknown(clean.GetSection("Studio")));

        var flat = StudioSettingsFixture.Flat();
        flat["Studio:Regions:eastus2:Compute:MaxJobMinute"] = "30";
        flat["Studio:Regions:westeurope:Netwrok:GpuSubnetId"] = "x";
        flat["Studio:Regions:eastus2:OwnershipTags:anyTagName"] = "free-form keys are allowed";
        var unknown = VideoSafetyStudio.Infrastructure.Operations.StudioOptionsKeys.Unknown(new ConfigurationBuilder().AddInMemoryCollection(flat).Build().GetSection("Studio"));
        Assert.Equal(["Studio:Regions:eastus2:Compute:MaxJobMinute", "Studio:Regions:westeurope:Netwrok"], unknown.Order().ToArray());
    }

    [Fact]
    public void Resources_that_depend_on_managed_egress_are_required_only_when_it_is_enabled()
    {
        var withoutEgress = StudioSettingsFixture.Foundation(options =>
        {
            options.Regions["eastus2"].Network.NatGatewayId = null;
            options.Regions["eastus2"].Network.PublicIpId = null;
        });
        Assert.False(withoutEgress.Regions["eastus2"].Policy.ManageComputeEgress);
        var error = Assert.Throws<ValidationException>(() => StudioSettingsFixture.Foundation(options =>
        {
            options.Regions["eastus2"].Compute.ManageEgress = true;
            options.Regions["eastus2"].Network.NatGatewayId = null;
        }));
        Assert.Contains("NatGatewayId", error.Message);
        Assert.True(StudioSettingsFixture.Foundation(options => options.Regions["eastus2"].Compute.ManageEgress = true).Regions["eastus2"].Policy.ManageComputeEgress);
    }

    [Fact]
    public void Ownership_tags_default_to_the_convention_and_a_customer_nsg_must_be_the_gpu_nsg()
    {
        var foundation = StudioSettingsFixture.Foundation(options => options.Regions["eastus2"].OwnershipTags.Clear());
        Assert.Equal(["application", "deployment", "managedBy", "region"], foundation.Regions["eastus2"].OwnershipTags.Keys.Order().ToArray());
        Assert.Equal("eastus2", foundation.Regions["eastus2"].OwnershipTags["region"]);
        var customer = StudioSettingsFixture.Foundation(options => options.Regions["eastus2"].Network.CustomerManagedNsg = true);
        Assert.Equal(customer.Regions["eastus2"].NetworkSecurityGroupId, customer.Regions["eastus2"].LandingZone.ExistingGpuNsgId);
    }

    [Fact]
    public void Regions_cannot_share_a_workspace_or_gpu_subnet()
    {
        var shared = Assert.Throws<ValidationException>(() => StudioSettingsFixture.Foundation(options =>
            options.Regions["westeurope"].Network.GpuSubnetId = options.Regions["eastus2"].Network.GpuSubnetId));
        Assert.Contains("share", shared.Message);
    }

    private static JsonObject Fixture() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(TestPaths.RepoRoot, "contracts", "fixtures", "studio.json")))!.AsObject();

    private static T Read<T>(JsonNode node) => JsonSerializer.Deserialize<T>(node.ToJsonString(), StudioContracts.Options)!;
    private static string Json(JsonNode node) => node.ToJsonString();

    private static (StudioFoundation Foundation, GpuBuildManifest Build) Load(JsonObject fixture) =>
        (StudioSettingsFixture.Foundation(), StudioContracts.ReadBuild(Json(fixture["build"]!)));

    [Fact]
    public void Checked_in_contracts_match_types_and_shared_fixtures()
    {
        var samples = Samples();
        var update = Environment.GetEnvironmentVariable("WAN_STUDIO_UPDATE_CONTRACTS") == "1";
        Assert.False(update && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true",
            "CI must validate committed contract schemas/fixtures, not regenerate them.");
        foreach (var (name, _) in StudioContractSchemas.Types)
        {
            var schema = StudioContractSchemas.Export(name);
            var path = Path.Combine(TestPaths.RepoRoot, "contracts", name + ".schema.json");
            if (update)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            }
            Assert.True(File.Exists(path), $"Missing exported contract: {path}");
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), schema), $"Schema drift: {name}");
        }
        var fixturePath = Path.Combine(TestPaths.RepoRoot, "contracts", "fixtures", "studio.json");
        if (update)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
            File.WriteAllText(fixturePath, samples.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        Assert.True(JsonNode.DeepEquals(Fixture(), samples), "Golden contract fixture drift.");
        var (foundation, build) = Load(samples);
        Assert.NotNull(StudioContracts.ReadRelease(Json(samples["release"]!), foundation, build));
        Assert.NotNull(StudioContracts.ReadSettings(Json(samples["settings"]!), foundation));
        Assert.NotNull(StudioContracts.ReadJob(Json(samples["job"]!), foundation));
        Assert.NotNull(StudioContracts.ReadBuild(Json(samples["unicodeBuild"]!)));
    }

    [Theory]
    [InlineData("\"schemaVersion\":1,\"schemaVersion\":1")]
    [InlineData("\"schemaVersion\":\"1\"")]
    [InlineData("\"SchemaVersion\":1")]
    [InlineData("\"schemaVersion\":1,\"unexpected\":false")]
    [InlineData("\"schemaVersion\":1.0")]
    public void Boundary_json_rejects_duplicate_wrong_case_unknown_and_coerced_fields(string replacement)
    {
        var fixture = Fixture();
        var (foundation, _) = Load(fixture);
        var json = Json(fixture["settings"]!).Replace("\"schemaVersion\":1", replacement);
        Assert.Throws<ValidationException>(() => StudioContracts.ReadSettings(json, foundation));
    }

    [Fact]
    public void Region_contracts_do_not_have_a_fixed_two_region_limit()
    {
        var fixture = Samples(includeThirdRegion: true);
        var foundation = StudioOptionsMapper.ToFoundation(StudioSettingsFixture.Options(StudioSettingsFixture.WithThirdRegion()));
        var build = StudioContracts.ReadBuild(Json(fixture["build"]!));
        Assert.Equal(3, foundation.Regions.Count);
        Assert.NotNull(StudioContracts.ReadSettings(Json(fixture["settings"]!), foundation));
        Assert.NotNull(StudioContracts.ReadRelease(Json(fixture["release"]!), foundation, build));
        Assert.NotNull(StudioContracts.ReadJob(Json(fixture["job"]!), foundation));
    }

    [Fact]
    public void Build_identity_is_independent_of_region_policy_and_file_order()
    {
        var fixture = Fixture();
        var (foundation, build) = Load(fixture);
        var original = build.ArtifactId;
        var reversed = build with { Code = [.. build.Code.Reverse()], Models = [.. build.Models.Reverse()] };
        Assert.Equal(original, StudioContracts.ArtifactId(reversed));
        var changed = foundation with { Regions = new(foundation.Regions) };
        changed.Regions["eastus2"] = changed.Regions["eastus2"] with
        {
            Policy = changed.Regions["eastus2"].Policy with { MaxJobMinutes = 15, MaxNodes = 2 },
        };
        Assert.Equal(StudioContracts.ScopeFingerprint(foundation), StudioContracts.ScopeFingerprint(changed));
        Assert.Equal(original, StudioContracts.ReadBuild(Json(fixture["build"]!)).ArtifactId);
        var newCode = build with { Code = [.. build.Code.Select((file, index) => index == 0 ? file with { Sha256 = new string('f', 64) } : file)] };
        Assert.NotEqual(original, StudioContracts.ArtifactId(newCode));
    }

    [Theory]
    [InlineData("azureml/config.py")]
    [InlineData("azureml/cost_guard.py")]
    [InlineData("azureml/register_and_submit.py")]
    [InlineData("azureml/web_submit.py")]
    [InlineData("src/.env")]
    [InlineData("src/.cache/credentials.json")]
    [InlineData("src/models/example.safetensors")]
    [InlineData("src/comfy/../secret.py")]
    [InlineData("src\\comfy\\something.py")]
    [InlineData("src/%2e%2e/secret.py")]
    public void Build_rejects_operator_secrets_weights_and_traversal_even_with_a_rehashed_manifest(string path)
    {
        var (foundation, build) = Load(Fixture());
        build = build with { Code = [.. build.Code, new ArtifactFile(path, 1, new string('b', 64))] };
        build = build with { ArtifactId = StudioContracts.ArtifactId(build) };
        Assert.Throws<ValidationException>(() => StudioContracts.ReadBuild(StudioContracts.Write(build)));
    }

    [Fact]
    public void Build_keeps_legitimate_nested_model_and_input_packages()
    {
        var (foundation, build) = Load(Fixture());
        build = build with
        {
            Code = [.. build.Code,
                new ArtifactFile("src/comfy/ldm/models/autoencoder.py", 1, new string('b', 64)),
                new ArtifactFile("src/comfy_api/input/video_types.py", 1, new string('b', 64))],
        };
        build = build with { ArtifactId = StudioContracts.ArtifactId(build) };
        Assert.Equal(build.ArtifactId, StudioContracts.ReadBuild(StudioContracts.Write(build)).ArtifactId);
    }

    [Fact]
    public void Code_keeps_empty_package_markers_but_empty_models_are_rejected()
    {
        var (_, original) = Load(Fixture());
        var build = original with { Code = [.. original.Code, new ArtifactFile("src/comfy/__init__.py", 0,
            Convert.ToHexStringLower(SHA256.HashData([])))] };
        build = build with { ArtifactId = StudioContracts.ArtifactId(build) };
        Assert.NotNull(StudioContracts.ReadBuild(StudioContracts.Write(build)));
        build = original with { Models = [original.Models[0] with { Bytes = 0 }] };
        build = build with { ArtifactId = StudioContracts.ArtifactId(build) };
        Assert.Throws<ValidationException>(() => StudioContracts.ReadBuild(StudioContracts.Write(build)));
    }

    [Fact]
    public void Build_rejects_control_characters_and_source_revision_traversal_after_rehashing()
    {
        var (_, original) = Load(Fixture());
        var cases = new[]
        {
            original with { BaseImage = original.BaseImage + "\n" },
            original with { ImageDigest = original.ImageDigest + "\n" },
            original with { Code = [.. original.Code.Select((file, index) => index == 0 ? file with { Sha256 = file.Sha256 + "\n" } : file)] },
            original with { Models = [original.Models[0] with { Source = original.Models[0].Source.Replace("/split_files/", "/../../split_files/") }] },
        };
        foreach (var changed in cases)
        {
            var manifest = changed with { ArtifactId = StudioContracts.ArtifactId(changed) };
            Assert.Throws<ValidationException>(() => StudioContracts.ReadBuild(StudioContracts.Write(manifest)));
        }
    }

    [Theory]
    [InlineData("modelsRef", "azureml:models:hash-version")]
    [InlineData("modelsRef", "azureml:models:0")]
    [InlineData("modelsRef", "azureml:models:01")]
    [InlineData("codeUri", "azureml://datastores/wan_blob/paths/code/release/")]
    [InlineData("codeUri", "azureml://datastores/wan_release/paths/code/release/?sig=secret")]
    [InlineData("modelsUri", "azureml://datastores/wan_release/paths/models/../other/")]
    [InlineData("image", "other.azurecr.io/comfyui@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("workspaceId", "/subscriptions/elsewhere/workspaces/other")]
    public void Release_rejects_wrong_scope_inputs_registry_or_model_version(string field, string bad)
    {
        var fixture = Fixture();
        var (foundation, build) = Load(fixture);
        fixture["release"]![field] = bad;
        Assert.Throws<ValidationException>(() => StudioContracts.ReadRelease(Json(fixture["release"]!), foundation, build));
    }

    [Theory]
    [InlineData("maxJobMinutes", 121)]
    [InlineData("maxConcurrentJobs", 2)]
    [InlineData("priority", -1)]
    public void Settings_reject_regional_policy_escalation(string field, int bad)
    {
        var fixture = Fixture();
        var (foundation, _) = Load(fixture);
        fixture["settings"]!["regions"]!["eastus2"]![field] = bad;
        Assert.Throws<ValidationException>(() => StudioContracts.ReadSettings(Json(fixture["settings"]!), foundation));
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(2, 1)]
    [InlineData(2, 4)]
    [InlineData(long.MaxValue, 1)]
    public void Settings_reject_stale_or_overflowed_state_versions(long current, long proposed) =>
        Assert.Throws<ConflictException>(() => StudioContracts.RequireNextVersion(current, proposed));

    [Fact]
    public void State_versions_advance_exactly_once() => StudioContracts.RequireNextVersion(1, 2);

    [Fact]
    public void A_job_can_move_only_after_verified_cancellation_and_keeps_exact_regional_output()
    {
        var fixture = Fixture();
        var (foundation, _) = Load(fixture);
        var job = StudioContracts.ReadJob(Json(fixture["job"]!), foundation);
        var first = job.Attempts[0] with
        {
            Phase = AttemptPhase.Canceled,
            UpdatedAt = job.Attempts[0].UpdatedAt.AddMinutes(10),
            TerminalVerifiedAt = job.Attempts[0].UpdatedAt.AddMinutes(10),
        };
        var next = first with
        {
            Number = 2, Region = "westeurope", WorkspaceId = foundation.Regions["westeurope"].WorkspaceId,
            RemoteName = job.Name + "-a2", TimeoutSeconds = 1800, Phase = AttemptPhase.Intent,
            CreatedAt = first.TerminalVerifiedAt.Value.AddSeconds(1), UpdatedAt = first.TerminalVerifiedAt.Value.AddSeconds(1),
            TerminalVerifiedAt = null, OutputUri = $"azureml://datastores/wan_blob/paths/video-library/{job.Name}-a2/",
        };
        Assert.NotNull(StudioContracts.ReadJob(StudioContracts.Write(job with { Attempts = [first, next] }), foundation));
        foreach (var phase in new[] { AttemptPhase.Intent, AttemptPhase.Unknown, AttemptPhase.Running, AttemptPhase.Completed, AttemptPhase.Failed })
        {
            var terminal = phase is AttemptPhase.Completed or AttemptPhase.Failed;
            Assert.Throws<ValidationException>(() => StudioContracts.ReadJob(
                StudioContracts.Write(job with { Attempts = [first with { Phase = phase, TerminalVerifiedAt = terminal ? first.TerminalVerifiedAt : null }, next] }), foundation));
        }
        Assert.Throws<ValidationException>(() => StudioContracts.ReadJob(
            StudioContracts.Write(job with { Attempts = [first with { StartedAt = first.CreatedAt }, next] }), foundation));
    }

    [Fact]
    public void A_job_rejects_unverified_terminal_state_and_out_of_scope_output()
    {
        var fixture = Fixture();
        var (foundation, _) = Load(fixture);
        var job = StudioContracts.ReadJob(Json(fixture["job"]!), foundation);
        Assert.Throws<ValidationException>(() => StudioContracts.ReadJob(
            StudioContracts.Write(job with { Attempts = [job.Attempts[0] with { Phase = AttemptPhase.Completed }] }), foundation));
        Assert.Throws<ValidationException>(() => StudioContracts.ReadJob(
            StudioContracts.Write(job with { Attempts = [job.Attempts[0] with { OutputUri = "azureml://datastores/wan_blob/paths/video-library/another-job/" }] }), foundation));
    }

    [Theory]
    [InlineData("build", "code")]
    [InlineData("build", "models")]
    [InlineData("job", "attempts")]
    public void Null_collections_are_validation_errors_not_runtime_failures(string contract, string field)
    {
        var fixture = Fixture();
        var (foundation, _) = Load(fixture);
        fixture[contract]![field] = null;
        Assert.Throws<ValidationException>(() =>
        {
            switch (contract)
            {
                case "build": StudioContracts.ReadBuild(Json(fixture[contract]!)); break;
                case "job": StudioContracts.ReadJob(Json(fixture[contract]!), foundation); break;
            }
        });
    }

    private static JsonObject Samples(bool includeThirdRegion = false)
    {
        var options = StudioSettingsFixture.Options(includeThirdRegion ? StudioSettingsFixture.WithThirdRegion() : null);
        var foundation = StudioOptionsMapper.ToFoundation(options);
        var scope = foundation.Scope;
        var foundations = foundation.Regions;
        var regions = options.Regions.Select(pair => (Region: pair.Key, Minutes: pair.Value.Compute.MaxJobMinutes)).ToList();
        static ArtifactFile File(string path) => new(path, 1, Convert.ToHexStringLower(SHA256.HashData("x"u8)));        var build = new GpuBuildManifest(1, "", "wan", "dc0d29031b73a5ba7376d910adb7a58c206f32b1",
            "pytorch/pytorch:2.8.0-cuda12.8-cudnn9-runtime@sha256:417bd75df6365104c283ea4c1651fb3530d9eb5a4c2fafa51943cff2a94e6385",
            new string('d', 64), "sha256:" + new string('a', 64),
            [File("LICENSE"), File("NOTICE.txt"), File("src/main.py"), File("azureml/run_workflow_job.py"),
                File("azureml/aml_project_layout.py"), File("azureml/workflow_profiles.py"), File("azureml/workflow_utils.py"), File("azureml/extra_model_paths.yaml")],
            [new ModelArtifact("diffusion_models/example.safetensors", 5, Convert.ToHexStringLower(SHA256.HashData("model"u8)),
                "https://huggingface.co/Comfy-Org/Wan_2.2_ComfyUI_Repackaged/resolve/c4f60d30c55a624e35427060fdd217579a6c1d77/split_files/diffusion_models/example.safetensors",
                "Comfy-Org/Wan_2.2_ComfyUI_Repackaged", "c4f60d30c55a624e35427060fdd217579a6c1d77", "apache-2.0")],
            [File("Comfy-Org--Wan_2.2_ComfyUI_Repackaged--README.md")],
            [File("Dockerfile"), File("profiles.json"), File("runtime-ltx.patch"),
                new ArtifactFile("requirements-runtime.lock", 1, new string('d', 64))]);
        build = build with { ArtifactId = StudioContracts.ArtifactId(build) };
        var unicode = build with
        {
            Code = [.. build.Code,
                File("src/unicode-\u00e9-\U0001f600.py"),
                File("src/unicode-\ue000.py"),
                File("src/unicode-\U00010000.py")],
            Models = [build.Models[0] with { License = "Unicode \u00e9 \U0001f600 \u2028 \u000b" }],
        };
        unicode = unicode with { ArtifactId = StudioContracts.ArtifactId(unicode) };
        var first = foundations["eastus2"];
        var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var binding = new RegionalRelease(1, "eastus2", first.WorkspaceId, StudioContracts.RegionFingerprint(foundation, "eastus2"), build.ArtifactId,
            $"{first.WorkspaceId}/environments/wan-demo-wan/versions/{build.ArtifactId}", $"azureml:wan-demo-models-{build.ArtifactId[..12]}:1",
            $"azureml://datastores/{first.ReleaseDatastore}/paths/code/{build.ArtifactId}/",
            $"azureml://datastores/{first.ReleaseDatastore}/paths/models/{build.Models[0].Sha256}/",
            $"{StudioResourceNames.NameOf(first.RegistryId)}.azurecr.io/comfyui@{build.ImageDigest}", at,
            "88888888-8888-8888-8888-888888888888");
        var settings = new StudioSettings(1, 1, StudioContracts.ScopeFingerprint(foundation), 1, 10,
            regions.Select((value, index) => (value.Region, Settings: new RegionSettings(1, value.Minutes, index)))
                .ToDictionary(value => value.Region, value => value.Settings, StringComparer.Ordinal));
        const string jobName = "wan-010203040506";
        var job = new StudioJobRecord(1, 1, scope, jobName,
            [new JobAttempt(1, "eastus2", first.WorkspaceId, jobName + "-a1", build.ArtifactId, 7200, AttemptPhase.Intent, at, at, null, null,
                $"azureml://datastores/{first.Datastore}/paths/video-library/{jobName}-a1/")]);
        return new JsonObject
        {
            ["foundation"] = JsonSerializer.SerializeToNode(foundation, StudioContracts.Options),
            ["build"] = JsonSerializer.SerializeToNode(build, StudioContracts.Options),
            ["unicodeBuild"] = JsonSerializer.SerializeToNode(unicode, StudioContracts.Options),
            ["release"] = JsonSerializer.SerializeToNode(binding, StudioContracts.Options),
            ["settings"] = JsonSerializer.SerializeToNode(settings, StudioContracts.Options),
            ["job"] = JsonSerializer.SerializeToNode(job, StudioContracts.Options),
        };
    }
}
