using Azure.Core;
using Microsoft.Extensions.Options;

namespace VideoSafetyStudio.Infrastructure.AzureMl;

/// <summary>The validated Azure ML scope and release, built once at first use (a bad configuration fails loudly) plus the shared credential.</summary>
public sealed record AzureMlRuntime(AzureMlFoundation Foundation, AzureMlRelease? Release, TokenCredential Credential)
{
    public static AzureMlRuntime Load(AzureMlOptions options, TokenCredential credential)
    {
        var foundation = AzureMlConfig.Foundation(options);
        return new AzureMlRuntime(foundation, AzureMlConfig.Release(options, foundation), credential);
    }
}
