using System.Text.Json.Nodes;
using VideoSafetyStudio.Application;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Application.Generation;
using VideoSafetyStudio.Domain.Generation;
using VideoSafetyStudio.Infrastructure.ComfyUi;
using VideoSafetyStudio.Tests.Support;

namespace VideoSafetyStudio.Tests;

public class WorkflowTests
{
    private static JsonObject Load(string file) => JsonNode.Parse(File.ReadAllText(Path.Combine(TestPaths.Workflows, file)))!.AsObject();

    [Fact]
    public void Gpu_probe_workflows_match_every_mode_rendered_by_the_production_renderer()
    {
        var folder = Path.Combine(TestPaths.RepoRoot, "contracts", "fixtures", "wan-workflows");
        var update = Environment.GetEnvironmentVariable("WAN_STUDIO_UPDATE_CONTRACTS") == "1";
        Assert.False(update && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true");
        var index = new JsonObject();
        foreach (var mode in WanModes.All)
        {
            var rendered = WorkflowRenderer.Render(Load(mode.Template), WorkflowValues.Build(new GenerationOptions(),
                mode, "CPU build probe", "", "input.png", 1, 512, 512, WorkflowValues.Length(1, mode.Fps), "ComfyUI"));
            Assert.DoesNotContain("{{", rendered.ToJsonString());
            var name = mode.Id + ".json";
            index[mode.Id] = name;
            var path = Path.Combine(folder, name);
            if (update)
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(path, rendered.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
            }
            Assert.True(File.Exists(path), "Regenerate the served-mode build probe fixtures.");
            Assert.True(JsonNode.DeepEquals(rendered, JsonNode.Parse(File.ReadAllText(path))), $"Stale GPU probe workflow: {mode.Id}");
        }
        var indexPath = Path.Combine(folder, "index.json");
        if (update) File.WriteAllText(indexPath, index.ToJsonString() + "\n");
        Assert.True(JsonNode.DeepEquals(index, JsonNode.Parse(File.ReadAllText(indexPath))), "GPU probe mode index differs from WanModes.All.");
    }

    [Fact]
    public void Placeholders_keep_types_and_unknown_ones_are_rejected()
    {
        var template = JsonNode.Parse("""{"1": {"inputs": {"text": "{{positive_prompt}}", "seed": "{{seed}}", "label": "a-{{width}}x{{height}}"}}}""")!.AsObject();
        var rendered = WorkflowRenderer.Render(template, new Dictionary<string, object?> { ["positive_prompt"] = "p", ["seed"] = 7L, ["width"] = 8, ["height"] = 4 });
        Assert.Equal(7, rendered["1"]!["inputs"]!["seed"]!.GetValue<long>());
        Assert.Equal("a-8x4", rendered["1"]!["inputs"]!["label"]!.GetValue<string>());
        Assert.Throws<ComfyUiException>(() => WorkflowRenderer.Render(JsonNode.Parse("""{"x": "{{nope}}"}""")!.AsObject(), new Dictionary<string, object?>()));
    }

    [Fact]
    public void Videos_are_found_across_output_kinds()
    {
        var entry = JsonNode.Parse("""{"outputs": {"1": {"gifs": [{"filename": "a.webm"}]}, "2": {"images": [{"filename": "b.png"}]}}}""")!;
        Assert.Equal(["a.webm"], WorkflowRenderer.FindVideos(entry).Select(v => v.Filename));
    }

    [Fact]
    public void Stages_map_wan_nodes()
    {
        var workflow = Load("wan22_i2v.api.json");
        Assert.Equal("load_models", Stages.Of(workflow, "37"));
        Assert.Equal("sample_high_noise", Stages.Of(workflow, "57"));
        Assert.Equal("sample_low_noise", Stages.Of(workflow, "58"));
        Assert.Equal("vae_decode", Stages.Of(workflow, "8"));
        Assert.Equal("save_video", Stages.Of(workflow, "47"));
    }

    [Fact]
    public void Smb_share_root_and_connect()
    {
        Assert.Equal(@"\\UNAS-Pro-8\ComfyUI", SmbShare.Root(@"\\UNAS-Pro-8\ComfyUI\output"));
        Assert.Throws<ArgumentException>(() => SmbShare.Root(@"C:\x"));
        var calls = new List<(string, string, string)>();
        Assert.Equal(@"\\nas\c", SmbShare.Connect(@"\\nas\c", "u", "p", (r, u, p) => { calls.Add((r, u, p)); return 0; }));
        Assert.Single(calls);
        SmbShare.Connect(@"\\nas\c", "u", "p", (_, _, _) => 1219); // already connected is fine
        Assert.Throws<IOException>(() => SmbShare.Connect(@"\\nas\c", "u", "bad", (_, _, _) => 1326));
    }
}
