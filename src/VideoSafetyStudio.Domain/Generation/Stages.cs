using System.Text.Json.Nodes;

namespace VideoSafetyStudio.Domain.Generation;

/// <summary>Maps ComfyUI nodes of the Wan workflow to benchmark stages.</summary>
public static class Stages
{
    private static readonly HashSet<string> Loaders =
        ["UNETLoader", "UnetLoaderGGUF", "CLIPLoader", "VAELoader", "CheckpointLoaderSimple", "LoraLoader", "LoraLoaderModelOnly"];

    private static readonly HashSet<string> Savers =
        ["SaveWEBM", "SaveVideo", "CreateVideo", "SaveAnimatedWEBP", "VHS_VideoCombine", "SaveImage"];

    public static string Of(JsonObject workflow, string nodeId)
    {
        var node = workflow[nodeId] as JsonObject;
        var kind = node?["class_type"]?.GetValue<string>() ?? string.Empty;
        if (Loaders.Contains(kind))
        {
            return "load_models";
        }

        if (kind == "CLIPTextEncode")
        {
            return "text_encode";
        }

        if (kind is "LoadImage" or "WanImageToVideo" or "Wan22ImageToVideoLatent")
        {
            return "image_to_latent";
        }

        if (kind == "ModelSamplingSD3")
        {
            return "model_setup";
        }

        if (kind == "KSampler")
        {
            return "sample"; // single-pass models (Wan 2.2 TI2V-5B) have no high/low-noise experts
        }

        if (kind.StartsWith("KSampler", StringComparison.Ordinal))
        {
            // Wan 2.2 A14B: the first sampler adds noise (high-noise expert); the second finishes with the low-noise expert.
            var addNoise = node?["inputs"]?["add_noise"]?.GetValue<string>();
            return addNoise == "disable" ? "sample_low_noise" : "sample_high_noise";
        }

        if (kind == "VAEDecode")
        {
            return "vae_decode";
        }

        return Savers.Contains(kind) ? "save_video" : "other";
    }
}
