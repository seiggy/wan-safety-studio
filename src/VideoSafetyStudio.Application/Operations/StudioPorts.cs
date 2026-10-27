using System.Text.Json.Nodes;

namespace VideoSafetyStudio.Application.Operations;

/// <summary>Native Azure/ARM and private-storage ports; Infrastructure implements them, tests simulate them.</summary>
public interface IStudioArm
{
    /// <summary>The resource, or null when ARM answers 404. Every other failure throws.</summary>
    Task<JsonNode?> GetAsync(string id, string api, CancellationToken ct);

    /// <summary>PUT/POST/DELETE on an exact in-scope resource id (may carry a query). Returns the response body, if any.</summary>
    Task<JsonNode?> SendAsync(string method, string id, string api, JsonNode? body, string? ifMatch, CancellationToken ct);

    /// <summary>Every page of a list; an incomplete, malformed or out-of-scope continuation throws instead of returning a prefix.</summary>
    Task<IReadOnlyList<JsonNode>> ListAsync(string id, string api, CancellationToken ct);
}

public sealed record StoredState(string Json, string ETag);

public sealed class StudioStateConflictException() : Exception("Operational state changed concurrently.");

/// <summary>Durable operation fence and audit trail in the control-region <c>wan-control</c> container.</summary>
public interface IStudioControlStore
{
    Task<StoredState?> ReadStateAsync(CancellationToken ct);

    /// <summary>Create-only when <paramref name="etag"/> is null, otherwise If-Match. Throws <see cref="StudioStateConflictException"/> on a lost race.</summary>
    Task<string> WriteStateAsync(string json, string? etag, CancellationToken ct);

    Task AppendAuditAsync(string name, string json, CancellationToken ct);

    /// <summary>The durable regional settings document (written by the administration surface; absent means deployed defaults).</summary>
    Task<StoredState?> ReadSettingsAsync(CancellationToken ct);

    Task<string> WriteSettingsAsync(string json, string? etag, CancellationToken ct);
}

/// <summary>Read-only access to a region's prepared release container; the host never writes or acquires assets.</summary>
public interface IStudioReleaseReader
{
    Task<string?> ReadAsync(string region, string path, CancellationToken ct);

    /// <summary>Every blob path under the prefix in that region's release container.</summary>
    Task<IReadOnlyList<string>> ListAsync(string region, string prefix, CancellationToken ct);
}

public interface IStudioPrices
{
    Task<IReadOnlyList<JsonNode>> ListAsync(string vmSize, string region, CancellationToken ct);
}

public interface IStudioConnectivity
{
    /// <summary>Throws unless the host resolves only to private addresses and accepts TCP 443.</summary>
    Task AssertPrivateAsync(string host, CancellationToken ct);
}
