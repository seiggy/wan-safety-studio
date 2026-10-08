using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using VideoSafetyStudio.Application.Abstractions;

namespace VideoSafetyStudio.Infrastructure.Storage;

public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>local (folder or mounted share) | azurite | azure</summary>
    public string Backend { get; set; } = "local";

    public string? Path { get; set; }
    public string? Account { get; set; }
    public string Container { get; set; } = "studio";
    public string? Connection { get; set; }
}

internal static class StoreKey
{
    public static string Check(string key)
    {
        var parts = key.Split('/');
        if (key.Length == 0 || key.StartsWith('/') || key.Contains('\\') || parts.Any(p => p is "" or "." or ".."))
        {
            throw new ArgumentException("Invalid storage key.", nameof(key));
        }

        return key;
    }
}

/// <summary>A directory on disk; a NFS/SMB share is just a mounted path.</summary>
public sealed class LocalFolderObjectStore(string root) : IObjectStore
{
    public string Root { get; } = System.IO.Path.GetFullPath(root);

    private string PathOf(string key) => System.IO.Path.Combine(Root, StoreKey.Check(key).Replace('/', System.IO.Path.DirectorySeparatorChar));

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(File.Exists(PathOf(key)));

    public async Task<byte[]?> ReadAsync(string key, CancellationToken ct = default)
    {
        var path = PathOf(key);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
    }

    public async Task WriteAsync(string key, ReadOnlyMemory<byte> data, bool overwrite = true, CancellationToken ct = default)
    {
        var path = PathOf(key);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write);
        await stream.WriteAsync(data, ct);
    }

    public Task UploadAsync(string key, string path, bool overwrite = false, CancellationToken ct = default)
    {
        var target = PathOf(key);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        if (File.Exists(target) && !overwrite)
        {
            throw new IOException($"{key} already exists.");
        }

        File.Copy(path, target, overwrite);
        return Task.CompletedTask;
    }

    public Task DownloadAsync(string key, string path, CancellationToken ct = default)
    {
        File.Copy(PathOf(key), path, overwrite: true);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default)
    {
        var directory = PathOf(prefix.TrimEnd('/'));
        IReadOnlyList<string> keys = Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(file => System.IO.Path.GetRelativePath(Root, file).Replace('\\', '/')).Order(StringComparer.Ordinal)]
            : [];
        return Task.FromResult(keys);
    }
}

/// <summary>Azure Blob container (Azurite or Azure Storage). The container is created on first use.</summary>
public sealed class BlobObjectStore(BlobServiceClient service, string container) : IObjectStore
{
    private readonly SemaphoreSlim _init = new(1, 1);
    private BlobContainerClient? _client;

    private async Task<BlobContainerClient> ContainerAsync(CancellationToken ct)
    {
        if (_client is not null)
        {
            return _client;
        }

        await _init.WaitAsync(ct);
        try
        {
            var client = service.GetBlobContainerClient(container);
            await client.CreateIfNotExistsAsync(cancellationToken: ct);
            return _client = client;
        }
        finally
        {
            _init.Release();
        }
    }

    private async Task<BlobClient> BlobAsync(string key, CancellationToken ct) =>
        (await ContainerAsync(ct)).GetBlobClient(StoreKey.Check(key));

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default) =>
        (await (await BlobAsync(key, ct)).ExistsAsync(ct)).Value;

    public async Task<byte[]?> ReadAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var result = await (await BlobAsync(key, ct)).DownloadContentAsync(ct);
            return result.Value.Content.ToArray();
        }
        catch (RequestFailedException error) when (error.Status == 404)
        {
            return null;
        }
    }

    public async Task WriteAsync(string key, ReadOnlyMemory<byte> data, bool overwrite = true, CancellationToken ct = default)
    {
        try
        {
            await (await BlobAsync(key, ct)).UploadAsync(BinaryData.FromBytes(data), overwrite, ct);
        }
        catch (RequestFailedException error) when (error.Status == 409)
        {
            throw new IOException($"{key} already exists.", error);
        }
    }

    public async Task UploadAsync(string key, string path, bool overwrite = false, CancellationToken ct = default)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            await (await BlobAsync(key, ct)).UploadAsync(stream, overwrite, ct);
        }
        catch (RequestFailedException error) when (error.Status == 409)
        {
            throw new IOException($"{key} already exists.", error);
        }
    }

    public async Task DownloadAsync(string key, string path, CancellationToken ct = default)
    {
        await using var stream = File.Create(path);
        await (await BlobAsync(key, ct)).DownloadToAsync(stream, ct);
    }

    public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default)
    {
        var client = await ContainerAsync(ct);
        var names = new List<string>();
        await foreach (var blob in client.GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, prefix.TrimEnd('/') + "/", ct))
        {
            names.Add(blob.Name);
        }

        return [.. names.Order(StringComparer.Ordinal)];
    }
}

public static class ObjectStoreFactory
{
    /// <summary>Picks the store from configuration. Azure is always the choice when deployed; local/azurite are for dev.</summary>
    public static (IObjectStore Store, string Label) Create(StorageOptions options, string defaultFolder)
    {
        switch (options.Backend.ToLowerInvariant())
        {
            case "local":
                var root = string.IsNullOrWhiteSpace(options.Path) ? defaultFolder : options.Path;
                Directory.CreateDirectory(root);
                return (new LocalFolderObjectStore(root), $"folder {root}");
            case "azurite":
                if (string.IsNullOrWhiteSpace(options.Connection))
                {
                    throw new InvalidOperationException("Azurite storage needs ConnectionStrings:blobs (set by the AppHost) or Storage:Connection.");
                }

                return (new BlobObjectStore(new BlobServiceClient(options.Connection), options.Container), $"Azurite/{options.Container}");
            case "azure":
                if (string.IsNullOrWhiteSpace(options.Account))
                {
                    throw new InvalidOperationException("Azure storage needs Storage:Account.");
                }

                var service = new BlobServiceClient(new Uri($"https://{options.Account}.blob.core.windows.net"), new DefaultAzureCredential());
                return (new BlobObjectStore(service, options.Container), $"Azure Storage {options.Account}/{options.Container}");
            default:
                throw new InvalidOperationException("Storage:Backend must be local, azurite or azure.");
        }
    }
}
