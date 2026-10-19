using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace VideoSafetyStudio.Infrastructure;

/// <summary>
/// The one credential the server uses for Azure: the user-assigned managed identity named by AZURE_CLIENT_ID when set,
/// otherwise the default chain (the Azure CLI login on a developer machine; AZURE_TOKEN_CREDENTIALS narrows it).
/// </summary>
public static class StudioCredential
{
    public static TokenCredential Create(IConfiguration configuration) =>
        Blank(configuration["AZURE_CLIENT_ID"]) is { } identity
            ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(identity))
            : new DefaultAzureCredential(new DefaultAzureCredentialOptions { TenantId = Blank(configuration["AzureMl:TenantId"]) });

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
