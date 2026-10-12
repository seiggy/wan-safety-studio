using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Application.Generation;
using VideoSafetyStudio.Application.Training;
using VideoSafetyStudio.Application.Vision;
using VideoSafetyStudio.Infrastructure.AzureMl;
using VideoSafetyStudio.Infrastructure.Compute;
using VideoSafetyStudio.Infrastructure.ComfyUi;
using VideoSafetyStudio.Infrastructure.Events;
using VideoSafetyStudio.Infrastructure.Media;
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
            var runtime = sp.GetRequiredService<AzureMlRuntime>();
            storage = new StorageOptions { Backend = "azure", Account = runtime.Foundation.StorageAccountName, Container = runtime.Foundation.ContainerName, CreateContainer = false };
            return new StoreSelection(ObjectStoreFactory.Create(storage, contentRoot, runtime.Credential));
        });
        services.AddSingleton(sp => sp.GetRequiredService<StoreSelection>().Store);
        services.TryAddSingleton<TokenCredential>(sp => StudioCredential.Create(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp => AzureMlRuntime.Load(sp.GetRequiredService<IOptions<AzureMlOptions>>().Value, sp.GetRequiredService<TokenCredential>()));
        services.AddSingleton<AzureMlGate>();
        services.AddSingleton<JobRecords>();
        services.AddSingleton<IGenerationBackend>(sp => UsesAzureMl(sp)
            ? new AzureMlBackend(sp.GetRequiredService<IObjectStore>(), sp.GetRequiredService<IAzureMlClient>(), sp.GetRequiredService<AzureMlRuntime>().Foundation,
                sp.GetRequiredService<AzureMlRuntime>().Release, sp.GetRequiredService<AzureMlGate>(), sp.GetRequiredService<IWorkflowSource>(),
                sp.GetRequiredService<IGenerationTelemetry>(), sp.GetRequiredService<JobRecords>(), sp.GetRequiredService<IOptions<GenerationOptions>>(),
                sp.GetRequiredService<IOptions<AzureMlOptions>>(), sp.GetRequiredService<TimeProvider>())
            : ActivatorUtilities.CreateInstance<ComfyUiBackend>(sp));
        // Not from the HTTP client factory: its resilience handlers would retry the (billable) job creation.
        services.AddSingleton<IAzureMlClient>(sp => new ArmAzureMlClient(new HttpClient { Timeout = TimeSpan.FromMinutes(2) },
            sp.GetRequiredService<AzureMlRuntime>().Credential, sp.GetRequiredService<AzureMlRuntime>().Foundation));

        services.AddSingleton<IMediaTools, FfmpegMediaTools>();
        services.AddSingleton<IComputeTarget, DockerComputeTarget>();
        services.AddSingleton<ITrainerRegistry, FileTrainerRegistry>();
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
