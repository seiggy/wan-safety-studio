using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.Options;
using VideoSafetyStudio.Infrastructure.Agent;

namespace VideoSafetyStudio.Server.Endpoints;

public static class AgentEndpoints
{
    public static IServiceCollection AddVideoHelper(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<VideoHelperOptions>(configuration.GetSection(VideoHelperOptions.Section));
        services.AddSingleton<VideoHelperAgent>();
        services.AddAGUIServer();
        return services;
    }

    /// <summary>The helper's AG-UI stream lives at /api/agent; /api/agent/status tells the UI whether to offer it.</summary>
    public static void MapVideoHelper(this WebApplication app)
    {
        app.MapGet("/api/agent/status", (IOptions<VideoHelperOptions> options) =>
        {
            var agent = options.Value;
            return Results.Json(new
            {
                enabled = agent.Enabled,
                model = agent.Enabled ? agent.Model : null,
                endpoint = agent.Enabled ? agent.EndpointUri?.Host : null,
                vision = agent.Enabled && agent.Vision,
                auth = agent.Enabled ? agent.ResolvedAuth?.ToString() : null,
                hint = agent.ConfigurationError,
            });
        });

        if (app.Services.GetRequiredService<IOptions<VideoHelperOptions>>().Value.Enabled)
        {
            app.MapAGUIServer("/api/agent", app.Services.GetRequiredService<VideoHelperAgent>());
        }
    }
}
