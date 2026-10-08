using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoSafetyStudio.Server.Auth;

public enum AuthMode
{
    /// <summary>Local developer machine: no sign-in, one fixed identity.</summary>
    LocalDev,

    /// <summary>Hosted behind a gateway that already authenticates: one fixed identity, host check kept.</summary>
    Open,

    /// <summary>Hosted: Entra sign-in, role check, CSRF.</summary>
    Entra,
}

/// <summary>Resolved sign-in configuration. Mirrors the Python portal's load_auth_config and mode flags.</summary>
public sealed partial record AuthSettings(
    AuthMode Mode,
    string? Origin = null,
    string? TenantId = null,
    string? ClientId = null,
    string? ManagedIdentityClientId = null,
    string? ClientSecret = null)
{
    public const string RequiredRole = "VideoCreator";
    public const string CallbackPath = "/auth/callback";

    public string? Host => Origin is null ? null : new Uri(Origin).Authority;

    public bool Secure => Origin?.StartsWith("https://", StringComparison.Ordinal) == true;

    public string? RedirectUri => Origin is null ? null : Origin + CallbackPath;

    public static AuthSettings Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        var disable = configuration.GetValue<bool>("WAN_STUDIO_DISABLE_AUTH");
        var clientId = Blank(configuration["WAN_STUDIO_PORTAL_CLIENT_ID"]);
        var explicitOrigin = Blank(configuration["WAN_STUDIO_PUBLIC_ORIGIN"]);
        var foundation = Foundation.Read(configuration);
        var origin = explicitOrigin ?? (foundation.Hostname is { } hostname ? "https://" + hostname : null);

        // A foundation file alone on a developer machine (provider azureml run locally) is not a hosted site.
        var hosted = explicitOrigin is not null || clientId is not null || (origin is not null && !environment.IsDevelopment());
        if (!hosted)
        {
            if (environment.IsDevelopment() || disable || configuration.GetValue<bool>("Server:AllowAnonymous"))
            {
                return new AuthSettings(AuthMode.LocalDev);
            }

            throw new InvalidOperationException(
                "VideoSafetyStudio.Server has no sign-in configured. Set WAN_STUDIO_PORTAL_CLIENT_ID and WAN_STUDIO_PUBLIC_ORIGIN (or a foundation portal.hostname), "
                + "or WAN_STUDIO_DISABLE_AUTH=true / Server:AllowAnonymous=true behind a gateway that authenticates, or run in Development.");
        }

        if (origin is not null && !OriginPattern().IsMatch(origin))
        {
            throw new InvalidOperationException("WAN_STUDIO_PUBLIC_ORIGIN must be an https://host origin (or a loopback http origin).");
        }

        if (disable)
        {
            return new AuthSettings(AuthMode.Open, origin);
        }

        var secret = Blank(configuration["Auth:ClientSecret"]);
        var identity = Blank(configuration["WAN_STUDIO_MANAGED_IDENTITY_CLIENT_ID"]);
        if (clientId is null || origin is null || foundation.TenantId is null)
        {
            throw new InvalidOperationException("Hosted sign-in needs WAN_STUDIO_PORTAL_CLIENT_ID, a public origin and a foundation tenantId.");
        }

        if (secret is null && identity is null)
        {
            throw new InvalidOperationException("Hosted sign-in needs a client credential: WAN_STUDIO_MANAGED_IDENTITY_CLIENT_ID (federated) or Auth:ClientSecret.");
        }

        return new AuthSettings(AuthMode.Entra, origin, foundation.TenantId, clientId, identity, secret);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^(https://[a-z0-9-]+(\.[a-z0-9-]+)+|http://(localhost|127\.0\.0\.1)(:\d+)?)$")]
    private static partial Regex OriginPattern();

    /// <summary>Only the two foundation keys sign-in needs: tenantId and portal.hostname.</summary>
    private readonly record struct Foundation(string? TenantId, string? Hostname)
    {
        public static Foundation Read(IConfiguration configuration)
        {
            var path = Blank(configuration["WAN_STUDIO_FOUNDATION"]);
            var inline = Blank(configuration["WAN_STUDIO_FOUNDATION_JSON"]);
            if (path is not null && inline is not null)
            {
                throw new InvalidOperationException("Set WAN_STUDIO_FOUNDATION or WAN_STUDIO_FOUNDATION_JSON, not both.");
            }

            if (path is null && inline is null)
            {
                return default;
            }

            using var document = JsonDocument.Parse(inline ?? File.ReadAllText(path!));
            var root = document.RootElement;
            string? Text(JsonElement element, string name) =>
                element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? Blank(value.GetString())
                    : null;
            return new Foundation(Text(root, "tenantId"), root.TryGetProperty("portal", out var portal) ? Text(portal, "hostname") : null);
        }
    }
}
