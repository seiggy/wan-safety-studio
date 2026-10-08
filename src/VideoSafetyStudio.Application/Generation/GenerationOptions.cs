namespace VideoSafetyStudio.Application.Generation;

/// <summary>Model file names on the ComfyUI server. Defaults match the Wan 2.2 fp8 scaled files.</summary>
public sealed class GenerationOptions
{
    public const string Section = "ComfyUi";

    public string UnetHigh { get; set; } = "wan2.2_i2v_high_noise_14B_fp8_scaled.safetensors";
    public string UnetLow { get; set; } = "wan2.2_i2v_low_noise_14B_fp8_scaled.safetensors";
    public string Vae { get; set; } = "wan_2.1_vae.safetensors";
    public string Clip { get; set; } = "umt5_xxl_fp8_e4m3fn_scaled.safetensors";
    public string LoraHigh { get; set; } = "wan2.2_i2v_lightx2v_4steps_lora_v1_high_noise.safetensors";
    public string LoraLow { get; set; } = "wan2.2_i2v_lightx2v_4steps_lora_v1_low_noise.safetensors";
    public string Unet5B { get; set; } = "Wan2_2-TI2V-5B_fp8_e5m2_scaled_KJ.safetensors";
    public string Vae5B { get; set; } = "wan2.2_vae.safetensors";
}

public sealed record SceneImage(string Name, byte[] Data);

public sealed record SubmitRequest(
    IReadOnlyList<string> Prompts,
    string NegativePrompt,
    string AspectRatio,
    double DurationSeconds,
    int Takes,
    IReadOnlyList<SceneImage?> Images,
    string? Mode = null);
