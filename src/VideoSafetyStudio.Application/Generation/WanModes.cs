namespace VideoSafetyStudio.Application.Generation;

/// <summary>A speed/quality preset: which workflow template to render and its sampler settings.</summary>
public sealed record WanMode(
    string Id,
    string Label,
    string Description,
    string Template,
    int Steps,
    int SplitStep,
    double Cfg,
    double Shift,
    string Sampler,
    int Fps,
    string Model);

/// <summary>
/// Settings follow the Comfy-Org templates: video_wan2_2_14B_i2v (20 steps, split 10, CFG 3.5, shift 5, euler; the lightx2v
/// 4-step LoRAs run 4 steps, split 2, CFG 1) and video_wan2_2_5B_ti2v (single KSampler, 20 steps, CFG 5, shift 8, uni_pc, 24 fps).
/// </summary>
public static class WanModes
{
    public const string DefaultId = "prime";

    public static IReadOnlyList<WanMode> All { get; } =
    [
        new("draft", "Draft", "14B + lightx2v LoRA, 4 steps (2 high-noise + 2 low-noise). Quick look at composition and motion.",
            "wan22_i2v_lightning.api.json", 4, 2, 1.0, 5.0, "euler", 16, "Wan 2.2 14B + lightx2v 4-step LoRA"),
        new("fast", "Fast", "14B + lightx2v LoRA, 7 steps (3 high-noise + 4 low-noise). More detail than Draft at about twice the cost.",
            "wan22_i2v_lightning.api.json", 7, 3, 1.0, 5.0, "euler", 16, "Wan 2.2 14B + lightx2v 4-step LoRA"),
        new("turbo", "Turbo", "Wan 2.2 TI2V-5B, one sampler at 20 steps, 24 fps. Much smaller model than the 14B.",
            "wan22_ti2v_5b.api.json", 20, 20, 5.0, 8.0, "uni_pc", 24, "Wan 2.2 TI2V-5B"),
        new("prime", "Prime", "Base 14B at 20 steps (10 high-noise + 10 low-noise), the recommended quality setting.",
            "wan22_i2v.api.json", 20, 10, 3.5, 5.0, "euler", 16, "Wan 2.2 14B"),
    ];

    public static WanMode Get(string? id) =>
        All.FirstOrDefault(mode => mode.Id == (string.IsNullOrWhiteSpace(id) ? DefaultId : id))
        ?? throw new VideoSafetyStudio.Domain.ValidationException($"Mode must be one of: {string.Join(", ", All.Select(m => m.Id))}.");
}
