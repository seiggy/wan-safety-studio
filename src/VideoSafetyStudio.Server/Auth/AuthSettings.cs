using System.Text.RegularExpressions;

namespace VideoSafetyStudio.Server.Auth;

public enum AuthMode
{
    /// <summary>No sign-in: one fixed local identity (developer machine, or behind a gateway that already authenticates).</summary>
    None,

    /// <summary>Entra sign-in, role check, CSRF.</summary>
    Entra,
}

/// <summary>The `Auth` configuration section.</summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>None | Entra. Unset: None in Development, Entra otherwise.</summary>
    public string? Mode { get; set; }

    /// <summary>Entra app registration client id.</summary>
    public string? ClientId { get; set; }

    /// <summary>Defaults to AzureMl:TenantId.</summary>
    public string? TenantId { get; set; }

    /// <summary>The site's public https origin; the redirect URI and CSRF origin are built from it.</summary>
    public string? PublicOrigin { get; set; }

    /// <summary>Optional client secret; without it the managed identity (AZURE_CLIENT_ID) signs a federated assertion.</summary>
    public string? ClientSecret { get; set; }
}

/// <summary>Resolved and validated sign-in configuration.</summary>
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
        var options = configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
        if (ModeOf(options, environment) == AuthMode.None)
        {
            return new AuthSettings(AuthMode.None);
        }

        var origin = Blank(options.PublicOrigin);
        var clientId = Blank(options.ClientId);
        var tenant = Blank(options.TenantId) ?? Blank(configuration["AzureMl:TenantId"]);
        var secret = Blank(options.ClientSecret);
        var identity = Blank(configuration["AZURE_CLIENT_ID"]);

        var missing = new List<string>();
        if (clientId is null) missing.Add("Auth:ClientId");
        if (origin is null) missing.Add("Auth:PublicOrigin");
        if (tenant is null) missing.Add("Auth:TenantId (or AzureMl:TenantId)");
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Entra sign-in needs {string.Join(", ", missing)}. Set Auth:Mode=None only behind a gateway that authenticates.");
        }

        if (!OriginPattern().IsMatch(origin!))
        {
            throw new InvalidOperationException("Auth:PublicOrigin must be an https://host origin (or a loopback http origin).");
        }

        if (secret is null && identity is null)
        {
            throw new InvalidOperationException("Entra sign-in needs a client credential: AZURE_CLIENT_ID (managed identity, federated) or Auth:ClientSecret.");
        }

        return new AuthSettings(AuthMode.Entra, origin, tenant, clientId, identity, secret);
    }

    private static AuthMode ModeOf(AuthOptions options, IHostEnvironment environment) =>
        Blank(options.Mode) switch
        {
            null => environment.IsDevelopment() ? AuthMode.None : AuthMode.Entra,
            var text when Enum.TryParse<AuthMode>(text, true, out var mode) && Enum.IsDefined(mode) => mode,
            _ => throw new InvalidOperationException("Auth:Mode must be None or Entra."),
        };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^(https://[a-z0-9-]+(\.[a-z0-9-]+)+|http://(localhost|127\.0\.0\.1)(:\d+)?)$")]
    private static partial Regex OriginPattern();
}
