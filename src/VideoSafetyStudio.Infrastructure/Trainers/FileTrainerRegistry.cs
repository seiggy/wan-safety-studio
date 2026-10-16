using System.Text.Json;
using Microsoft.Extensions.Options;
using VideoSafetyStudio.Application;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Domain.Training;

namespace VideoSafetyStudio.Infrastructure.Trainers;

public sealed class TrainerOptions
{
    public const string Section = "Trainers";

    /// <summary>Extra folders holding &lt;name&gt;/trainer.json (e.g. customer trainers). The built-in folder is added automatically.</summary>
    public List<string> Directories { get; set; } = [];

    public string? BuiltIn { get; set; }
}

/// <summary>Discovers trainer.json manifests. Folders starting with "_" are templates and skipped.</summary>
public sealed partial class FileTrainerRegistry(IOptions<TrainerOptions> options) : ITrainerRegistry
{
    public IReadOnlyList<TrainerManifest> All()
    {
        var roots = options.Value.Directories.Concat(options.Value.BuiltIn is { } builtIn ? [builtIn] : []);
        var found = new Dictionary<string, TrainerManifest>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var path in Directory.EnumerateFiles(root, "trainer.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                if (new DirectoryInfo(Path.GetDirectoryName(path)!).Name.StartsWith('_'))
                {
                    continue;
                }

                var manifest = Load(path);
                found[manifest.Name] = manifest;
            }
        }

        return [.. found.Values];
    }

    public TrainerManifest Get(string name) =>
        All().FirstOrDefault(m => m.Name == name) ?? throw new ValidationException($"Unknown trainer '{name}'.");

    private static TrainerManifest Load(string path)
    {
        var manifest = Json.From<TrainerManifest>(File.ReadAllBytes(path));
        return manifest is { Name.Length: > 0, Presets.Count: > 0 } && manifest.Presets.Contains(manifest.DefaultPreset)
               && System.Text.RegularExpressions.Regex.IsMatch(manifest.Name, @"\A[a-z0-9][a-z0-9-]{1,40}\z")
            ? manifest
            : throw new ValidationException($"{path}: invalid name or presets.");
    }
}
