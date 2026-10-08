using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VideoSafetyStudio.Application.Abstractions;

namespace VideoSafetyStudio.Application.Generation;

/// <summary>Replaces "{{name}}" string values in an API-format workflow. A value that is exactly one placeholder keeps its type.</summary>
public static partial class WorkflowRenderer
{
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>
    {
        "positive_prompt", "negative_prompt", "input_image", "seed", "width", "height", "length", "fps", "filename_prefix",
        "unet_high", "unet_low", "vae_name", "clip_name",
    };

    [GeneratedRegex(@"\{\{([a-z_]+)\}\}")]
    private static partial Regex Placeholder();

    public static JsonObject Render(JsonObject template, IReadOnlyDictionary<string, object?> values)
    {
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        var rendered = (JsonObject)Walk(template, values, unknown)!;
        return unknown.Count == 0
            ? rendered
            : throw new ComfyUiException($"Workflow uses unknown placeholders: [{string.Join(", ", unknown)}]. Supported: [{string.Join(", ", Supported.Order())}].");
    }

    private static JsonNode? Walk(JsonNode? node, IReadOnlyDictionary<string, object?> values, ISet<string> unknown) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(pair => KeyValuePair.Create(pair.Key, Walk(pair.Value, values, unknown)))),
        JsonArray array => new JsonArray([.. array.Select(item => Walk(item, values, unknown))]),
        JsonValue value when value.TryGetValue<string>(out var text) => Substitute(text, values, unknown),
        null => null,
        _ => node.DeepClone(),
    };

    private static JsonNode? Substitute(string text, IReadOnlyDictionary<string, object?> values, ISet<string> unknown)
    {
        var whole = Placeholder().Match(text);
        if (whole.Success && whole.Length == text.Length)
        {
            if (values.TryGetValue(whole.Groups[1].Value, out var value))
            {
                return ToNode(value);
            }

            unknown.Add(whole.Groups[1].Value);
            return JsonValue.Create(text);
        }

        return JsonValue.Create(Placeholder().Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var v)
            ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
            : m.Value));
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        float f => JsonValue.Create(f),
        bool b => JsonValue.Create(b),
        _ => JsonValue.Create(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)),
    };

    /// <summary>Every output file reference that looks like a video, whichever node type produced it.</summary>
    public static IReadOnlyList<Abstractions.OutputRef> FindVideos(JsonNode historyEntry)
    {
        string[] extensions = [".mp4", ".webm", ".mov", ".mkv"];
        var found = new List<Abstractions.OutputRef>();
        if (historyEntry["outputs"] is JsonObject outputs)
        {
            foreach (var node in outputs.Select(p => p.Value).OfType<JsonObject>())
            {
                foreach (var items in node.Select(p => p.Value).OfType<JsonArray>())
                {
                    foreach (var item in items.OfType<JsonObject>())
                    {
                        var name = item["filename"]?.GetValue<string>();
                        if (name is not null && extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                        {
                            found.Add(new Abstractions.OutputRef(name, item["subfolder"]?.GetValue<string>() ?? "", item["type"]?.GetValue<string>() ?? "output"));
                        }
                    }
                }
            }
        }

        return found;
    }
}
