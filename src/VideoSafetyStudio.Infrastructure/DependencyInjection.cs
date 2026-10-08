using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Application.Generation;
using VideoSafetyStudio.Application.Training;
using VideoSafetyStudio.Application.Vision;
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

        services.AddSingleton<StoreSelection>(sp => new StoreSelection(ObjectStoreFactory.Create(
            sp.GetRequiredService<IOptions<StorageOptions>>().Value, Path.Combine(contentRoot, ".dev-data", "blob"))));
        services.AddSingleton(sp => sp.GetRequiredService<StoreSelection>().Store);

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
    }
}

/// <summary>The chosen store and a human-readable label for the Environment view.</summary>
public sealed record StoreSelection((IObjectStore Store, string Label) Selected)
{
    public IObjectStore Store => Selected.Store;

    public string Label => Selected.Label;
}
