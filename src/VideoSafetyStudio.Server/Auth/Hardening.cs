using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace VideoSafetyStudio.Server.Auth;

/// <summary>Request body ceilings: a small global default, raised per upload route.</summary>
public static class BodyLimits
{
    public const long Default = 32L * 1024 * 1024;

    /// <summary>One video upload.</summary>
    public const long Video = 512L * 1024 * 1024;

    /// <summary>A submit with up to five scenes, each with an image of up to 64 MB, plus the form text.</summary>
    public const long Submit = 5L * 64 * 1024 * 1024 + 4 * 1024 * 1024;
}

public static class Hardening
{
    public const string PaidPolicy = "paid";
    public const string AgentPolicy = "agent";

    /// <summary>Content-Security-Policy for the built UI: its scripts and styles are served files; media comes from this origin and, when blob storage is configured, that account's host.</summary>
    public static string ContentSecurityPolicy(IConfiguration configuration)
    {
        var media = "'self' blob:";
        var account = configuration["Storage:Account"];
        if (string.Equals(configuration["Storage:Backend"], "azure", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(account))
        {
            media += $" https://{account.Trim()}.blob.core.windows.net";
        }

        // style-src allows inline styles: the diagram renderer injects <style> elements. Scripts stay 'self' only.
        return "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
               $"media-src {media}; connect-src 'self'; worker-src 'self' blob:; font-src 'self'; " +
               "object-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    }

    /// <summary>Runs before auth so 401 and 403 responses carry the headers too.</summary>
    public static void UseSecurityHeaders(this WebApplication app, IConfiguration configuration)
    {
        var csp = ContentSecurityPolicy(configuration);
        app.Use((context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["X-Frame-Options"] = "DENY";
            headers["Content-Security-Policy"] = csp;
            return next();
        });
    }

    /// <summary>Per-creator limits on the calls that spend GPU or model money. Keys: RateLimits:SubmitPermits, AgentPermits, WindowMinutes.</summary>
    public static void AddStudioRateLimits(this IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IConfiguration>((limits, configuration) =>
        {
            var submit = Math.Max(1, configuration.GetValue("RateLimits:SubmitPermits", 10));
            var agent = Math.Max(1, configuration.GetValue("RateLimits:AgentPermits", 60));
            var window = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("RateLimits:WindowMinutes", 10)));
            limits.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limits.OnRejected = (rejected, ct) => new ValueTask(rejected.HttpContext.Response.WriteAsJsonAsync(new { error = "Too many requests. Try again later." }, ct));
            limits.AddPolicy(PaidPolicy, http => Partition(http, submit, window));
            limits.AddPolicy(AgentPolicy, http => Partition(http, agent, window));
        });
    }

    private static RateLimitPartition<string> Partition(HttpContext http, int permits, TimeSpan window)
    {
        var who = http.Items[typeof(StudioSession)] is StudioSession session ? session.CreatorId : "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(who, _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window, QueueLimit = 0 });
    }
}
