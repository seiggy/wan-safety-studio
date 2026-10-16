using System.Diagnostics;
using VideoSafetyStudio.Infrastructure.Compute;

namespace VideoSafetyStudio.Tests;

public sealed class DockerAccessRegressionTests
{
    [Fact]
    public void Linux_identity_policy_is_explicit_and_never_adds_world_writable_flags()
    {
        var args = DockerComputeTarget.BuildArguments("test", "all", "8g", "image", [], "data", "out").ToList();
        if (OperatingSystem.IsLinux())
        {
            Assert.Contains("--user", args);
            Assert.Contains("HOME=/out", args);
        }
        else Assert.DoesNotContain("--user", args);
        Assert.DoesNotContain("--privileged", args);
        Assert.Contains(args, value => value.EndsWith(":/data:ro", StringComparison.Ordinal));
    }

    [LinuxDockerAccessFact]
    public async Task Non_root_api_identity_overrides_image_uid_and_can_read_private_output()
    {
        using var workspace = new VisionRegressionWorkspace();
        var image = Environment.GetEnvironmentVariable("VSS_DOCKER_ACCESS_IMAGE")!;
        var imageUser = await RegressionProcess.RunAsync("docker", ["image", "inspect", "--format", "{{.Config.User}}", image]);
        Assert.StartsWith("10001", imageUser.Trim());
        var data = Path.Combine(workspace.Root, "data");
        var output = Path.Combine(workspace.Root, "out");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(output);
        var input = Path.Combine(data, "classes.json");
        await File.WriteAllTextAsync(input, """["leak"]""");
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(input, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var compute = new DockerComputeTarget(commandFactory: arguments =>
        {
            var list = arguments.ToList();
            if (list[0] == "run")
            {
                var gpu = list.IndexOf("--gpus");
                list.RemoveRange(gpu, 2);
                list.InsertRange(list.IndexOf(image), ["--entrypoint", "sh"]);
            }
            var info = new ProcessStartInfo("docker") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in list) info.ArgumentList.Add(argument);
            return info;
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var code = await compute.RunAsync(image, ["-c", "umask 022; cat /data/classes.json > /out/artifact.json; id -u > /out/uid"],
            data, output, Path.Combine(output, "train.log"), timeout.Token);
        Assert.Equal(0, code);
        Assert.Equal("""["leak"]""", await File.ReadAllTextAsync(Path.Combine(output, "artifact.json")));
        var args = DockerComputeTarget.BuildArguments("test", "all", "8g", image, [], data, output).ToList();
        Assert.Equal(args[args.IndexOf("--user") + 1].Split(':')[0], (await File.ReadAllTextAsync(Path.Combine(output, "uid"))).Trim());
        if (OperatingSystem.IsLinux())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(output));
    }
}

public sealed class LinuxDockerAccessFactAttribute : FactAttribute
{
    public LinuxDockerAccessFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root")
            Skip = "Requires a non-root Linux API UID and a same-host Docker daemon.";
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VSS_DOCKER_ACCESS_IMAGE")))
            Skip = "Set VSS_DOCKER_ACCESS_IMAGE to a local shell-capable image declaring USER 10001 (no image is pulled).";
    }
}
