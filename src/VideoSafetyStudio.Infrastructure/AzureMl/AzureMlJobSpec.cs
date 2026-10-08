using System.Globalization;
using System.Text.Json.Nodes;

namespace VideoSafetyStudio.Infrastructure.AzureMl;

/// <summary>Everything one Azure ML command job is created from. Built only from the foundation, the release and the request.</summary>
public sealed record AzureMlJobSpec(
    string Name, string DisplayName, string Experiment, string ComputeId, string Command, string EnvironmentId,
    int TimeoutSeconds, int InstanceCount, string IdentityClientId, string RepoUri, string ModelsRef, string ModelsKind,
    string ImageUri, string WorkflowUri, string OutputUri, IReadOnlyDictionary<string, string> Literals)
{
    public string ComputeName => ComputeId[(ComputeId.LastIndexOf('/') + 1)..];

    public int BatchSize => int.TryParse(Literals.GetValueOrDefault("batch_size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : -1;

    public JsonObject ToArmBody()
    {
        var inputs = new JsonObject
        {
            ["repo"] = Folder(RepoUri),
            ["models"] = ModelsKind == "model"
                ? new JsonObject { ["jobInputType"] = "custom_model", ["uri"] = ModelsRef, ["mode"] = "ReadOnlyMount" }
                : Folder(ModelsRef),
            ["input_image_url"] = File(ImageUri),
            ["workflow_json"] = File(WorkflowUri),
        };
        foreach (var (key, value) in Literals)
        {
            inputs[key] = new JsonObject { ["jobInputType"] = "literal", ["value"] = value };
        }

        return new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["jobType"] = "Command",
                ["displayName"] = DisplayName,
                ["experimentName"] = Experiment,
                ["computeId"] = ComputeId,
                ["command"] = Command,
                ["environmentId"] = EnvironmentId,
                ["inputs"] = inputs,
                ["outputs"] = new JsonObject { ["generated"] = new JsonObject { ["jobOutputType"] = "uri_folder", ["uri"] = OutputUri, ["mode"] = "ReadWriteMount" } },
                ["limits"] = new JsonObject { ["jobLimitsType"] = "Command", ["timeout"] = $"PT{TimeoutSeconds}S" },
                ["resources"] = new JsonObject { ["instanceCount"] = InstanceCount },
                ["identity"] = new JsonObject { ["identityType"] = "Managed", ["clientId"] = IdentityClientId },
                ["environmentVariables"] = new JsonObject(),
            },
        };

        static JsonObject Folder(string uri) => new() { ["jobInputType"] = "uri_folder", ["uri"] = uri, ["mode"] = "ReadOnlyMount" };
        static JsonObject File(string uri) => new() { ["jobInputType"] = "uri_file", ["uri"] = uri, ["mode"] = "Download" };
    }
}

/// <summary>The mode's own settings for one job; run_workflow_job.py overrides the graph with them, so they must match what was rendered.</summary>
public sealed record AzureMlRunSettings(
    string Prompt, string Negative, long Seed, int Steps, double Cfg, string Sampler, int Width, int Height,
    double DurationSeconds, int Length, int Fps, string ImageFilename);

public static class AzureMlJobSpecBuilder
{
    public const string Experiment = "comfyui-wan";
    public const int MaxSeconds = 7200;

    public const string Command =
        "cd \"${{inputs.repo}}\" && python azureml/run_workflow_job.py --workflow \"${{inputs.workflow_json}}\" --models-dir ${{inputs.models}} --output-dir ${{outputs.generated}} --startup-timeout 900 --run-timeout 7200 " +
        "--positive-prompt \"${{inputs.positive_prompt}}\" --negative-prompt \"${{inputs.negative_prompt}}\" --seed ${{inputs.seed}} --steps ${{inputs.steps}} --cfg ${{inputs.cfg}} " +
        "--sampler-name \"${{inputs.sampler_name}}\" --scheduler \"${{inputs.scheduler}}\" --denoise ${{inputs.denoise}} --width ${{inputs.width}} --height ${{inputs.height}} " +
        "--duration-seconds ${{inputs.duration_seconds}} --length ${{inputs.length}} --batch-size ${{inputs.batch_size}} --fps ${{inputs.fps}} --filename-prefix \"${{inputs.filename_prefix}}\" " +
        "--input-image-url \"file://${{inputs.input_image_url}}\" --input-image-filename \"${{inputs.input_image_filename}}\"";

    public static string EnvironmentArmId(AzureMlFoundation foundation, AzureMlRelease release)
    {
        if (!release.EnvironmentId.StartsWith("azureml:", StringComparison.Ordinal))
        {
            return release.EnvironmentId;
        }

        var parts = release.EnvironmentId.Split(':');
        return $"{foundation.WorkspaceArmId}/environments/{parts[1]}/versions/{parts[2]}";
    }

    public static AzureMlJobSpec Build(AzureMlFoundation foundation, AzureMlRelease release, string jobName, string displayName,
        string imageUri, string workflowUri, AzureMlRunSettings run)
    {
        var literals = new Dictionary<string, string>
        {
            ["positive_prompt"] = ShellDoubleQuoted(run.Prompt),
            ["negative_prompt"] = ShellDoubleQuoted(run.Negative),
            ["seed"] = run.Seed.ToString(CultureInfo.InvariantCulture),
            ["steps"] = run.Steps.ToString(CultureInfo.InvariantCulture),
            ["cfg"] = Decimal(run.Cfg),
            ["sampler_name"] = run.Sampler,
            ["scheduler"] = "simple",
            ["denoise"] = "1.0",
            ["width"] = run.Width.ToString(CultureInfo.InvariantCulture),
            ["height"] = run.Height.ToString(CultureInfo.InvariantCulture),
            ["duration_seconds"] = Decimal(run.DurationSeconds),
            ["length"] = run.Length.ToString(CultureInfo.InvariantCulture),
            ["batch_size"] = "1",
            ["fps"] = run.Fps.ToString(CultureInfo.InvariantCulture),
            ["filename_prefix"] = "ComfyUI",
            ["input_image_filename"] = run.ImageFilename,
        };
        return new AzureMlJobSpec(jobName, displayName, Experiment, foundation.ComputeId, Command, EnvironmentArmId(foundation, release),
            MaxSeconds, 1, foundation.ComputeIdentityClientId, release.CodeUri, release.ModelsRef, release.ModelsKind,
            imageUri, workflowUri, foundation.DatastorePath($"video-library/{jobName}/"), literals);
    }

    private static string Decimal(double value) => value.ToString("0.0###", CultureInfo.InvariantCulture);

    /// <summary>Azure ML pastes literal inputs into the shell command, inside double quotes: escape what a shell would still interpret there.</summary>
    internal static string ShellDoubleQuoted(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`").Replace("\r", " ");
}
