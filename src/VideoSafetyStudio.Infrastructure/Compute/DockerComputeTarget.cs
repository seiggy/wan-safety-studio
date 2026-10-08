using System.Diagnostics;
using VideoSafetyStudio.Application.Abstractions;

namespace VideoSafetyStudio.Infrastructure.Compute;

/// <summary>Runs the trainer image with the local GPU (dev machine). Same image and arguments as an Azure ML job.</summary>
public sealed class DockerComputeTarget(string gpus = "all", string shmSize = "8g") : IComputeTarget
{
    public string Name => "local-docker";

    public async Task<int> RunAsync(string image, IReadOnlyList<string> args, string dataDirectory, string outDirectory, string logPath, CancellationToken ct = default)
    {
        var info = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "run", "--rm", "--gpus", gpus, "--shm-size", shmSize, "-v", $"{Path.GetFullPath(dataDirectory)}:/data:ro", "-v", $"{Path.GetFullPath(outDirectory)}:/out", image }.Concat(args))
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start docker.");
        await using var log = new StreamWriter(logPath);
        var pumps = new[] { Pump(process.StandardOutput, log), Pump(process.StandardError, log) };
        await process.WaitForExitAsync(ct);
        await Task.WhenAll(pumps);
        return process.ExitCode;
    }

    private static async Task Pump(StreamReader reader, StreamWriter log)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            lock (log)
            {
                log.WriteLine(line);
            }
        }
    }
}
