using System.ComponentModel;
using System.Runtime.InteropServices;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Win32.SafeHandles;
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

    /// <summary>False for a container someone else provisioned (the Azure ML datastore): the identity may only have data access.</summary>
    public bool CreateContainer { get; set; } = true;
}

internal static class StoreKey
{
    public static string Check(string key)
    {
        var parts = key.Split('/');
        if (key.Length == 0 || key.StartsWith('/') || key.Contains('\\') || key.Contains(':') || parts.Any(p => p is "" or "." or ".."))
        {
            throw new ArgumentException("Invalid storage key.", nameof(key));
        }

        return key;
    }

    public static string CheckPrefix(string prefix) => prefix.Length == 0 ? string.Empty : Check(prefix.TrimEnd('/'));
}

/// <summary>A directory on disk; a NFS/SMB share is just a mounted path.</summary>
public sealed class LocalFolderObjectStore(string root) : IObjectStore
{
    private const string TemporaryPrefix = ".objectstore-";
    private const string TemporarySuffix = ".tmp";

    public string Root { get; } = System.IO.Path.GetFullPath(root);

    private string PathOf(string key) => System.IO.Path.Combine(Root, StoreKey.Check(key).Replace('/', System.IO.Path.DirectorySeparatorChar));

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(File.Exists(PathOf(key)));

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = PathOf(key);
        try
        {
            return Task.FromResult<Stream?>(OpenFile(path));
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
        catch (DirectoryNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    public async Task<byte[]?> ReadAsync(string key, CancellationToken ct = default)
    {
        await using var stream = await OpenReadAsync(key, ct);
        if (stream is null)
        {
            return null;
        }

        using var data = new MemoryStream();
        await stream.CopyToAsync(data, ct);
        return data.ToArray();
    }

    public async Task WriteAsync(string key, ReadOnlyMemory<byte> data, bool overwrite = true, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = PathOf(key);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await PublishAsync(path, (stream, token) => stream.WriteAsync(data, token).AsTask(), overwrite, ct);
    }

    public async Task UploadAsync(string key, string path, bool overwrite = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var target = PathOf(key);
        await using var source = OpenFile(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        await PublishAsync(target, (stream, token) => source.CopyToAsync(stream, token), overwrite, ct);
    }

    public async Task DownloadAsync(string key, string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var source = OpenFile(PathOf(key));
        await PublishAsync(System.IO.Path.GetFullPath(path), (stream, token) => source.CopyToAsync(stream, token), overwrite: true, ct);
    }

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default)
    {
        var normalized = StoreKey.CheckPrefix(prefix);
        var directory = normalized.Length == 0 ? Root : PathOf(normalized);
        IReadOnlyList<string> keys = Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(file => !IsTemporaryFile(file))
                .Select(file => System.IO.Path.GetRelativePath(Root, file).Replace('\\', '/')).Order(StringComparer.Ordinal)]
            : [];
        return Task.FromResult(keys);
    }

    private static FileStream OpenFile(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static bool IsTemporaryFile(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        return name.StartsWith(TemporaryPrefix, StringComparison.Ordinal) &&
            name.EndsWith(TemporarySuffix, StringComparison.Ordinal) &&
            Guid.TryParseExact(name.AsSpan(TemporaryPrefix.Length, name.Length - TemporaryPrefix.Length - TemporarySuffix.Length), "N", out _);
    }

    private static async Task PublishAsync(string path, Func<Stream, CancellationToken, Task> write, bool overwrite, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, $"{TemporaryPrefix}{Guid.NewGuid():N}{TemporarySuffix}");
        var created = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                created = true;
                await write(stream, ct);
                await stream.FlushAsync(ct);
            }

            ct.ThrowIfCancellationRequested();
            LocalFilePublish.Move(temporary, path, overwrite);
        }
        finally
        {
            if (created)
            {
                File.Delete(temporary);
            }
        }
    }
}

