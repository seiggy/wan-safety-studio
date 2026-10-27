namespace VideoSafetyStudio.Application.Operations;

/// <summary>Small helpers over Azure resource IDs and the default ownership tags.</summary>
public static class StudioResourceNames
{
    public static string NameOf(string id) => id[(id.LastIndexOf('/') + 1)..];

    /// <summary>The tags the foundation resources carry unless <c>Studio:Regions:{region}:OwnershipTags</c> says otherwise.</summary>
    public static Dictionary<string, string> Tags(StudioScope scope, string region) => new(StringComparer.Ordinal)
    {
        ["application"] = "wan-safety-studio",
        ["deployment"] = scope.DeploymentName,
        ["managedBy"] = "terraform",
        ["region"] = region,
    };
}