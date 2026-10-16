using Azure.Core;
using Azure.Identity;

namespace VideoSafetyStudio.Infrastructure.Agent;

public enum VideoHelperAuth
{
    ApiKey,
    AzureCli,
    SystemAssignedManagedIdentity,
    UserAssignedManagedIdentity,
}

/// <summary>
/// Bring-your-own-key model endpoint for the Video Production Helper. Any OpenAI v1-compatible endpoint works:
/// Azure AI Foundry (<c>https://RESOURCE.openai.azure.com/openai/v1/</c>), or a self-hosted/third-party server.
/// Bound from the <c>Agent</c> section, so every setting is also an environment variable (<c>Agent__Endpoint</c>, ...).
/// </summary>
public sealed class VideoHelperOptions
{
    public const string Section = "Agent";
    public const string TokenScope = "https://cognitiveservices.azure.com/.default";

    /// <summary>OpenAI v1 base URL. Empty disables the helper.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Model or Foundry deployment name sent to the endpoint.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// How to authenticate: <c>ApiKey</c>, <c>AzureCli</c>, <c>SystemAssignedManagedIdentity</c> or <c>UserAssignedManagedIdentity</c>.
    /// When unset: <c>ApiKey</c> if a key is present, otherwise <c>AzureCli</c> for an Azure endpoint.
    /// </summary>
    public string? Auth { get; set; }

    /// <summary>API key, for <c>ApiKey</c> auth.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Client ID of the user-assigned managed identity, for <c>UserAssignedManagedIdentity</c> auth.</summary>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary><c>completions</c> or <c>responses</c>. When unset: <c>responses</c> for Azure endpoints (Foundry reasoning models reject function tools on completions), else <c>completions</c> (widest compatibility).</summary>
    public string? WireApi { get; set; }

    /// <summary>The model accepts images, so the helper can look at the scene reference images. Set false for text-only models.</summary>
    public bool Vision { get; set; } = true;

    /// <summary>Isolated state folder for the Copilot runtime; defaults to a folder under the user's local app data.</summary>
    public string? StateDirectory { get; set; }

    public Uri? EndpointUri => Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) ? uri : null;

    public bool IsAzureEndpoint => EndpointUri?.Host.EndsWith(".azure.com", StringComparison.OrdinalIgnoreCase) == true;

    public string ResolvedWireApi => !string.IsNullOrWhiteSpace(WireApi) ? WireApi.Trim().ToLowerInvariant() : IsAzureEndpoint ? "responses" : "completions";

    /// <summary>The auth method in effect, or null when <see cref="Auth"/> is not recognised.</summary>
    public VideoHelperAuth? ResolvedAuth => Auth?.Trim().ToLowerInvariant() switch
    {
        null or "" => string.IsNullOrWhiteSpace(ApiKey) && IsAzureEndpoint ? VideoHelperAuth.AzureCli : VideoHelperAuth.ApiKey,
        "apikey" or "key" => VideoHelperAuth.ApiKey,
        "azurecli" or "cli" or "entra" => VideoHelperAuth.AzureCli,
        "systemassignedmanagedidentity" or "systemmanagedidentity" or "managedidentity" => VideoHelperAuth.SystemAssignedManagedIdentity,
        "userassignedmanagedidentity" or "usermanagedidentity" => VideoHelperAuth.UserAssignedManagedIdentity,
        _ => null,
    };

    /// <summary>What is missing or wrong, or null when the configuration is complete.</summary>
    public string? ConfigurationError
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Endpoint) || string.IsNullOrWhiteSpace(Model)) return "Set Agent:Endpoint (an OpenAI v1 URL) and Agent:Model. See docs/video-helper.md.";
            if (EndpointUri is not { Scheme: "http" or "https" }) return "Agent:Endpoint must be an absolute HTTP or HTTPS URL.";
            if (ResolvedWireApi is not ("completions" or "responses")) return "Agent:WireApi must be completions or responses.";
            return ResolvedAuth switch
            {
                null => $"Agent:Auth '{Auth}' is not recognised. Use ApiKey, AzureCli, SystemAssignedManagedIdentity or UserAssignedManagedIdentity.",
                VideoHelperAuth.ApiKey when string.IsNullOrWhiteSpace(ApiKey) => "Agent:Auth is ApiKey but Agent:ApiKey is empty (set the agent-api-key parameter).",
                VideoHelperAuth.UserAssignedManagedIdentity when string.IsNullOrWhiteSpace(ManagedIdentityClientId) => "Agent:Auth is UserAssignedManagedIdentity but Agent:ManagedIdentityClientId is empty.",
                _ => null,
            };
        }
    }

    public bool Enabled => ConfigurationError is null;

    /// <summary>The Azure credential for the token-based methods; null for <c>ApiKey</c> or an incomplete configuration.</summary>
    public TokenCredential? CreateCredential() => !Enabled ? null : ResolvedAuth switch
    {
        VideoHelperAuth.AzureCli => new AzureCliCredential(),
        VideoHelperAuth.SystemAssignedManagedIdentity => new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned),
        VideoHelperAuth.UserAssignedManagedIdentity => new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(ManagedIdentityClientId!)),
        _ => null,
    };
}
