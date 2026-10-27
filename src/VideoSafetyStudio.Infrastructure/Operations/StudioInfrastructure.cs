using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using VideoSafetyStudio.Application.Operations;

namespace VideoSafetyStudio.Infrastructure.Operations;

/// <summary>The `Studio` configuration section: the cached foundation receipt and the independently derived customer selection.</summary>
/// <summary>The hosted lifecycle when a foundation is configured; null for local development and legacy single-workspace hosts.</summary>
public sealed record StudioOperations(StudioLifecycle? Lifecycle, StudioFoundation? Foundation, BlobStudioStore? Store = null);

/// <summary>ARM called with the host's managed identity. Never retried by a resilience handler: PUT/DELETE can change billing state.</summary>
public sealed class ArmStudioClient(HttpClient http, TokenCredential credential, string subscriptionId) : IStudioArm
{
    private const string Origin = "https://management.azure.com";

    private string Scope => $"/subscriptions/{subscriptionId}/";

    private string Url(string id, string api)
    {
        if (!id.StartsWith(Scope, StringComparison.OrdinalIgnoreCase) || id.Any(char.IsControl) || id.Contains("..", StringComparison.Ordinal) ||
            id.Contains('#', StringComparison.Ordinal) || id.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An ARM request left the approved subscription or was malformed.");
        }

        return $"{Origin}{id}{(id.Contains('?', StringComparison.Ordinal) ? '&' : '?')}api-version={api}";
    }

    public async Task<JsonNode?> GetAsync(string id, string api, CancellationToken ct)
    {
        var (status, body) = await SendRawAsync(HttpMethod.Get, Url(id, api), null, null, ct);
        return status == HttpStatusCode.NotFound ? null : Check("GET", status, body);
    }

    public async Task<JsonNode?> SendAsync(string method, string id, string api, JsonNode? payload, string? ifMatch, CancellationToken ct)
    {
        var (status, body) = await SendRawAsync(new HttpMethod(method), Url(id, api), payload, ifMatch, ct);
        return Check(method, status, body);
    }

    public async Task<IReadOnlyList<JsonNode>> ListAsync(string id, string api, CancellationToken ct)
    {
        var items = new List<JsonNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? url = Url(id, api);
        while (url is not null)
        {
            if (!seen.Add(url) || seen.Count > 500) throw new InvalidOperationException("ARM paging did not terminate.");
            var (status, body) = await SendRawAsync(HttpMethod.Get, url, null, null, ct);
            var page = Check("GET", status, body) as JsonObject;
            if (page?["value"] is not JsonArray values) throw new InvalidOperationException("ARM returned an incomplete list.");
            items.AddRange(values.Select(node => node ?? throw new InvalidOperationException("ARM returned a null list entry.")));
            var next = page["nextLink"] is JsonValue link && link.TryGetValue<string>(out var text) ? text : null;
            if (next is not null && !next.StartsWith($"{Origin}{Scope}", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("ARM continuation left the approved subscription.");
            }

            url = next;
        }

        return items;
    }

    private async Task<(HttpStatusCode Status, string Body)> SendRawAsync(HttpMethod method, string url, JsonNode? payload, string? ifMatch, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        var token = await credential.GetTokenAsync(new TokenRequestContext([$"{Origin}/.default"]), ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (payload is not null) request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private static JsonNode? Check(string method, HttpStatusCode status, string body)
    {
        // Provider error text can carry identifiers or signed values; only the status is surfaced.
        if ((int)status is < 200 or >= 300) throw new HttpRequestException($"ARM {method} failed (HTTP {(int)status}).", null, status);
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            return JsonNode.Parse(body);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new InvalidOperationException("ARM returned an unreadable response.");
        }
    }
}

/// <summary>Control state/audit in the control-region container and read-only release data from each region; every container already exists.</summary>
public sealed class BlobStudioStore(StudioFoundation foundation, TokenCredential credential) : IStudioControlStore, IStudioReleaseReader
{
    private const string StatePath = "operations/state.json";
    private const string SettingsPath = "operations/settings.json";

    private BlobContainerClient Container(string region, bool control)
    {
        var value = foundation.Regions[control ? foundation.ControlRegion : region];
        var account = value.StorageAccountId[(value.StorageAccountId.LastIndexOf('/') + 1)..];
        return new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), credential)
            .GetBlobContainerClient(control ? value.ControlContainer : value.ReleaseContainer);
    }

