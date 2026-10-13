using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;

namespace VideoSafetyStudio.Infrastructure.AzureMl;

/// <summary>The few Azure ML operations the studio needs, so tests can fake the workspace.</summary>
public interface IAzureMlClient
{
    /// <summary>Creates the job (never retried) and returns it as Azure ML stored it.</summary>
    Task<JsonNode> CreateJobAsync(AzureMlJobSpec spec, CancellationToken ct);

    Task<JsonNode> GetJobAsync(string name, CancellationToken ct);

    Task CancelJobAsync(string name, CancellationToken ct);

    /// <summary>The newest jobs, up to <paramref name="max"/>.</summary>
    Task<IReadOnlyList<JsonNode>> ListJobsAsync(int max, CancellationToken ct);

    /// <summary>The compute's provisioningState, or null when it does not exist.</summary>
    Task<string?> GetComputeStateAsync(CancellationToken ct);
}

public sealed class AzureMlException(string message, HttpStatusCode? status = null, string? detail = null) : Exception(message), VideoSafetyStudio.Domain.Generation.ISafeMessage
{
    public HttpStatusCode? Status { get; } = status;

    /// <summary>The provider's own error text: for logs only, never shown to users.</summary>
    public string? Detail { get; } = detail;

    public override string ToString() => Detail is null ? base.ToString() : $"{base.ToString()}{Environment.NewLine}Detail: {Detail}";

    public bool Transient => Status is null or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError or HttpStatusCode.RequestTimeout;
}

/// <summary>The workspace's ARM REST API (api-version 2024-10-01), called with the managed identity (or the developer's login locally).</summary>
public sealed class ArmAzureMlClient(HttpClient http, TokenCredential credential, AzureMlFoundation foundation) : IAzureMlClient
{
    private const string ApiVersion = "api-version=2024-10-01";
    private const string Arm = "https://management.azure.com";

    private string JobUrl(string name) => $"{Arm}{foundation.WorkspaceArmId}/jobs/{Uri.EscapeDataString(name)}";

    public Task<JsonNode> CreateJobAsync(AzureMlJobSpec spec, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"{JobUrl(spec.Name)}?{ApiVersion}", spec.ToArmBody(), ct);

    public Task<JsonNode> GetJobAsync(string name, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"{JobUrl(name)}?{ApiVersion}", null, ct);

    public async Task CancelJobAsync(string name, CancellationToken ct) =>
        await SendAsync(HttpMethod.Post, $"{JobUrl(name)}/cancel?{ApiVersion}", null, ct);

    public async Task<IReadOnlyList<JsonNode>> ListJobsAsync(int max, CancellationToken ct)
    {
        var jobs = new List<JsonNode>();
        string? url = $"{Arm}{foundation.WorkspaceArmId}/jobs?{ApiVersion}&jobType=Command";
        while (url is not null && jobs.Count < max)
        {
            var page = await SendAsync(HttpMethod.Get, url, null, ct);
            jobs.AddRange(page["value"]?.AsArray().Where(node => node is not null).Select(node => node!) ?? []);
            url = page["nextLink"]?.GetValue<string>() is { } next && next.StartsWith(Arm + "/", StringComparison.Ordinal) ? next : null;
        }

        return [.. jobs.Take(max)];
    }

    public async Task<string?> GetComputeStateAsync(CancellationToken ct)
    {
        try
        {
            var compute = await SendAsync(HttpMethod.Get, $"{Arm}{foundation.ComputeId}?{ApiVersion}", null, ct);
            return compute["properties"]?["provisioningState"]?.GetValue<string>();
        }
        catch (AzureMlException error) when (error.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string url, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        var token = await credential.GetTokenAsync(new TokenRequestContext([$"{Arm}/.default"]), ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = TryParse(text)?["error"];
            var message = error?["message"]?.ToString();
            var details = error?["details"]?.ToJsonString();
            var full = string.Join(" ", new[] { message, details }.Where(s => !string.IsNullOrEmpty(s)));
            throw new AzureMlException($"Azure ML {method} failed ({(int)response.StatusCode})", response.StatusCode, full.Length == 0 ? null : full[..Math.Min(full.Length, 1500)]);
        }

        return TryParse(text) ?? new JsonObject();
    }

    private static JsonNode? TryParse(string text)
    {
        try
        {
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
