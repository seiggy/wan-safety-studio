using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Application.Generation;
using VideoSafetyStudio.Application.Operations;
using VideoSafetyStudio.Application.Training;
using VideoSafetyStudio.Application.Vision;
using VideoSafetyStudio.Infrastructure.AzureMl;
using VideoSafetyStudio.Infrastructure.Compute;
using VideoSafetyStudio.Infrastructure.ComfyUi;
using VideoSafetyStudio.Infrastructure.Events;
using VideoSafetyStudio.Infrastructure.Media;
using VideoSafetyStudio.Infrastructure.Operations;
using VideoSafetyStudio.Infrastructure.Storage;
using VideoSafetyStudio.Infrastructure.Telemetry;
using VideoSafetyStudio.Infrastructure.Trainers;

namespace VideoSafetyStudio.Infrastructure;

#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental
public static class DependencyInjection
{
    /// <summary>Registers the application services and their local-development adapters.</summary>
    public static IServiceCollection AddStudio(this IServiceCollection services, IConfiguration configuration, string contentRoot)
    {
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.Section));
        services.Configure<ComfyUiOptions>(configuration.GetSection(ComfyUiOptions.Section));
        services.Configure<GenerationOptions>(configuration.GetSection(GenerationOptions.Section));
        services.Configure<AzureMlOptions>(configuration.GetSection(AzureMlOptions.Section));
        services.Configure<TrainingOptions>(configuration.GetSection(TrainingOptions.Section));
        services.Configure<TrainerOptions>(configuration.GetSection(TrainerOptions.Section));
        services.PostConfigure<StorageOptions>(options => options.Connection ??= configuration.GetConnectionString("blobs"));

        services.PostConfigure<TrainerOptions>(options =>
        {
            options.BuiltIn = Resolve(options.BuiltIn);
            options.Directories = [.. options.Directories.Select(d => Resolve(d)!)];
        });

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<EventHub>();
        services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<EventHub>());

        services.AddSingleton<StoreSelection>(sp =>
        {
            var storage = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            if (!UsesAzureMl(sp))
            {
                return new StoreSelection(ObjectStoreFactory.Create(storage, Path.Combine(contentRoot, ".dev-data", "blob")));
            }

            // Azure ML reads and writes the studio datastore container, so that is the store (the identity may hold data access only).
            if (sp.GetRequiredService<StudioOperations>().Foundation is { } topology)
            {
                var control = topology.Regions[topology.ControlRegion];
                var account = new StorageOptions { Backend = "azure", Account = StudioResourceNames.NameOf(control.StorageAccountId), Container = control.Container, CreateContainer = false };
                return new StoreSelection(ObjectStoreFactory.Create(account, contentRoot, sp.GetRequiredService<TokenCredential>()));
            }

            var runtime = sp.GetRequiredService<AzureMlRuntime>();
            storage = new StorageOptions { Backend = "azure", Account = runtime.Foundation.StorageAccountName, Container = runtime.Foundation.ContainerName, CreateContainer = false };
            return new StoreSelection(ObjectStoreFactory.Create(storage, contentRoot, runtime.Credential));
        });
        // Hosted multi-region: library reads span the regional run-data stores; every other key stays in the control store.
        services.AddSingleton<IObjectStore>(sp => sp.GetRequiredService<HostedGenerationHolder>().Value?.Library ?? sp.GetRequiredService<StoreSelection>().Store);
        services.AddSingleton(sp => UsesAzureMl(sp) ? HostedGenerationFactory.Create(sp) : new HostedGenerationHolder(null));
        services.TryAddSingleton<TokenCredential>(sp => StudioCredential.Create(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp => AzureMlRuntime.Load(sp.GetRequiredService<IOptions<AzureMlOptions>>().Value, sp.GetRequiredService<TokenCredential>()));
        services.AddSingleton<AzureMlGate>(sp =>
        {
            // The single-workspace (flat AzureMl) mode only; a configured Studio topology uses the regional backend and the durable fence instead.
            return new AzureMlGate(sp.GetRequiredService<IOptionsMonitor<AzureMlOptions>>());
        });
        // The studio topology: explicit regional resource IDs from configuration (App Service settings). The host does not validate at start: /health-ui reports problems and the server refuses work until they are fixed.
        services.AddOptions<StudioOptions>().Bind(configuration.GetSection(StudioOptions.Section));
        services.AddSingleton(sp => new StudioArmedGate(sp.GetRequiredService<TimeProvider>(), TimeSpan.FromSeconds(30)));
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<StudioOptions>>().Value;
            if (!options.Configured)
            {
                return new StudioOperations(null, null);
            }

            var foundation = StudioOptionsMapper.ToFoundation(options);
            var credential = sp.GetRequiredService<TokenCredential>();
            var store = new BlobStudioStore(foundation, credential);
            var gate = sp.GetRequiredService<StudioArmedGate>();
            gate.Bind(foundation);
            // Not from the HTTP client factory: no automatic retries for billable ARM writes.
            return new StudioOperations(new StudioLifecycle(foundation, new ArmStudioClient(new HttpClient { Timeout = TimeSpan.FromMinutes(2) }, credential, foundation.Scope.SubscriptionId),
                store, store, new RetailPriceClient(new HttpClient { Timeout = TimeSpan.FromSeconds(90) }), new PrivateNetworkProbe(), gate,
                sp.GetRequiredService<TimeProvider>(), StudioWaits.Default, sp.GetRequiredService<ILogger<StudioLifecycle>>()), foundation, store);
        });
        services.AddSingleton<JobRecords>();
        services.AddSingleton<IGenerationBackend>(sp => UsesAzureMl(sp) && sp.GetRequiredService<HostedGenerationHolder>().Value is { } hosted
            ? hosted.CreateBackend(sp)
            : UsesAzureMl(sp)
            ? new AzureMlBackend(sp.GetRequiredService<IObjectStore>(), sp.GetRequiredService<IAzureMlClient>(), sp.GetRequiredService<AzureMlRuntime>().Foundation,
                sp.GetRequiredService<AzureMlRuntime>().Release, sp.GetRequiredService<AzureMlGate>(), sp.GetRequiredService<IWorkflowSource>(),
                sp.GetRequiredService<IGenerationTelemetry>(), sp.GetRequiredService<JobRecords>(), sp.GetRequiredService<IOptions<GenerationOptions>>(),
                sp.GetRequiredService<IOptions<AzureMlOptions>>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<AzureMlBackend>>())
            : ActivatorUtilities.CreateInstance<ComfyUiBackend>(sp));
        // Not from the HTTP client factory: its resilience handlers would retry the (billable) job creation.
        services.AddSingleton<IAzureMlClient>(sp => new ArmAzureMlClient(new HttpClient { Timeout = TimeSpan.FromMinutes(2) },
            sp.GetRequiredService<AzureMlRuntime>().Credential, sp.GetRequiredService<AzureMlRuntime>().Foundation));

        services.AddSingleton<IMediaTools, FfmpegMediaTools>();
        // Hosted: the promoted trainer images run as Azure ML jobs under the shared admission limits. Otherwise the local Docker mode and its folders.
        services.AddSingleton<IComputeTarget>(sp => sp.GetRequiredService<HostedGenerationHolder>().Value is { } hosted ? hosted.CreateTraining(sp) : ActivatorUtilities.CreateInstance<DockerComputeTarget>(sp));
        services.AddSingleton<ITrainerRegistry>(sp => sp.GetRequiredService<HostedGenerationHolder>().Value is { } hosted ? hosted.Trainers : ActivatorUtilities.CreateInstance<FileTrainerRegistry>(sp));
        services.AddSingleton<IGenerationTelemetry, GenerationTelemetry>();

        // Long uploads, downloads and generations: the default 30 s resilience timeouts from ServiceDefaults must not apply here.
        services.AddHttpClient<IComfyUiClient, ComfyUiClient>().RemoveAllResilienceHandlers().ConfigureHttpClient(client => client.Timeout = TimeSpan.FromMinutes(30));
        services.AddSingleton<IVideoRetrieval>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ComfyUiOptions>>().Value;
            if (!string.Equals(options.Retrieval, "share", StringComparison.OrdinalIgnoreCase))
            {
                return new ApiVideoRetrieval();
            }

            var output = options.OutputDir ?? throw new InvalidOperationException("ComfyUi:Retrieval=share needs ComfyUi:OutputDir (ComfyUI's output folder as mounted here).");
            if (!string.IsNullOrEmpty(options.SmbUser) && output.StartsWith(@"\\", StringComparison.Ordinal))
            {
                SmbShare.Connect(output, options.SmbUser, options.SmbPassword ?? string.Empty);
            }

            return new ShareVideoRetrieval(output);
        });
        services.AddSingleton<IWorkflowSource>(sp =>
            new FileWorkflowSource(sp.GetRequiredService<IOptions<ComfyUiOptions>>().Value.WorkflowsDir
                                   ?? Path.Combine(AppContext.BaseDirectory, "Workflows")));

        services.AddSingleton<VisionService>();
        services.AddSingleton<TrainingService>();
        services.AddSingleton<GenerationService>();
        services.AddSingleton<ArchiveService>();
        return services;

        string? Resolve(string? path) => path is null || Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(contentRoot, path));

        // Read when first resolved so test and host configuration overrides apply.
        static bool UsesAzureMl(IServiceProvider sp) => ProviderOf(sp.GetRequiredService<IConfiguration>()) == "azureml";
    }

    /// <summary>Generation:Provider: azureml (default) or comfyui.</summary>
    public static string ProviderOf(IConfiguration configuration) => (configuration["Generation:Provider"] ?? "azureml").Trim().ToLowerInvariant() switch
    {
        "azureml" => "azureml",
        "comfyui" => "comfyui",
        _ => throw new InvalidOperationException("Generation:Provider must be azureml or comfyui."),
    };
}

/// <summary>The chosen store and a human-readable label for the Environment view.</summary>
public sealed record StoreSelection((IObjectStore Store, string Label) Selected)
{
    public IObjectStore Store => Selected.Store;

    public string Label => Selected.Label;
}