internal static partial class LocalFilePublish
{
    private const uint DeleteAccess = 0x00010000;
    private const uint OpenExisting = 3;
    private const uint ReplaceIfExists = 0x00000001;
    private const uint PosixSemantics = 0x00000002;
    private const int FileRenameInfoEx = 22;

    public static unsafe void Move(string source, string destination, bool overwrite)
    {
        if (!OperatingSystem.IsWindows() || !overwrite)
        {
            File.Move(source, destination, overwrite);
            return;
        }

        // ReplaceFile exposes a missing-name/sharing window; POSIX rename keeps old reader handles valid.
        using var handle = CreateFile(source, DeleteAccess, FileShare.ReadWrite | FileShare.Delete, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            throw new IOException("Could not open the staged file for atomic publication.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        var nameOffset = Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FileName)).ToInt32();
        var nameLength = checked(destination.Length * sizeof(char));
        var buffer = new byte[nameOffset + nameLength];
        MemoryMarshal.AsBytes(destination.AsSpan()).CopyTo(buffer.AsSpan(nameOffset));
        fixed (byte* pointer = buffer)
        {
            var information = (RenameInformation*)pointer;
            information->Flags = ReplaceIfExists | PosixSemantics;
            information->FileNameLength = (uint)nameLength;
            if (!SetFileInformationByHandle(handle, FileRenameInfoEx, pointer, (uint)buffer.Length))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error is 1 or 50 or 87)
                {
                    // Older filesystem drivers can still rename atomically, or fail without touching the destination.
                    handle.Dispose();
                    File.Move(source, destination, overwrite: true);
                    return;
                }

                throw new IOException("The filesystem could not atomically publish the staged file.", new Win32Exception(error));
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RenameInformation
    {
        public uint Flags;
        public nint RootDirectory;
        public uint FileNameLength;
        public char FileName;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string name, uint access, FileShare share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, byte* information, uint size);
}

/// <summary>Azure Blob container (Azurite or Azure Storage). The container is created on first use.</summary>
public sealed class BlobObjectStore(BlobServiceClient service, string container, bool createContainer = true) : IObjectStore
{
    private readonly SemaphoreSlim _init = new(1, 1);
    private volatile BlobContainerClient? _client;

    private async Task<BlobContainerClient> ContainerAsync(CancellationToken ct)
    {
        if (_client is not null)
        {
            return _client;
        }

        await _init.WaitAsync(ct);
        try
        {
            if (_client is not null)
            {
                return _client;
            }

            var client = service.GetBlobContainerClient(container);
            if (createContainer)
            {
                await client.CreateIfNotExistsAsync(cancellationToken: ct);
            }

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

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        try
        {
            return await (await BlobAsync(key, ct)).OpenReadAsync(cancellationToken: ct);
        }
        catch (RequestFailedException error) when (error.Status == 404)
        {
            return null;
        }
    }

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
        var normalized = StoreKey.CheckPrefix(prefix);
        var client = await ContainerAsync(ct);
        var names = new List<string>();
        await foreach (var blob in client.GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, normalized.Length == 0 ? string.Empty : normalized + "/", ct))
        {
            names.Add(blob.Name);
        }

        return [.. names.Order(StringComparer.Ordinal)];
    }
}

public static class ObjectStoreFactory
{
    /// <summary>Picks the store from configuration. Azure is always the choice when deployed; local/azurite are for dev.</summary>
    public static (IObjectStore Store, string Label) Create(StorageOptions options, string defaultFolder, Azure.Core.TokenCredential? credential = null)
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

                var service = new BlobServiceClient(new Uri($"https://{options.Account}.blob.core.windows.net"), credential ?? new DefaultAzureCredential());
                return (new BlobObjectStore(service, options.Container, options.CreateContainer), $"Azure Storage {options.Account}/{options.Container}");
            default:
                throw new InvalidOperationException("Storage:Backend must be local, azurite or azure.");
        }
    }
}
