using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace VideoSafetyStudio.Server.Auth;

/// <summary>The signed-in user for the current request; <see cref="Expires"/> is Unix seconds like the legacy /api/session.</summary>
public sealed record StudioSession(string Name, string Csrf, double Expires);

public static class AuthExtensions
{
    public const string CookieScheme = "Cookie";
    private const string OidcScheme = "oidc";
    private const string TokenExchangeScope = "api://AzureADTokenExchange/.default";
    private const string JwtBearerAssertion = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
    private static readonly StudioSession Local = new("Local developer", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), 32503680000.0);
    private static readonly StudioSession OpenAccess = new("Open access (sign-in disabled)", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), 32503680000.0);

    /// <summary>
    /// Registers sign-in. Everything resolves the final configuration lazily; <see cref="UseStudioAuth"/> resolves the settings
    /// at startup, so an unauthenticated non-Development host refuses to start.
    /// </summary>
    public static void AddStudioAuth(this IServiceCollection services)
    {
        services.AddSingleton(sp => AuthSettings.Resolve(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>()));
        services.AddDataProtection().SetApplicationName("wan-safety-studio");
        // One instance: keys on /home survive App Service restarts so sessions are not dropped.
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp => new ConfigureOptions<KeyManagementOptions>(options =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var keys = configuration["Auth:KeysPath"] ?? (configuration["WEBSITE_SITE_NAME"] is null ? null : "/home/.wan-studio/keys");
            if (keys is not null && sp.GetRequiredService<AuthSettings>().Mode == AuthMode.Entra)
            {
                try
                {
                    options.XmlRepository = new FileSystemXmlRepository(Directory.CreateDirectory(keys), sp.GetRequiredService<ILoggerFactory>());
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Sessions then end on restart, but the site still starts.
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Auth").LogWarning(error, "Cannot persist data-protection keys to {Path}; using the default location.", keys);
                }
            }
        }));

        services.AddAuthentication(CookieScheme)
            .AddCookie(CookieScheme, _ => { })
            .AddOpenIdConnect(OidcScheme, _ => { });
        services.AddOptions<CookieAuthenticationOptions>(CookieScheme).Configure<AuthSettings>((cookie, settings) =>
        {
            cookie.Cookie.Name = "wan_session";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Lax;
            cookie.Cookie.SecurePolicy = settings.Secure ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            cookie.ExpireTimeSpan = CreatorClaims.SessionLifetime;
            cookie.SlidingExpiration = false;
        });
        services.AddOptions<OpenIdConnectOptions>(OidcScheme).Configure<AuthSettings>(ConfigureOidc);
    }

    private static void ConfigureOidc(OpenIdConnectOptions oidc, AuthSettings settings)
    {
        if (settings.Mode != AuthMode.Entra)
        {
            // The scheme is registered unconditionally and validated at startup; these placeholders are never used to sign in.
            oidc.ClientId = "unused";
            oidc.Authority = "https://login.microsoftonline.com/common/v2.0";
            return;
        }

        oidc.Authority = $"https://login.microsoftonline.com/{settings.TenantId}/v2.0";
        oidc.SignInScheme = CookieScheme;
        oidc.ClientId = settings.ClientId;
        oidc.ClientSecret = settings.ClientSecret;
        oidc.ResponseType = OpenIdConnectResponseType.Code;
        oidc.CallbackPath = AuthSettings.CallbackPath;
        oidc.UsePkce = true;
        oidc.SaveTokens = false;
        oidc.MapInboundClaims = false;
        oidc.GetClaimsFromUserInfoEndpoint = false;
        oidc.Scope.Clear();
        oidc.Scope.Add("openid");
        oidc.Scope.Add("profile");
        var assertionCredential = settings.ClientSecret is null && settings.ManagedIdentityClientId is not null
            ? new Azure.Identity.ManagedIdentityCredential(Azure.Identity.ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId))
            : null;

        oidc.Events = new OpenIdConnectEvents
        {
            // Behind the App Service proxy the request URL is not the public one: pin it.
            OnRedirectToIdentityProvider = context =>
            {
                context.ProtocolMessage.RedirectUri = settings.RedirectUri;
                context.ProtocolMessage.Prompt = "select_account";
                return Task.CompletedTask;
            },
            OnAuthorizationCodeReceived = async context =>
            {
                context.TokenEndpointRequest!.RedirectUri = settings.RedirectUri;
                if (assertionCredential is not null)
                {
                    var token = await assertionCredential.GetTokenAsync(new Azure.Core.TokenRequestContext([TokenExchangeScope]), context.HttpContext.RequestAborted);
                    context.TokenEndpointRequest.ClientAssertionType = JwtBearerAssertion;
                    context.TokenEndpointRequest.ClientAssertion = token.Token;
                }
            },
            OnTokenValidated = context =>
            {
                var verdict = CreatorClaims.Validate(context.Principal, settings, DateTimeOffset.UtcNow, out var session, out var expires);
                if (verdict != ClaimsVerdict.Accepted)
                {
                    context.Response.Redirect("/?signin=" + (verdict == ClaimsVerdict.Forbidden ? "forbidden" : "failed"));
                    context.HandleResponse();
                    return Task.CompletedTask;
                }

                context.Principal = session;
                context.Properties!.IsPersistent = false;
                context.Properties.IssuedUtc = DateTimeOffset.UtcNow;
                context.Properties.ExpiresUtc = expires;
                context.Properties.AllowRefresh = false;
                return Task.CompletedTask;
            },
            OnAuthenticationFailed = context =>
            {
                context.Response.Redirect("/?signin=failed");
                context.HandleResponse();
                return Task.CompletedTask;
            },
            OnRemoteFailure = context =>
            {
                context.Response.Redirect("/?signin=failed");
                context.HandleResponse();
                return Task.CompletedTask;
            },
        };
    }

    public static void UseStudioAuth(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<AuthSettings>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Auth");
        if (settings.Mode == AuthMode.Open)
        {
            logger.LogWarning("WAN_STUDIO_DISABLE_AUTH is true: sign-in is OFF and anyone who can reach this site can submit GPU jobs.");
        }

        app.Use((context, next) => Protect(context, next, settings, logger));
        if (settings.Mode == AuthMode.Entra)
        {
            app.UseAuthentication();
        }
    }

    private static readonly HashSet<string> PublicPaths = ["/", "/videos", "/healthz", "/health", "/alive", "/auth/login", "/auth/callback", "/auth/logout"];

    private static async Task Protect(HttpContext context, RequestDelegate next, AuthSettings settings, ILogger logger)
    {
        var path = context.Request.Path.Value ?? "/";
        if (settings.Mode == AuthMode.LocalDev)
        {
            context.Items[typeof(StudioSession)] = Local;
            await next(context);
            return;
        }

        // The platform health probe may not send the site host; /healthz exposes nothing.
        if (settings.Host is not null && path != "/healthz" && !string.Equals(context.Request.Host.Value, settings.Host, StringComparison.OrdinalIgnoreCase))
        {
            await Deny(context, StatusCodes.Status403Forbidden, "This site accepts only its private host.");
            return;
        }

        StudioSession? session = settings.Mode == AuthMode.Open ? OpenAccess : await ReadSession(context);
        var isApi = path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) && (path.Length == 4 || path[4] == '/');
        var isAuth = path.StartsWith("/auth", StringComparison.OrdinalIgnoreCase) && (path.Length == 5 || path[5] == '/');
        var isPublic = PublicPaths.Contains(path) || (!isApi && !isAuth);
        if (!isPublic && session is null)
        {
            await Deny(context, StatusCodes.Status401Unauthorized, "Sign in with your approved creator account.");
            return;
        }

        if (settings.Mode == AuthMode.Entra && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method))
        {
            var originOk = context.Request.Headers.Origin == settings.Origin;
            var token = context.Request.Headers["X-CSRF-Token"].ToString();
            var tokenOk = session is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(session.Csrf));
            if (!originOk || !tokenOk)
            {
                logger.LogWarning("Rejected {Method} {Path}: session={HasSession} originOk={OriginOk} tokenOk={TokenOk}", context.Request.Method, path, session is not null, originOk, tokenOk);
                await Deny(context, StatusCodes.Status403Forbidden, "Invalid request origin or CSRF token. Refresh and try again.");
                return;
            }
        }

        if (session is not null)
        {
            context.Items[typeof(StudioSession)] = session;
        }

        await next(context);
    }

    private static async Task<StudioSession?> ReadSession(HttpContext context)
    {
        var result = await context.AuthenticateAsync(CookieScheme);
        if (!result.Succeeded || result.Principal?.FindFirst(CreatorClaims.Csrf)?.Value is not { } csrf)
        {
            return null;
        }

        var expires = result.Properties?.ExpiresUtc ?? DateTimeOffset.UtcNow.Add(CreatorClaims.SessionLifetime);
        return new StudioSession(result.Principal.FindFirst("name")?.Value ?? "Video creator", csrf, expires.ToUnixTimeSeconds());
    }

    private static Task Deny(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = message });
    }

    public static void MapAuth(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<AuthSettings>();
        app.MapGet("/api/session", (HttpContext context) =>
            context.Items[typeof(StudioSession)] is StudioSession session
                ? Results.Json(new { name = session.Name, csrfToken = session.Csrf, expires = session.Expires })
                : Results.Json(new { error = "Sign in with your approved creator account." }, statusCode: StatusCodes.Status401Unauthorized));

        app.MapGet("/auth/login", () => settings.Mode == AuthMode.Entra
            ? Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, [OidcScheme])
            : Results.Redirect("/"));

        app.MapPost("/auth/logout", async (HttpContext context) =>
        {
            if (settings.Mode == AuthMode.Entra)
            {
                await context.SignOutAsync(CookieScheme);
            }

            return Results.Json(new { signedOut = true });
        });
    }
}
