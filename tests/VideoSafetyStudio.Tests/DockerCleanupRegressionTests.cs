using System.Diagnostics;
using System.Globalization;
using VideoSafetyStudio.Infrastructure.Compute;
using VideoSafetyStudio.Tests.Support;

namespace VideoSafetyStudio.Tests;

public sealed class DockerCleanupRegressionTests
{
    [PowerShellFixtureFact]
    public async Task Timed_out_removal_kills_its_specific_process_tree_observes_pipes_and_keeps_cancellation()
    {
        using var workspace = new VisionRegressionWorkspace();
        var script = Path.Combine(workspace.Root, "docker-helper.ps1");
        await File.WriteAllTextAsync(script, """
            param([string]$Mode, [string]$PidFile)
            if ($Mode -eq 'rm') {
                $child = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 300') -NoNewWindow -PassThru
                [IO.File]::WriteAllText($PidFile + '.child', $child.Id.ToString())
            }
            [Console]::Out.WriteLine('trainer stdout')
            [Console]::Error.WriteLine('trainer stderr')
            [Console]::Out.Flush()
            [Console]::Error.Flush()
            [IO.File]::WriteAllText($PidFile, $PID.ToString())
            Start-Sleep -Seconds 300
            """);
        var runPid = Path.Combine(workspace.Root, "run.pid");
        var removePid = Path.Combine(workspace.Root, "remove.pid");
        var output = Path.Combine(workspace.Root, "out");
        Directory.CreateDirectory(output);
        var log = Path.Combine(output, "train.log");
        var clock = new RemovalClock();
        var compute = new DockerComputeTarget(commandFactory: arguments =>
        {
            var mode = arguments.First();
            var info = new ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", script, mode, mode == "run" ? runPid : removePid })
                info.ArgumentList.Add(argument);
            return info;
        }, cleanupTimeout: TimeSpan.FromSeconds(3), time: clock);
        using var cancel = new CancellationTokenSource();
        var running = compute.RunAsync("fixture", [], workspace.Root, output, log, cancel.Token);
        try
        {
            await WaitForFileAsync(runPid);
            await cancel.CancelAsync();
            await WaitForFileAsync(removePid);
            await clock.Ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // Start the timeout only after both helper PIDs exist; shell startup is not the behavior under test.
            clock.Advance(TimeSpan.FromSeconds(3));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(15)));
            foreach (var path in new[] { runPid, removePid, removePid + ".child" })
                Assert.False(IsRunning(ReadPid(path)), $"Child {ReadPid(path)} survived cancellation.");
            Assert.Contains("trainer stdout", await File.ReadAllTextAsync(log));
            Assert.Contains("trainer stderr", await File.ReadAllTextAsync(log));
        }
        finally
        {
            await cancel.CancelAsync();
            clock.Advance(TimeSpan.FromSeconds(30));
            foreach (var path in new[] { runPid, removePid, removePid + ".child" })
            {
                if (File.Exists(path) && IsRunning(ReadPid(path)))
                {
                    using var process = Process.GetProcessById(ReadPid(path));
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            try { await running.WaitAsync(TimeSpan.FromSeconds(15)); } catch (Exception) { }
        }
    }

    private static int ReadPid(string path) => int.Parse(File.ReadAllText(path), CultureInfo.InvariantCulture);

    private sealed class RemovalClock : TimeProvider
    {
        private readonly ReliabilityTimeProvider _inner = new();
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override long GetTimestamp() => _inner.GetTimestamp();
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public void Advance(TimeSpan elapsed) => _inner.Advance(elapsed);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _inner.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromSeconds(1.8)) Ready.TrySetResult();
            return timer;
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!File.Exists(path) || new FileInfo(path).Length == 0)
            await Task.Delay(20, timeout.Token);
    }
}

public sealed class PowerShellFixtureFactAttribute : FactAttribute
{
    public PowerShellFixtureFactAttribute()
    {
        if (!RegressionProcess.Available("pwsh")) Skip = "PowerShell is needed for the controlled Docker process fixture.";
        else if (OperatingSystem.IsLinux() && Environment.UserName == "root") Skip = "The local Docker target requires a non-root Linux API.";
    }
}
