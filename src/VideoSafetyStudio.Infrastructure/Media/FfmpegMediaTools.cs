using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Domain.Vision;

namespace VideoSafetyStudio.Infrastructure.Media;

/// <summary>ffmpeg/ffprobe. Imported media is untrusted: bounded duration, size and frame count.</summary>
public sealed class FfmpegMediaTools : IMediaTools
{
    private const int MaxSeconds = 600;
    private const int MaxSide = 4096;

    public static IReadOnlyList<string> ProbeArguments(string video) =>
        ["-v", "error", "-protocol_whitelist", "file", "-select_streams", "v:0", "-show_entries", "stream=width,height,avg_frame_rate:format=duration", "-of", "json", video];

    public static IReadOnlyList<string> ExtractArguments(string video, string outDirectory, double fps, int limit) =>
        // Keep coded geometry, matching ffprobe's width/height even when a display rotation is present.
        ["-nostdin", "-v", "error", "-protocol_whitelist", "file", "-noautorotate", "-i", video, "-vf", $"fps={fps.ToString(CultureInfo.InvariantCulture)}", "-frames:v", limit.ToString(CultureInfo.InvariantCulture),
         "-q:v", "2", Path.Combine(outDirectory, "%06d.jpg")];

    public async Task<VideoInfo> ProbeAsync(string video, CancellationToken ct = default)
    {
        var (_, output) = await RunAsync("ffprobe", ProbeArguments(video), TimeSpan.FromSeconds(60), ct);
        try
        {
            using var document = JsonDocument.Parse(output);
            var stream = document.RootElement.GetProperty("streams")[0];
            var duration = double.Parse(document.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
            var rate = stream.GetProperty("avg_frame_rate").GetString()!.Split('/');
            var info = new VideoInfo(stream.GetProperty("width").GetInt32(), stream.GetProperty("height").GetInt32(), duration,
                double.Parse(rate[0], CultureInfo.InvariantCulture) / double.Parse(rate[1], CultureInfo.InvariantCulture));
            return duration is > 0 and <= MaxSeconds && Math.Max(info.Width, info.Height) is > 0 and <= MaxSide && double.IsFinite(info.Fps)
                ? info
                : throw new ValidationException($"Video must be under {MaxSeconds}s and {MaxSide}px per side.");
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or IndexOutOfRangeException or FormatException or InvalidOperationException)
        {
            throw new ValidationException("The file is not a readable video.");
        }
    }

    public async Task<IReadOnlyList<string>> ExtractFramesAsync(string video, string outDirectory, double fps, int limit, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outDirectory);
        var (code, _) = await RunAsync("ffmpeg", ExtractArguments(video, outDirectory, fps, limit), TimeSpan.FromMinutes(30), ct);
        return code == 0 ? [.. Directory.EnumerateFiles(outDirectory, "*.jpg").Order(StringComparer.Ordinal)] : throw new ValidationException("Frame extraction failed.");
    }

    private static async Task<(int Code, string Output)> RunAsync(string tool, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var info = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.Environment.Clear();
        info.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("SystemRoot") is { } systemRoot)
        {
            info.Environment["SystemRoot"] = systemRoot;
        }
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(info)!;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new ValidationException("ffmpeg and ffprobe are not installed on this host, so videos cannot be imported or extracted here.");
        }

        using (process)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var output = process.StandardOutput.ReadToEndAsync(cts.Token);
            var errors = process.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            await errors;
            return (process.ExitCode, await output);
        }
    }
}
