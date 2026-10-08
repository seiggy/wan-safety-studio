namespace VideoSafetyStudio.Application.Generation;

/// <summary>The values a workflow template is rendered with.</summary>
public static class WorkflowValues
{
    /// <summary>Wan latent length: 4n+1 frames.</summary>
    public static int Length(double seconds, int fps) => (int)Math.Round(seconds * fps / 4) * 4 + 1;

    public static long NewSeed() => System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MaxValue) * 1000L + System.Security.Cryptography.RandomNumberGenerator.GetInt32(1000);

    public static Dictionary<string, object?> Build(GenerationOptions o, WanMode mode, string prompt, string negative, string image, long seed, int width, int height, int length, string prefix) => new()
    {
        ["steps"] = mode.Steps, ["split_step"] = mode.SplitStep, ["cfg"] = mode.Cfg, ["shift"] = mode.Shift, ["sampler"] = mode.Sampler,
        ["lora_high"] = o.LoraHigh, ["lora_low"] = o.LoraLow, ["unet_5b"] = o.Unet5B, ["vae_5b"] = o.Vae5B,
        ["unet_high"] = o.UnetHigh, ["unet_low"] = o.UnetLow, ["vae_name"] = o.Vae, ["clip_name"] = o.Clip,
        ["positive_prompt"] = prompt, ["negative_prompt"] = negative, ["input_image"] = image, ["seed"] = seed,
        ["width"] = width, ["height"] = height, ["length"] = length, ["fps"] = mode.Fps, ["filename_prefix"] = prefix,
    };
}
