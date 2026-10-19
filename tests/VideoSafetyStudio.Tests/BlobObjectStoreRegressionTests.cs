using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using VideoSafetyStudio.Infrastructure.Storage;

namespace VideoSafetyStudio.Tests;

public sealed class BlobObjectStoreRegressionTests : IDisposable
{
    private readonly string _directory = Path.GetFullPath(Path.Combine(".artifacts", "blob-regression", Guid.NewGuid().ToString("N")));

    public BlobObjectStoreRegressionTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ContainerAsync_ConcurrentFirstCallers_PerformOneCreationCheck()
    {
        using var handler = new StorageHandler { HoldCreation = true };
        using var http = new HttpClient(handler);
        var store = CreateStore(http);
        var first = store.ExistsAsync("first");
        await handler.CreationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var queued = Enumerable.Range(0, 12).Select(index => store.ExistsAsync($"queued-{index}")).ToArray();
        try
        {
            Assert.All(queued, task => Assert.False(task.IsCompleted));
            Assert.Equal(1, handler.CreationChecks);
        }
        finally
        {
            handler.ReleaseCreation.SetResult();
        }

        var results = await Task.WhenAll(queued.Prepend(first)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(results, result => Assert.True(result));
        Assert.Equal(1, handler.CreationChecks);
        Assert.Equal(13, handler.ExistenceChecks);
    }

    [Theory]
    [InlineData("../")]
    [InlineData("../objects")]
    [InlineData("/objects")]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("C:/objects")]
    [InlineData("C:objects")]
    [InlineData(@"C:\objects")]
    [InlineData(@"\objects")]
    [InlineData("objects/../")]
    [InlineData("objects/./")]
    [InlineData("objects//child")]
    [InlineData(@"objects\")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task ListAsync_InvalidPrefixes_AreRejectedByBothStoresBeforeIo(string prefix)
    {
        using var handler = new StorageHandler();
        using var http = new HttpClient(handler);
        var blob = CreateStore(http);
        var local = new LocalFolderObjectStore(Path.Combine(_directory, "not-created"));

        var localError = await Assert.ThrowsAsync<ArgumentException>(() => local.ListAsync(prefix));
        var blobError = await Assert.ThrowsAsync<ArgumentException>(() => blob.ListAsync(prefix));

        Assert.Equal(localError.ParamName, blobError.ParamName);
        Assert.Equal(localError.Message, blobError.Message);
        Assert.Equal(0, handler.Requests);
        Assert.False(Directory.Exists(local.Root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("docs")]
    [InlineData("docs/")]
    [InlineData("docs///")]
    public async Task ListAsync_ValidPrefixes_HaveIdenticalScopeAndOrdering(string prefix)
    {
        string[] names = ["docs/z.json", "other/out.json", "docs/nested/b.json", "docs/a.json"];
        using var handler = new StorageHandler { Names = names };
        using var http = new HttpClient(handler);
        var blob = CreateStore(http, createContainer: false);
        var local = new LocalFolderObjectStore(_directory);
        foreach (var name in names)
        {
            await local.WriteAsync(name, "data"u8.ToArray());
        }

        var localNames = await local.ListAsync(prefix);
        var blobNames = await blob.ListAsync(prefix);

        var expectedPrefix = prefix.Length == 0 ? "" : "docs/";
        var expected = names.Where(name => name.StartsWith(expectedPrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, localNames);
        Assert.Equal(expected, blobNames);
        Assert.Equal(expectedPrefix, Assert.Single(handler.Prefixes));
        Assert.Equal(1, handler.Requests);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static BlobObjectStore CreateStore(HttpClient http, bool createContainer = true)
    {
        var options = new BlobClientOptions { Transport = new HttpClientTransport(http) };
        options.Retry.MaxRetries = 0;
        return new BlobObjectStore(new BlobServiceClient(new Uri("https://storage.invalid/"), options), "studio", createContainer);
    }

    private sealed class StorageHandler : HttpMessageHandler
    {
        private int _requests;
        private int _creationChecks;
        private int _existenceChecks;

        public bool HoldCreation { get; init; }
        public IReadOnlyList<string> Names { get; init; } = [];
        public TaskCompletionSource CreationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCreation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> Prefixes { get; } = new();
        public int Requests => Volatile.Read(ref _requests);
        public int CreationChecks => Volatile.Read(ref _creationChecks);
        public int ExistenceChecks => Volatile.Read(ref _existenceChecks);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            if (request.Method == HttpMethod.Put)
            {
                Interlocked.Increment(ref _creationChecks);
                CreationEntered.TrySetResult();
                if (HoldCreation)
                {
                    await ReleaseCreation.Task.WaitAsync(cancellationToken);
                }

                return Response(HttpStatusCode.Created);
            }

            if (request.Method == HttpMethod.Head)
            {
                Interlocked.Increment(ref _existenceChecks);
                return Response(HttpStatusCode.OK);
            }

            Assert.Equal(HttpMethod.Get, request.Method);
            var query = request.RequestUri!.Query.TrimStart('?').Split('&')
                .Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => part.Length == 2 ? Uri.UnescapeDataString(part[1]) : "");
            Assert.Equal("list", query["comp"]);
            var prefix = query.GetValueOrDefault("prefix", "");
            Prefixes.Enqueue(prefix);
            var xml = new XElement("EnumerationResults",
                new XAttribute("ServiceEndpoint", "https://storage.invalid/"),
                new XAttribute("ContainerName", "studio"),
                new XElement("Prefix", prefix),
                new XElement("Blobs", Names.Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(name => new XElement("Blob", new XElement("Name", name),
                        new XElement("Properties", new XElement("BlobType", "BlockBlob"), new XElement("Content-Length", 4))))),
                new XElement("NextMarker", ""));
            var response = Response(HttpStatusCode.OK);
            response.Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml");
            return response;
        }

        private static HttpResponseMessage Response(HttpStatusCode status)
        {
            var response = new HttpResponseMessage(status);
            response.Headers.Add("x-ms-request-id", Guid.NewGuid().ToString());
            response.Headers.Add("x-ms-version", "2025-11-05");
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"test\"");
            return response;
        }
    }
}
