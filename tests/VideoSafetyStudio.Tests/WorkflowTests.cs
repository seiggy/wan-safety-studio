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
    public void Placeholders_keep_types_and_unknown_ones_are_rejected()
    {
        var template = JsonNode.Parse("""{"1": {"inputs": {"text": "{{positive_prompt}}", "seed": "{{seed}}", "label": "a-{{width}}x{{height}}"}}}""")!.AsObject();
        var rendered = WorkflowRenderer.Render(template, new Dictionary<string, object?> { ["positive_prompt"] = "p", ["seed"] = 7L, ["width"] = 8, ["height"] = 4 });
        Assert.Equal(7, rendered["1"]!["inputs"]!["seed"]!.GetValue<long>());
        Assert.Equal("a-8x4", rendered["1"]!["inputs"]!["label"]!.GetValue<string>());
        Assert.Throws<ComfyUiException>(() => WorkflowRenderer.Render(JsonNode.Parse("""{"x": "{{nope}}"}""")!.AsObject(), new Dictionary<string, object?>()));
    }

    [Fact]
    public void Shipped_template_renders_to_the_importable_reference_graph()
    {
        var reference = Load("wan22_i2v_fp8.api.json");
        var defaults = new GenerationOptions();
        var values = new Dictionary<string, object?>
        {
            ["positive_prompt"] = reference["6"]!["inputs"]!["text"]!.GetValue<string>(),
            ["negative_prompt"] = reference["7"]!["inputs"]!["text"]!.GetValue<string>(),
            ["input_image"] = reference["52"]!["inputs"]!["image"]!.GetValue<string>(),
            ["seed"] = reference["57"]!["inputs"]!["noise_seed"]!.GetValue<long>(),
            ["width"] = reference["50"]!["inputs"]!["width"]!.GetValue<int>(),
            ["height"] = reference["50"]!["inputs"]!["height"]!.GetValue<int>(),
            ["length"] = reference["50"]!["inputs"]!["length"]!.GetValue<int>(),
            ["fps"] = reference["47"]!["inputs"]!["fps"]!.GetValue<double>(),
            ["filename_prefix"] = reference["47"]!["inputs"]!["filename_prefix"]!.GetValue<string>(),
            ["unet_high"] = defaults.UnetHigh,
            ["unet_low"] = defaults.UnetLow,
            ["vae_name"] = defaults.Vae,
            ["clip_name"] = defaults.Clip,
        };
        var rendered = WorkflowRenderer.Render(Load("wan22_i2v.api.json"), values);
        Assert.True(JsonNode.DeepEquals(reference, rendered));
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
        var workflow = Load("wan22_i2v_fp8.api.json");
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
