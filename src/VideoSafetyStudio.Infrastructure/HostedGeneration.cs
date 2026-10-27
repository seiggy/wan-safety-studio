using Azure.Core;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Application.Generation;
using VideoSafetyStudio.Application.Operations;
using VideoSafetyStudio.Application.Training;
using VideoSafetyStudio.Infrastructure.AzureMl;
using VideoSafetyStudio.Infrastructure.Operations;
using VideoSafetyStudio.Infrastructure.Storage;
using VideoSafetyStudio.Infrastructure.Telemetry;

namespace VideoSafetyStudio.Infrastructure;

public sealed record HostedGenerationHolder(HostedGeneration? Value);

/// <summary>Everything the hosted regional generation backend shares with the library reader and the refresh loop.</summary>
public sealed record HostedGeneration(
    StudioFoundation Foundation, IReadOnlyDictionary<string, RegionContext> Regions, RegionalLibraryStore Library, AttemptLedger Ledger,
    StudioSettingsSource Settings, RegionalAdmission Admission, RegionalReleaseResolver Releases, TrainerBindings Trainers)
{
    /// <summary>Remote training shares this host's gate, settings and admission slots, so it cannot exceed generation's capacity controls.</summary>
    public AzureMlTrainingCompute CreateTraining(IServiceProvider sp) => new(
        Foundation, Regions, Trainers, sp.GetRequiredService<StudioArmedGate>(), Settings, Admission, sp.GetRequiredService<TimeProvider>(),
        RemoteTrainingTimings.Default, sp.GetRequiredService<ILogger<AzureMlTrainingCompute>>());

    public RegionalAzureMlBackend CreateBackend(IServiceProvider sp) => new(
        Foundation, Regions, Releases, sp.GetRequiredService<StudioArmedGate>(), Settings, Admission, Ledger, Library, sp.GetRequiredService<IWorkflowSource>(),
        sp.GetRequiredService<IGenerationTelemetry>(), sp.GetRequiredService<JobRecords>(), sp.GetRequiredService<IOptions<GenerationOptions>>(),
        sp.GetRequiredService<IOptions<AzureMlOptions>>(), sp.GetRequiredService<TimeProvider>(), RegionalTimings.Default, sp.GetRequiredService<ILogger<RegionalAzureMlBackend>>());
}

public static class HostedGenerationFactory
{
    public static HostedGenerationHolder Create(IServiceProvider sp)
    {
        var operations = sp.GetRequiredService<StudioOperations>();
        if (operations.Foundation is not { } foundation || operations.Store is not { } store) return new HostedGenerationHolder(null);
        var credential = sp.GetRequiredService<TokenCredential>();
        var regions = new Dictionary<string, RegionContext>(StringComparer.Ordinal);
        var stores = new Dictionary<string, IObjectStore>(StringComparer.Ordinal);
        foreach (var (key, region) in foundation.Regions)
        {
            var azure = RegionContext.Derive(foundation, region);
            var objects = new BlobObjectStore(new BlobServiceClient(new Uri($"https://{azure.StorageAccountName}.blob.core.windows.net"), credential), region.Container, createContainer: false);
            // Not from the HTTP client factory: its resilience handlers would retry the (billable) job creation.
            regions[key] = new RegionContext(key, region, azure, new ArmAzureMlClient(new HttpClient { Timeout = TimeSpan.FromMinutes(2) }, credential, azure), objects);
            stores[key] = objects;
        }

        var control = sp.GetRequiredService<StoreSelection>().Store;
        var gate = sp.GetRequiredService<StudioArmedGate>();
        return new HostedGenerationHolder(new HostedGeneration(
            foundation, regions, new RegionalLibraryStore(control, stores, foundation.ControlRegion), new AttemptLedger(control, foundation),
            new StudioSettingsSource(foundation, store, sp.GetRequiredService<ILogger<StudioSettingsSource>>()), new RegionalAdmission(),
            new RegionalReleaseResolver(foundation, store, gate), new TrainerBindings(foundation, store, sp.GetRequiredService<ILogger<TrainerBindings>>())));
    }
}
