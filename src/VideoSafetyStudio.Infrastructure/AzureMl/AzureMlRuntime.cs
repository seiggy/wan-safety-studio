using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace VideoSafetyStudio.Infrastructure.AzureMl;

/// <summary>Everything the Azure ML provider is configured with, loaded once at first use (a bad configuration fails the server loudly).</summary>
public sealed record AzureMlRuntime(AzureMlFoundation Foundation, AzureMlRelease? Release, TokenCredential Credential)
{
    public static AzureMlRuntime Load(IConfiguration configuration)
    {
        var foundation = AzureMlConfig.LoadFoundation(configuration);
        var identity = configuration["WAN_STUDIO_MANAGED_IDENTITY_CLIENT_ID"];
        TokenCredential credential = string.IsNullOrWhiteSpace(identity)
            ? new DefaultAzureCredential(new DefaultAzureCredentialOptions { TenantId = foundation.TenantId })
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(identity));
        return new AzureMlRuntime(foundation, AzureMlConfig.LoadRelease(configuration, foundation), credential);
    }
}
