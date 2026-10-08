using System.Text.RegularExpressions;
using VideoSafetyStudio.Application.Generation;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Domain.Generation;

namespace VideoSafetyStudio.Infrastructure.AzureMl;

public static class AzureMlStatus
{
    public static bool IsTerminal(string status) => status is "Completed" or "Failed" or "Canceled" or "Cancelled" or "NotResponding";

    /// <summary>Azure ML states -> the studio's: waiting for the node = Queued, executing = Running.</summary>
    public static string ToJobStatus(string status) => status switch
    {
        "Completed" => JobStatus.Completed,
        "Failed" or "Canceled" or "Cancelled" or "NotResponding" => JobStatus.Failed,
        "Running" or "Finalizing" or "CancelRequested" => JobStatus.Running,
        _ => JobStatus.Queued, // NotStarted, Queued, Starting, Preparing, Provisioning
    };

    public static bool IsRunning(string status) => status is "Running" or "Finalizing";
}

public static class AzureMlOutput
{
    private static readonly string[] Extensions = [".mp4", ".webm", ".mov", ".webp"];

    /// <summary>The video to show: prefers .mp4, then .webm, .mov, .webp, and avoids names containing "preview". Null when there is none.</summary>
    public static string? PickVideo(IEnumerable<string> names) => names
        .Where(name => Array.IndexOf(Extensions, Path.GetExtension(name).ToLowerInvariant()) >= 0)
        .OrderBy(name => name.Contains("preview", StringComparison.OrdinalIgnoreCase))
        .ThenBy(name => Array.IndexOf(Extensions, Path.GetExtension(name).ToLowerInvariant()))
        .ThenBy(name => name, StringComparer.Ordinal)
        .FirstOrDefault();
}

public static partial class AzureMlImage
{
    public const long MaxUploadBytes = 64L * 1024 * 1024;

    [GeneratedRegex("[^A-Za-z0-9._-]")]
    private static partial Regex Unsafe();

    /// <summary>Letters, digits, dot, underscore and dash only; at most 120 characters; .png when there is no extension.</summary>
    public static string SafeName(string name)
    {
        var clean = Unsafe().Replace(Path.GetFileName(name ?? string.Empty), "-").Trim('.', '-');
        if (clean.Length > 120)
        {
            clean = clean[..120];
        }

        if (clean.Length == 0)
        {
            return "input.png";
        }

        return Path.GetExtension(clean).Length == 0 ? clean[..Math.Min(clean.Length, 116)] + ".png" : clean;
    }

    /// <summary>Accepts only data that is really PNG, JPEG or WebP.</summary>
    public static bool IsSupported(ReadOnlySpan<byte> data) =>
        data.Length > 12 && (
            data[..8].SequenceEqual((byte[])[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) ||
            data[..3].SequenceEqual((byte[])[0xFF, 0xD8, 0xFF]) ||
            (data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8)));

    public static void Check(IEnumerable<SceneImage?> images)
    {
        long total = 0;
        foreach (var image in images.Where(i => i is not null))
        {
            if (!IsSupported(image!.Data))
            {
                throw new ValidationException($"{image.Name} is not a PNG, JPEG or WebP image.");
            }

            total += image.Data.Length;
        }

        if (total > MaxUploadBytes)
        {
            throw new ValidationException("Images are larger than 64 MB in total.");
        }
    }
}
