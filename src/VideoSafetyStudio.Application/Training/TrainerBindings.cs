using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Application.Operations;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Domain.Training;

namespace VideoSafetyStudio.Application.Training;

/// <summary>
/// The hosted trainer registry: only trainer images the pipeline promoted into a region (<c>trainers/{name}/{artifact}.json</c> in its private
/// release container) exist. Each binding is validated against the cached scope; an unreadable region keeps what it last proved.
/// </summary>
public sealed partial class TrainerBindings(StudioFoundation foundation, IStudioReleaseReader reader, ILogger<TrainerBindings> logger) : ITrainerRegistry
{
    public const string Prefix = "trainers/";

    [GeneratedRegex(@"^trainers/(?<name>[a-z0-9][a-z0-9-]{1,40})/(?<artifact>[a-f0-9]{64})\.json\z")]
    private static partial Regex BindingPath();

    private volatile IReadOnlyDictionary<string, IReadOnlyDictionary<string, TrainerBinding>> current =
        new Dictionary<string, IReadOnlyDictionary<string, TrainerBinding>>();

    public async Task RefreshAsync(CancellationToken ct)
    {
        var previous = current;
        var next = new Dictionary<string, IReadOnlyDictionary<string, TrainerBinding>>(StringComparer.Ordinal);
        foreach (var region in foundation.Regions.Keys)
        {
            try
            {
                next[region] = await LoadAsync(region, ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(error, "Trainer bindings for {Region} could not be read; the last verified set stays in effect.", region);
                if (previous.TryGetValue(region, out var known)) next[region] = known;
            }
        }

        current = next;
    }

    private async Task<IReadOnlyDictionary<string, TrainerBinding>> LoadAsync(string region, CancellationToken ct)
    {
        var best = new Dictionary<string, TrainerBinding>(StringComparer.Ordinal);
        foreach (var path in (await reader.ListAsync(region, Prefix, ct)).Order(StringComparer.Ordinal))
        {
            if (BindingPath().Match(path) is not { Success: true } match) continue;
            try
            {
                var binding = StudioContracts.ReadTrainer(await reader.ReadAsync(region, path, ct) ?? throw new ValidationException("Binding disappeared."), foundation);
                if (binding.Region != region || binding.Name != match.Groups["name"].Value || binding.ArtifactId != match.Groups["artifact"].Value)
                {
                    throw new ValidationException("Binding does not match its path or region.");
                }

                if (!best.TryGetValue(binding.Name, out var held) || (binding.VerifiedAt, binding.ArtifactId).CompareTo((held.VerifiedAt, held.ArtifactId)) > 0)
                {
                    best[binding.Name] = binding;
                }
            }
            catch (ValidationException error)
            {
                logger.LogWarning("Ignoring invalid trainer binding {Path} in {Region}: {Reason}", path, region, error.Message);
            }
        }

        return best;
    }

    /// <summary>The binding for the exact promoted build, or null when this region does not have it.</summary>
    public TrainerBinding? Binding(string region, string name, string artifactId) =>
        current.TryGetValue(region, out var bindings) && bindings.TryGetValue(name, out var binding) && binding.ArtifactId == artifactId ? binding : null;

    public IReadOnlyList<TrainerManifest> All() => [.. Preferred().Select(Manifest).OrderBy(item => item.Name, StringComparer.Ordinal)];

    public TrainerManifest Get(string name) =>
        Preferred().Where(binding => binding.Name == name).Select(Manifest).FirstOrDefault() ?? throw new ValidationException($"Unknown trainer '{name}'.");

    // The control region's build defines a trainer; other regions are used only when they hold that same build.
    private IEnumerable<TrainerBinding> Preferred()
    {
        var found = new Dictionary<string, TrainerBinding>(StringComparer.Ordinal);
        foreach (var region in foundation.Regions.Keys.OrderBy(key => key == foundation.ControlRegion ? 0 : 1).ThenBy(key => key, StringComparer.Ordinal))
        {
            if (!current.TryGetValue(region, out var bindings)) continue;
            foreach (var (name, binding) in bindings) found.TryAdd(name, binding);
        }

        return found.Values;
    }

    private static TrainerManifest Manifest(TrainerBinding binding) => new(
        binding.Name, binding.Version, binding.Image, binding.LicenseCode, binding.LicenseWeights, binding.Presets, binding.DefaultPreset,
        binding.Module, binding.ArtifactId);
}
