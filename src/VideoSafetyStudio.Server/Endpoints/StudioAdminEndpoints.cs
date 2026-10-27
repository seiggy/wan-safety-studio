using VideoSafetyStudio.Application;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Application.Operations;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Infrastructure;
using VideoSafetyStudio.Infrastructure.Operations;
using VideoSafetyStudio.Server.Auth;

namespace VideoSafetyStudio.Server.Endpoints;

public sealed record SelectReleaseRequest(string? ArtifactId);

public sealed record StartRequest(bool ApproveGpuSpend, bool ApproveCustomerNsgRules);

public sealed record UpdateSettingsRequest(long ExpectedVersion, int MaxConcurrentJobs, int QueueMoveMinutes, Dictionary<string, RegionSettings>? Regions);

/// <summary>Operational lifecycle. Thin adapters: every rule lives in <see cref="StudioLifecycle"/>; every route needs the StudioAdmin role.</summary>
public static class StudioAdminEndpoints
{
    public static void MapStudioAdmin(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/studio");
        api.AddEndpointFilter(async (context, next) =>
        {
            if (context.HttpContext.Items[typeof(StudioSession)] is not StudioSession { IsAdmin: true })
            {
                return Results.Json(new { error = "The StudioAdmin role is required." }, statusCode: StatusCodes.Status403Forbidden);
            }

            return context.HttpContext.RequestServices.GetRequiredService<StudioOperations>().Lifecycle is null
                ? Results.Json(new { error = "Studio operations are not configured for this deployment." }, statusCode: StatusCodes.Status503ServiceUnavailable)
                : await next(context);
        });

        api.MapGet("/status", async (StudioOperations operations, CancellationToken ct) => Results.Json(Describe(await operations.Lifecycle!.StatusAsync(ct)), Json.Options));

        api.MapGet("/settings", async (StudioOperations operations, CancellationToken ct) => Results.Json(await operations.Lifecycle!.SettingsAsync(ct), Json.Options));

        api.MapPut("/settings", async (UpdateSettingsRequest request, HttpContext context, StudioOperations operations, HostedGenerationHolder hosted, IEventPublisher events, CancellationToken ct) =>
        {
            var requested = new StudioSettings(1, 0, "", request.MaxConcurrentJobs, request.QueueMoveMinutes, request.Regions!);
            var saved = await operations.Lifecycle!.UpdateSettingsAsync(Actor(context), request.ExpectedVersion, requested, ct);
            // Apply now rather than at the next refresh; a refresh failure leaves the saved document, which the loop re-reads.
            if (hosted.Value is { } value) await value.Settings.RefreshAsync(CancellationToken.None);
            events.Publish("studio", new { version = saved.Version });
            return Results.Json(await operations.Lifecycle.SettingsAsync(ct), Json.Options);
        });

        api.MapPost("/regions/{region}/release", async (string region, SelectReleaseRequest request, HttpContext context, StudioOperations operations, IEventPublisher events, CancellationToken ct) =>
        {
            await operations.Lifecycle!.SelectReleaseAsync(Actor(context), region, request.ArtifactId ?? "", ct);
            events.Publish("studio", new { region });
            return Results.Json(new { selected = true });
        });

        api.MapPost("/regions/{region}/start", async (string region, StartRequest request, HttpContext context, StudioOperations operations, IEventPublisher events, CancellationToken ct) =>
        {
            try
            {
                await operations.Lifecycle!.StartAsync(Actor(context), region, new StartApproval(request.ApproveGpuSpend, request.ApproveCustomerNsgRules), ct);
            }
            finally
            {
                events.Publish("studio", new { region });
            }

            return Results.Json(new { armed = true });
        });

        // Emergency release is not tied to the request lifetime: a disconnected browser must not leave a half-released region.
        api.MapPost("/stop", async (HttpContext context, StudioOperations operations, IEventPublisher events) =>
        {
            var result = await operations.Lifecycle!.StopAsync(Actor(context));
            events.Publish("studio", new { stopped = true });
            return result.Verified
                ? Results.Json(Describe(result), Json.Options)
                : Results.Json(new { error = "Release is NOT verified for every region. Affected regions stay fenced; review the status and run Stop again.", result = Describe(result) },
                    Json.Options, statusCode: StatusCodes.Status409Conflict);
        });
    }

    private static string Actor(HttpContext context) => ((StudioSession)context.Items[typeof(StudioSession)]!).CreatorId;

    private static object Describe(StudioStatus status) => new
    {
        status.Version,
        Regions = status.Regions.Select(region => new
        {
            region.Region, Phase = region.Phase.ToString().ToLowerInvariant(), region.SelectedArtifactId, region.ArmedArtifactId, region.Detail, region.Live,
            region.Nodes, region.ActiveJobs, region.NatPresent, region.PublicIpPresent, region.Blockers, region.Error,
        }),
    };

    private static object Describe(StopResult result) => new
    {
        result.Verified,
        Regions = result.Regions.Select(region => new { region.Region, region.Verified, region.Error, region.Warnings }),
    };
}
