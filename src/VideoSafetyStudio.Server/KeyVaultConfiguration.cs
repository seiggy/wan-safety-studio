using Azure.Core;
using Azure.Extensions.AspNetCore.Configuration.Secrets;

namespace VideoSafetyStudio.Server;

public static class KeyVaultConfiguration
{
    public const string UriKey = "KeyVault:Uri";

    /// <summary>
    /// Adds Azure Key Vault as the last (highest priority) configuration source when KeyVault:Uri is set; secret names use `--` for `:`
    /// (Auth--ClientSecret is Auth:ClientSecret). Returns false, touching nothing, when it is unset.
    /// </summary>
    public static bool AddStudioKeyVault(this IConfigurationBuilder builder, IConfiguration current, TokenCredential credential)
    {
        if (string.IsNullOrWhiteSpace(current[UriKey]))
        {
            return false;
        }

        if (!Uri.TryCreate(current[UriKey]!.Trim(), UriKind.Absolute, out var vault) || vault.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"{UriKey} must be an https vault URI such as https://<name>.vault.azure.net/.");
        }

        builder.AddAzureKeyVault(vault, credential, new AzureKeyVaultConfigurationOptions());
        return true;
    }
}
