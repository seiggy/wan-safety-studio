using System.Security.Claims;
using System.Security.Cryptography;

namespace VideoSafetyStudio.Server.Auth;

public enum ClaimsVerdict
{
    Accepted,
    Forbidden,
    Failed,
}

/// <summary>Tenant, audience and either the VideoCreator or StudioAdmin role, then a session identity carrying exactly the roles held.</summary>
public static class CreatorClaims
{
    public const string Csrf = "csrf";
    public const string AdminClaim = "studio_admin";
    public const string CreatorClaim = "studio_creator";

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
        var roles = principal.FindAll("roles").Select(claim => claim.Value).ToList();
        if (tenant != settings.TenantId || !audiences.Contains(settings.ClientId) || !(roles.Contains(AuthSettings.RequiredRole) || roles.Contains(AuthSettings.AdminRole)))
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
        var claims = new List<Claim> { new("oid", objectId!), new("name", name), new(Csrf, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))) };
        // Each role is its own claim: an administrator never acquires creator access, and a creator never acquires administration.
        if (roles.Contains(AuthSettings.RequiredRole)) claims.Add(new Claim(CreatorClaim, "1"));
        if (roles.Contains(AuthSettings.AdminRole)) claims.Add(new Claim(AdminClaim, "1"));
        var identity = new ClaimsIdentity(claims, "wan-session", "name", "roles");
        session = new ClaimsPrincipal(identity);
        return ClaimsVerdict.Accepted;
    }
}
