using System.Diagnostics;
using VideoSafetyStudio.Application.Abstractions;

namespace VideoSafetyStudio.Infrastructure.Compute;

/// <summary>Runs the trainer image with the local GPU (dev machine). Same image and arguments as an Azure ML job.</summary>
public sealed class DockerComputeTarget(string gpus = "all", string shmSize = "8g") : IComputeTarget
{
    // Only what the docker CLI needs to find its daemon and config; no server secrets or managed-identity variables.
    private static readonly string[] CliEnvironment =
        ["PATH", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "SystemRoot", "TEMP", "TMP", "DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_CONFIG", "XDG_RUNTIME_DIR"];

    public string Name => "local-docker";

    /// <summary>
    /// The container gets no network: the trainer reads the dataset from /data, and the COCO-initialised weights and YOLOX sources are baked
    /// into the image at build time (see trainers/yolox/Dockerfile), so nothing is fetched while training.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(string name, string gpus, string shmSize, string image, IReadOnlyList<string> args, string dataDirectory, string outDirectory) =>
    [
        "run", "--rm", "--name", name, "--network", "none", "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--pids-limit", "4096",
        "--gpus", gpus, "--shm-size", shmSize,
        "-v", $"{Path.GetFullPath(dataDirectory)}:/data:ro", "-v", $"{Path.GetFullPath(outDirectory)}:/out", image, .. args,
    ];

    public async Task<int> RunAsync(string image, IReadOnlyList<string> args, string dataDirectory, string outDirectory, string logPath, CancellationToken ct = default)
    {
        var name = "vss-train-" + Guid.NewGuid().ToString("N")[..12];
        var info = NewInfo(BuildArguments(name, gpus, shmSize, image, args, dataDirectory, outDirectory));
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start docker.");
        await using var log = new StreamWriter(logPath);
        var pumps = new[] { Pump(process.StandardOutput, log), Pump(process.StandardError, log) };
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Killing the CLI does not stop the container, so remove it by name.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await RemoveAsync(name);
            await Task.WhenAll(pumps);
            throw;
        }

        await Task.WhenAll(pumps);
        return process.ExitCode;
    }

    private static ProcessStartInfo NewInfo(IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.Environment.Clear();
        foreach (var variable in CliEnvironment)
        {
            if (Environment.GetEnvironmentVariable(variable) is { } value)
            {
                info.Environment[variable] = value;
            }
        }

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    private static async Task RemoveAsync(string name)
    {
        try
        {
            using var remove = Process.Start(NewInfo(["rm", "-f", name]));
            if (remove is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                _ = remove.StandardOutput.ReadToEndAsync(timeout.Token);
                _ = remove.StandardError.ReadToEndAsync(timeout.Token);
                await remove.WaitForExitAsync(timeout.Token);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Best effort: the run is already being reported as cancelled.
        }
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
