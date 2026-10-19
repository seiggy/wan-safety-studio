using System.Security.Claims;
using System.Security.Cryptography;

namespace VideoSafetyStudio.Server.Auth;

public enum ClaimsVerdict
{
    Accepted,
    Forbidden,
    Failed,
}

/// <summary>Port of creator_identity: tenant, audience and Video Creator role, then a session identity.</summary>
public static class CreatorClaims
{
    public const string Csrf = "csrf";

    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(1);

    public static ClaimsVerdict Validate(ClaimsPrincipal? principal, AuthSettings settings, DateTimeOffset now, out ClaimsPrincipal? session, out DateTimeOffset expires)
    {
        session = null;
        expires = default;
        if (principal is null)
        {
            return ClaimsVerdict.Forbidden;
        }

        var tenant = principal.FindFirst("tid")?.Value;
        var audiences = principal.FindAll("aud").Select(claim => claim.Value);
        var roles = principal.FindAll("roles").Select(claim => claim.Value);
        if (tenant != settings.TenantId || !audiences.Contains(settings.ClientId) || !roles.Contains(AuthSettings.RequiredRole))
        {
            return ClaimsVerdict.Forbidden;
        }

        var objectId = principal.FindFirst("oid")?.Value;
        if (!Guid.TryParse(objectId, out _) || !long.TryParse(principal.FindFirst("exp")?.Value, out var exp))
        {
            return ClaimsVerdict.Forbidden;
        }

        var tokenExpiry = DateTimeOffset.FromUnixTimeSeconds(exp);
        if (tokenExpiry <= now)
        {
            return ClaimsVerdict.Failed;
        }

        var name = principal.FindFirst("name")?.Value;
        name = string.IsNullOrWhiteSpace(name) ? "Video creator" : name.Length > 200 ? name[..200] : name;
        expires = now + SessionLifetime < tokenExpiry ? now + SessionLifetime : tokenExpiry;
        var identity = new ClaimsIdentity(
            [new Claim("oid", objectId!), new Claim("name", name), new Claim(Csrf, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))],
            "wan-session", "name", "roles");
        session = new ClaimsPrincipal(identity);
        return ClaimsVerdict.Accepted;
    }
}
