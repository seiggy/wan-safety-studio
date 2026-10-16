using Microsoft.AspNetCore.Mvc.Testing;

namespace VideoSafetyStudio.Tests.Support;

public abstract class ReliabilityStorageFactory : WebApplicationFactory<Program>
{
    private readonly TempFolder _storage = new();
    private readonly List<IDisposable> _resources = [];

    public string StorageFolder => _storage.Path;

    protected T Own<T>(T resource) where T : IDisposable
    {
        _resources.Add(resource);
        return resource;
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            base.Dispose(disposing);
        }
        finally
        {
            if (disposing)
            {
                Cleanup();
            }
        }
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            Cleanup();
        }
    }

    private void Cleanup()
    {
        try
        {
            foreach (var resource in _resources)
            {
                resource.Dispose();
            }

            _resources.Clear();
        }
        finally
        {
            _storage.Dispose();
        }
    }
}
