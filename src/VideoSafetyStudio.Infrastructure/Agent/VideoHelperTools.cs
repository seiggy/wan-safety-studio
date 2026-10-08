using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace VideoSafetyStudio.Infrastructure.Agent;

/// <summary>
/// The controls the helper may operate on the Create page. The browser performs each action when it sees the tool call;
/// here the handlers only validate the arguments so the model learns immediately when it asks for something the form
/// cannot do. Limits mirror the form (web/src/features/create).
/// </summary>
public static class VideoHelperTools
{
    public const int MaxScenes = 5;
    public static readonly string[] AspectRatios = ["16:9", "4:3", "1:1"];
    public static readonly int[] Takes = [1, 3, 5];
    public static readonly string[] Modes = ["draft", "fast", "turbo", "prime"];

    public static IList<AIFunction> Create() =>
    [
        AIFunctionFactory.Create(SetScenePrompt, "set_scene_prompt"),
        AIFunctionFactory.Create(SetNegativePrompt, "set_negative_prompt"),
        AIFunctionFactory.Create(AddScene, "add_scene"),
        AIFunctionFactory.Create(RemoveScene, "remove_scene"),
        AIFunctionFactory.Create(SetAspectRatio, "set_aspect_ratio"),
        AIFunctionFactory.Create(SetDuration, "set_duration"),
        AIFunctionFactory.Create(SetTakes, "set_takes"),
        AIFunctionFactory.Create(SetQuality, "set_quality"),
    ];

    [Description("Replace the positive prompt of an existing scene (1-based). Use for rewrites and expansions the user asked for.")]
    private static string SetScenePrompt([Description("1-based scene number")] int scene, [Description("The full new positive prompt")] string prompt) =>
        scene is < 1 or > MaxScenes ? "Rejected: no such scene." : string.IsNullOrWhiteSpace(prompt) ? "Rejected: the prompt is empty." : Applied();

    [Description("Replace the negative prompt. It applies to every scene. Pass an empty string to clear it.")]
    private static string SetNegativePrompt([Description("The full new negative prompt")] string prompt) => Applied();

    [Description("Add a new scene (at most 5). Its reference image is chosen by the user, not by you.")]
    private static string AddScene([Description("Optional positive prompt for the new scene")] string? prompt = null) => Applied();

    [Description("Remove a scene (1-based). Scene 1 cannot be removed.")]
    private static string RemoveScene([Description("1-based scene number, 2 or higher")] int scene) =>
        scene < 2 ? "Rejected: scene 1 cannot be removed." : Applied();

    [Description("Set the aspect ratio: 16:9, 4:3 or 1:1.")]
    private static string SetAspectRatio([Description("16:9, 4:3 or 1:1")] string value) =>
        AspectRatios.Contains(value) ? Applied() : $"Rejected: choose one of {string.Join(", ", AspectRatios)}.";

    [Description("Set the clip duration in whole seconds, 5 to 10. Longer clips take longer and can drift; 5 is the tuned value.")]
    private static string SetDuration([Description("Seconds, 5 to 10")] int seconds) =>
        seconds is >= 5 and <= 10 ? Applied() : "Rejected: duration must be 5 to 10 seconds.";

    [Description("Set how many takes (videos, each with its own random seed) to generate per scene: 1, 3 or 5.")]
    private static string SetTakes([Description("1, 3 or 5")] int count) =>
        Takes.Contains(count) ? Applied() : "Rejected: takes must be 1, 3 or 5.";

    [Description("Set the quality mode: draft, fast, turbo or prime. Ask first unless the user requested it; prime is slowest.")]
    private static string SetQuality([Description("draft, fast, turbo or prime")] string mode) =>
        Modes.Contains(mode) ? Applied() : $"Rejected: choose one of {string.Join(", ", Modes)}.";

    private static string Applied() => "Applied in the form.";
}