    public Task<StoredState?> ReadStateAsync(CancellationToken ct) => ReadDocumentAsync(StatePath, ct);

    private async Task<StoredState?> ReadDocumentAsync(string path, CancellationToken ct)
    {
        try
        {
            var result = (await Container(foundation.ControlRegion, true).GetBlobClient(path).DownloadContentAsync(ct)).Value;
            return new StoredState(result.Content.ToString(), result.Details.ETag.ToString());
        }
        catch (RequestFailedException error) when (error.Status == 404)
        {
            return null;
        }
    }

    public Task<string> WriteStateAsync(string json, string? etag, CancellationToken ct) => WriteAsync(StatePath, json, etag, ct);

    public Task<StoredState?> ReadSettingsAsync(CancellationToken ct) => ReadDocumentAsync(SettingsPath, ct);

    public Task<string> WriteSettingsAsync(string json, string? etag, CancellationToken ct) => WriteAsync(SettingsPath, json, etag, ct);

    public async Task AppendAuditAsync(string name, string json, CancellationToken ct) => await WriteAsync(name, json, null, ct);

    private async Task<string> WriteAsync(string path, string json, string? etag, CancellationToken ct)
    {
        var conditions = etag is null ? new BlobRequestConditions { IfNoneMatch = ETag.All } : new BlobRequestConditions { IfMatch = new ETag(etag) };
        try
        {
            var response = await Container(foundation.ControlRegion, true).GetBlobClient(path)
                .UploadAsync(BinaryData.FromString(json), new BlobUploadOptions { Conditions = conditions, HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" } }, ct);
            return response.Value.ETag.ToString();
        }
        catch (RequestFailedException error) when (error.Status is 409 or 412)
        {
            throw new StudioStateConflictException();
        }
    }

    public async Task<string?> ReadAsync(string region, string path, CancellationToken ct)
    {
        try
        {
            return (await Container(region, false).GetBlobClient(path).DownloadContentAsync(ct)).Value.Content.ToString();
        }
        catch (RequestFailedException error) when (error.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> ListAsync(string region, string prefix, CancellationToken ct)
    {
        var names = new List<string>();
        await foreach (var item in Container(region, false).GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct)) names.Add(item.Name);
        return names;
    }
}

public sealed class RetailPriceClient(HttpClient http) : IStudioPrices
{
    private const string Origin = "https://prices.azure.com/";

    public async Task<IReadOnlyList<JsonNode>> ListAsync(string vmSize, string region, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"armSkuName eq '{vmSize}' and armRegionName eq '{region}' and priceType eq 'Consumption'");
        string? url = $"{Origin}api/retail/prices?currencyCode='USD'&$filter={filter}";
        var items = new List<JsonNode>();
        for (var page = 0; url is not null; page++)
        {
            if (page >= 50 || !url.StartsWith(Origin, StringComparison.Ordinal)) throw new InvalidOperationException("The retail price pages were not an approved bounded sequence.");
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Retail price request failed (HTTP {(int)response.StatusCode}).", null, response.StatusCode);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject ?? throw new InvalidOperationException("Retail prices were unreadable.");
            items.AddRange((body["Items"] as JsonArray ?? throw new InvalidOperationException("Retail prices were incomplete.")).Select(node => node!));
            url = body["NextPageLink"] is JsonValue next && next.TryGetValue<string>(out var text) ? text : null;
        }

        return items;
    }
}

/// <summary>Private DNS and TCP reachability from the host's own VNet integration.</summary>
public sealed class PrivateNetworkProbe : IStudioConnectivity
{
    public async Task AssertPrivateAsync(string host, CancellationToken ct)
    {
        var addresses = (await Dns.GetHostAddressesAsync(host, ct)).Where(address => address.AddressFamily == AddressFamily.InterNetwork).ToList();
        if (addresses.Count == 0 || addresses.Any(address => !IsPrivate(address.GetAddressBytes())))
        {
            throw new VideoSafetyStudio.Domain.ConflictException($"Private DNS or routing is required: {host} did not resolve only to private addresses.");
        }

        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await client.ConnectAsync(host, 443, timeout.Token);
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new VideoSafetyStudio.Domain.ConflictException($"Private endpoint {host} is not reachable on TCP 443.");
        }
    }

    internal static bool IsPrivate(byte[] address) => address[0] == 10 ||
        (address[0] == 172 && address[1] is >= 16 and <= 31) || (address[0] == 192 && address[1] == 168);
}
