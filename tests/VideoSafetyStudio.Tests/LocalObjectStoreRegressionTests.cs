using System.Buffers;
using System.Text;
using VideoSafetyStudio.Infrastructure.Storage;

namespace VideoSafetyStudio.Tests;

public sealed class LocalObjectStoreRegressionTests : IDisposable
{
    private readonly string _directory = Path.GetFullPath(Path.Combine(".artifacts", "storage-regression", Guid.NewGuid().ToString("N")));

    public LocalObjectStoreRegressionTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_PreCancelled_DoesNotChangeFilesOrCreateDirectories(bool existing)
    {
        var store = new LocalFolderObjectStore(Path.Combine(_directory, "store"));
        if (existing)
        {
            await store.WriteAsync("nested/document.json", "old"u8.ToArray());
        }

        var before = Entries();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.WriteAsync("nested/document.json", "new"u8.ToArray(), ct: cancellation.Token));

        Assert.Equal(before, Entries());
        if (existing)
        {
            Assert.Equal("old"u8.ToArray(), await store.ReadAsync("nested/document.json"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_FailedWrite_PreservesCommittedBytesAndCleansStaging(bool existing)
    {
        var store = new LocalFolderObjectStore(_directory);
        if (existing)
        {
            await store.WriteAsync("document.json", "old"u8.ToArray());
        }

        using var data = new CallbackMemory("replacement"u8.ToArray(), () => throw new IOException("Controlled write failure."));

        await Assert.ThrowsAsync<IOException>(() => store.WriteAsync("document.json", data.Data));

        Assert.Equal(existing ? "old"u8.ToArray() : null, await store.ReadAsync("document.json"));
        Assert.Equal(existing ? ["document.json"] : Array.Empty<string>(), RelativeFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_CancelledWrite_PreservesCommittedBytesAndCleansStaging(bool existing)
    {
        var store = new LocalFolderObjectStore(_directory);
        if (existing)
        {
            await store.WriteAsync("document.json", "old"u8.ToArray());
        }

        using var cancellation = new CancellationTokenSource();
        using var data = new CallbackMemory("replacement"u8.ToArray(), cancellation.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.WriteAsync("document.json", data.Data, ct: cancellation.Token));

        Assert.Equal(existing ? "old"u8.ToArray() : null, await store.ReadAsync("document.json"));
        Assert.Equal(existing ? ["document.json"] : Array.Empty<string>(), RelativeFiles());
    }

    [Fact]
    public async Task WriteAsync_PendingReplacement_HidesStagingAndAllowsOpenReadersDuringPublication()
    {
        var store = new LocalFolderObjectStore(_directory);
        await store.WriteAsync("docs/document.json", "old"u8.ToArray());
        await using var oldReader = await store.OpenReadAsync("docs/document.json");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var data = new CallbackMemory("replacement"u8.ToArray(), () =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The test did not release the staged write.");
            }
        });
        var write = Task.Run(() => store.WriteAsync("docs/document.json", data.Data));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, Directory.GetFiles(Path.Combine(_directory, "docs")).Length);
            Assert.Equal(["docs/document.json"], await store.ListAsync("docs"));
            Assert.Equal(["docs/document.json"], await store.ListAsync(""));
            Assert.Equal("old"u8.ToArray(), await store.ReadAsync("docs/document.json"));
        }
        finally
        {
            release.Set();
            await write;
        }

        Assert.NotNull(oldReader);
        using var reader = new StreamReader(oldReader);
        Assert.Equal("old", await reader.ReadToEndAsync());
        Assert.Equal("replacement"u8.ToArray(), await store.ReadAsync("docs/document.json"));
        Assert.Equal(["docs/document.json"], RelativeFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_ConcurrentReaders_ObserveOnlyCompleteDocuments(bool separateInstances)
    {
        var store = new LocalFolderObjectStore(_directory);
        var first = Encoding.UTF8.GetBytes("{\"value\":\"" + new string('a', 32768) + "\"}");
        var second = Encoding.UTF8.GetBytes("{\"value\":\"" + new string('b', 32768) + "\"}");
        await store.WriteAsync("document.json", first);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readers = Enumerable.Range(0, 4).Select(async _ =>
        {
            var readerStore = separateInstances ? new LocalFolderObjectStore(_directory) : store;
            await start.Task;
            for (var iteration = 0; iteration < 60; iteration++)
            {
                var bytes = await readerStore.ReadAsync("document.json");
                Assert.NotNull(bytes);
                Assert.True(bytes.SequenceEqual(first) || bytes.SequenceEqual(second), "A reader observed an incomplete document.");
            }
        }).ToArray();
        var writer = Task.Run(async () =>
        {
            await start.Task;
            for (var iteration = 0; iteration < 40; iteration++)
            {
                await store.WriteAsync("document.json", iteration % 2 == 0 ? second : first);
            }
        });
        start.SetResult();

        await Task.WhenAll(readers.Append(writer));

        Assert.Equal(["document.json"], RelativeFiles());
    }

    [Fact]
    public async Task WriteAsync_UnicodeAndLongPaths_KeepOldReadersAndPublishNewBytes()
    {
        var store = new LocalFolderObjectStore(_directory);
        var key = string.Join("/", Enumerable.Repeat(new string('d', 70), 4)) + "/résumé-文.json";
        await store.WriteAsync(key, "old"u8.ToArray());
        await using var oldReader = await store.OpenReadAsync(key);

        await store.WriteAsync(key, "new"u8.ToArray());

        Assert.Equal("new"u8.ToArray(), await store.ReadAsync(key));
        Assert.NotNull(oldReader);
        using var reader = new StreamReader(oldReader);
        Assert.Equal("old", await reader.ReadToEndAsync());
        Assert.Equal([key], await store.ListAsync(""));
    }

    [Fact]
    public async Task WriteAsync_EmptyNoOverwrite_ReservesOnceAcrossInstances()
    {
        var first = new LocalFolderObjectStore(_directory);
        var second = new LocalFolderObjectStore(_directory);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new[] { first, second }.Select(store => Task.Run(async () =>
        {
            await start.Task;
            try
            {
                await store.WriteAsync(".reserved", ReadOnlyMemory<byte>.Empty, overwrite: false);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        })).ToArray();
        start.SetResult();

        var results = await Task.WhenAll(tasks);

        Assert.Single(results, success => success);
        Assert.Equal(Array.Empty<byte>(), await first.ReadAsync(".reserved"));
        Assert.Equal([".reserved"], RelativeFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoOverwrite_CompetingWriters_HaveExactlyOneWinnerAndCleanStaging(bool upload)
    {
        var store = new LocalFolderObjectStore(Path.Combine(_directory, "store"));
        var sources = Path.Combine(_directory, "sources");
        Directory.CreateDirectory(sources);
        var candidates = Enumerable.Range(0, 12).Select(index => Encoding.UTF8.GetBytes($"candidate-{index}")).ToArray();
        for (var index = 0; index < candidates.Length; index++)
        {
            await File.WriteAllBytesAsync(Path.Combine(sources, index.ToString()), candidates[index]);
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = candidates.Select((data, index) => Task.Run(async () =>
        {
            await start.Task;
            try
            {
                if (upload)
                {
                    await store.UploadAsync("docs/winner", Path.Combine(sources, index.ToString()));
                }
                else
                {
                    await store.WriteAsync("docs/winner", data, overwrite: false);
                }

                return index;
            }
            catch (IOException)
            {
                return -1;
            }
        })).ToArray();
        start.SetResult();

        var results = await Task.WhenAll(writes);
        var winner = Assert.Single(results, index => index >= 0);
        Assert.Equal(candidates[winner], await store.ReadAsync("docs/winner"));
        Assert.Equal(["docs/winner"], await store.ListAsync(""));
        Assert.Single(Directory.GetFiles(store.Root, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CopyAsync_PreCancelled_DoesNotChangeDestinationOrCreateDirectories(bool upload, bool existing)
    {
        var store = new LocalFolderObjectStore(Path.Combine(_directory, "store"));
        await store.WriteAsync("source", "new"u8.ToArray());
        var input = Path.Combine(_directory, "input");
        await File.WriteAllBytesAsync(input, "new"u8.ToArray());
        var output = Path.Combine(_directory, "output");
        if (existing)
        {
            await store.WriteAsync("nested/destination", "old"u8.ToArray());
            await File.WriteAllBytesAsync(output, "old"u8.ToArray());
        }

        var before = Entries();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload
            ? store.UploadAsync("nested/destination", input, overwrite: true, ct: cancellation.Token)
            : store.DownloadAsync("source", output, cancellation.Token));

        Assert.Equal(before, Entries());
        if (existing)
        {
            Assert.Equal("old"u8.ToArray(), await store.ReadAsync("nested/destination"));
            Assert.Equal("old"u8.ToArray(), await File.ReadAllBytesAsync(output));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CopyAsync_CancelledAfterStagingProgress_DoesNotPublishPartialContent(bool upload, bool existing)
    {
        var store = new LocalFolderObjectStore(Path.Combine(_directory, "store"));
        Directory.CreateDirectory(store.Root);
        if (upload && existing)
        {
            await store.WriteAsync("destination", "committed"u8.ToArray());
        }

        var source = upload ? Path.Combine(_directory, "source") : Path.Combine(store.Root, "source");
        const long sourceLength = 256L * 1024 * 1024;
        await using (var input = new FileStream(source, FileMode.CreateNew, FileAccess.Write))
        {
            input.SetLength(sourceLength);
        }

        var destination = upload ? Path.Combine(store.Root, "destination") : Path.Combine(_directory, "destination");
        if (!upload && existing)
        {
            await File.WriteAllBytesAsync(destination, "committed"u8.ToArray());
        }

        using var cancellation = new CancellationTokenSource();
        using var observerReady = new ManualResetEventSlim();
        using var observerStopped = new ManualResetEventSlim();
        long stagedBytes = 0;
        Exception? observerError = null;
        var observer = new Thread(() =>
        {
            observerReady.Set();
            try
            {
                while (!observerStopped.IsSet)
                {
                    foreach (var file in Directory.GetFiles(Path.GetDirectoryName(destination)!, ".objectstore-*.tmp"))
                    {
                        var length = new FileInfo(file).Length;
                        if (length > 0)
                        {
                            stagedBytes = length;
                            cancellation.Cancel();
                            return;
                        }
                    }

                    Thread.Sleep(1);
                }
            }
            catch (Exception error)
            {
                observerError = error;
            }
        }) { IsBackground = true };
        observer.Start();
        observerReady.Wait();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload
                ? store.UploadAsync("destination", source, overwrite: true, ct: cancellation.Token)
                : store.DownloadAsync("source", destination, cancellation.Token));
        }
        finally
        {
            observerStopped.Set();
            Assert.True(observer.Join(TimeSpan.FromSeconds(10)), "The staging observer did not exit.");
        }

        Assert.Null(observerError);
        Assert.InRange(stagedBytes, 1, sourceLength - 1);
        if (existing)
        {
            Assert.Equal("committed"u8.ToArray(), await File.ReadAllBytesAsync(destination));
        }
        else
        {
            Assert.False(File.Exists(destination));
        }
        Assert.Empty(Directory.GetFiles(_directory, ".objectstore-*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CopyAsync_SuccessfulUploadAndDownload_PublishCompleteBytesAndCleanStaging()
    {
        var store = new LocalFolderObjectStore(Path.Combine(_directory, "store"));
        var bytes = Enumerable.Range(0, 300000).Select(index => (byte)index).ToArray();
        var input = Path.Combine(_directory, "input");
        var output = Path.Combine(_directory, "output");
        await File.WriteAllBytesAsync(input, bytes);
        await File.WriteAllBytesAsync(output, "old"u8.ToArray());
        await store.WriteAsync("nested/data", "old"u8.ToArray());

        await store.UploadAsync("nested/data", input, overwrite: true);
        await store.DownloadAsync("nested/data", output);

        Assert.Equal(bytes, await store.ReadAsync("nested/data"));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(output));
        Assert.Empty(Directory.GetFiles(_directory, ".objectstore-*.tmp", SearchOption.AllDirectories));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string[] Entries() => Directory.GetFileSystemEntries(_directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

    private string[] RelativeFiles() => Directory.GetFiles(_directory, "*", SearchOption.AllDirectories)
        .Select(file => Path.GetRelativePath(_directory, file).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();

    private sealed class CallbackMemory(byte[] bytes, Action callback) : MemoryManager<byte>
    {
        private int _called;

        public ReadOnlyMemory<byte> Data => CreateMemory(bytes.Length);

        public override Span<byte> GetSpan()
        {
            InvokeCallback();
            return bytes;
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            InvokeCallback();
            return bytes.AsMemory(elementIndex).Pin();
        }

        public override void Unpin() { }

        protected override void Dispose(bool disposing) { }

        private void InvokeCallback()
        {
            if (Interlocked.Exchange(ref _called, 1) == 0)
            {
                callback();
            }
        }
    }
}
