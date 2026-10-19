using System.Text.RegularExpressions;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Domain;

namespace VideoSafetyStudio.Application.Generation;

/// <summary>
/// Which library videos the team has archived. One shared document in the object store, so every creator and instance sees the same list.
/// Writes are serialized per process; two server instances archiving at the same moment can still drop one change.
/// </summary>
public sealed partial class ArchiveService(IObjectStore store)
{
    public const string Key = "portal-state/archived-videos.json";

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return [.. await ReadAsync(ct)];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> SetAsync(string? jobName, bool archived, CancellationToken ct = default)
    {
        if (jobName is null || !JobNamePattern().IsMatch(jobName))
        {
            throw new ValidationException("Send the job name of a video.");
        }

        await _gate.WaitAsync(ct);
        try
        {
            var names = await ReadAsync(ct);
            var changed = archived ? names.Add(jobName) : names.Remove(jobName);
            if (changed)
            {
                await store.WriteAsync(Key, Json.ToBytes(names.Order(StringComparer.Ordinal)), true, ct);
            }

            return [.. names];
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SortedSet<string>> ReadAsync(CancellationToken ct)
    {
        var data = await store.ReadAsync(Key, ct);
        return new SortedSet<string>(data is null ? [] : Json.From<List<string>>(data) ?? [], StringComparer.Ordinal);
    }

    [GeneratedRegex(@"\A[\w.-]{1,200}\z")]
    private static partial Regex JobNamePattern();
}
