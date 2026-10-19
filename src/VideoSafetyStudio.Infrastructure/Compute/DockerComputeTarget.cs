using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using VideoSafetyStudio.Application.Abstractions;

namespace VideoSafetyStudio.Infrastructure.Compute;

/// <summary>
/// Runs the trainer image with the local GPU. On Linux a non-root API shares its effective UID/GID with the
/// container (overriding the image user); output stays private, and HOME points at that writable mount.
/// </summary>
public sealed partial class DockerComputeTarget(
    string gpus = "all",
    string shmSize = "8g",
    Func<IEnumerable<string>, ProcessStartInfo>? commandFactory = null,
    TimeSpan? cleanupTimeout = null,
    TimeProvider? time = null) : IComputeTarget
{
    private TimeProvider Clock => time ?? TimeProvider.System;
    private TimeSpan CleanupTimeout => TimeSpan.FromSeconds(Math.Clamp(cleanupTimeout?.TotalSeconds ?? 30, 0.1, 300));
    private TimeSpan DrainTimeout => TimeSpan.FromTicks(CleanupTimeout.Ticks / 5);
    // Only what the docker CLI needs to find its daemon and config; no server secrets or managed-identity variables.
    private static readonly string[] CliEnvironment =
        ["PATH", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "SystemRoot", "TEMP", "TMP", "DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_CONFIG", "XDG_RUNTIME_DIR"];

    public string Name => "local-docker";

    /// <summary>
    /// The container gets no network: the trainer reads the dataset from /data, and the COCO-initialised weights and YOLOX sources are baked
    /// into the image at build time (see trainers/yolox/Dockerfile), so nothing is fetched while training.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(string name, string gpus, string shmSize, string image, IReadOnlyList<string> args, string dataDirectory, string outDirectory)
    {
        List<string> identity = [];
        if (OperatingSystem.IsLinux())
        {
            var uid = GetEffectiveUserId();
            identity.AddRange(["--user", $"{uid.ToString(CultureInfo.InvariantCulture)}:{GetEffectiveGroupId().ToString(CultureInfo.InvariantCulture)}",
                "--env", "HOME=/out"]);
        }

        return
        [
        "run", "--rm", "--name", name, "--network", "none", "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--pids-limit", "4096",
        "--gpus", gpus, "--shm-size", shmSize,
        .. identity,
        "-v", $"{Path.GetFullPath(dataDirectory)}:/data:ro", "-v", $"{Path.GetFullPath(outDirectory)}:/out", image, .. args,
        ];
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();

    [LibraryImport("libc", EntryPoint = "getegid")]
    private static partial uint GetEffectiveGroupId();

    public async Task<int> RunAsync(string image, IReadOnlyList<string> args, string dataDirectory, string outDirectory, string logPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // The Linux container runs as this API's UID/GID; private mounts need no world-write or chown.
        if (OperatingSystem.IsLinux())
        {
            if (GetEffectiveUserId() == 0)
            {
                throw new InvalidOperationException("Local Docker training requires a non-root API user on Linux so the trainer can share its bind-mount identity.");
            }

            File.SetUnixFileMode(outDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var name = "vss-train-" + Guid.NewGuid().ToString("N")[..12];
        var info = NewInfo(BuildArguments(name, gpus, shmSize, image, args, dataDirectory, outDirectory));
        await using var log = new StreamWriter(logPath);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start docker.");
        using var readers = new CancellationTokenSource();
        var drained = Task.WhenAll(Pump(process.StandardOutput, log, readers.Token), Pump(process.StandardError, log, readers.Token));
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Killing the CLI does not stop the container, so remove it by name.
            await TerminateAndDrainAsync(process, drained, readers);
            await RemoveAsync(name);
            throw;
        }

        await drained;
        return process.ExitCode;
    }

    private ProcessStartInfo NewInfo(IEnumerable<string> arguments)
    {
        if (commandFactory is not null)
        {
            return commandFactory(arguments);
        }

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

    private async Task RemoveAsync(string name)
    {
        try
        {
            using var remove = Process.Start(NewInfo(["rm", "-f", name]));
            if (remove is not null)
            {
                using var readers = new CancellationTokenSource();
                var drained = Task.WhenAll(remove.StandardOutput.ReadToEndAsync(readers.Token), remove.StandardError.ReadToEndAsync(readers.Token));
                // Reserve a fifth of the total budget for each process's forced exit and pipe drain.
                using var timeout = new CancellationTokenSource(CleanupTimeout - 2 * DrainTimeout, Clock);
                try
                {
                    await remove.WaitForExitAsync(timeout.Token);
                    // A descendant could retain a pipe after its parent exits.
                    await drained.WaitAsync(timeout.Token);
                }
                finally
                {
                    await TerminateAndDrainAsync(remove, drained, readers);
                }
            }
        }
        catch (Exception)
        {
            // Best effort: the run is already being reported as cancelled.
        }
    }

    private async Task TerminateAndDrainAsync(Process process, Task drained, CancellationTokenSource readers)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            using var timeout = new CancellationTokenSource(DrainTimeout, Clock);
            await process.WaitForExitAsync(timeout.Token);
            await drained.WaitAsync(timeout.Token);
        }
        catch (Exception)
        {
            // Preserve cancellation even if the daemon or process is already unavailable.
        }
        finally
        {
            await readers.CancelAsync();
            try
            {
                await drained;
            }
            catch (Exception)
            {
                // All pipe tasks are owned and observed; their errors must not replace cancellation.
            }
        }
    }

    private static async Task Pump(StreamReader reader, StreamWriter log, CancellationToken ct)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            lock (log)
            {
                log.WriteLine(line);
            }
        }
    }
}
