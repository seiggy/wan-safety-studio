using VideoSafetyStudio.Infrastructure.Compute;
using VideoSafetyStudio.Infrastructure.Media;

namespace VideoSafetyStudio.Tests;

public sealed class ProcessHardeningTests
{
    [Fact]
    public void Ffmpeg_and_ffprobe_only_open_local_files()
    {
        var probe = FfmpegMediaTools.ProbeArguments("in.bin");
        var extract = FfmpegMediaTools.ExtractArguments("in.bin", "out", 2, 10);

        foreach (var arguments in new[] { probe, extract })
        {
            var flag = arguments.ToList().IndexOf("-protocol_whitelist");
            Assert.True(flag >= 0 && arguments[flag + 1] == "file");
            Assert.True(flag < arguments.ToList().IndexOf("in.bin"), "the whitelist must precede the input");
        }
    }

    [Fact]
    public void Trainer_container_is_named_isolated_and_unprivileged()
    {
        var args = DockerComputeTarget.BuildArguments("vss-train-x", "all", "8g", "img:1", ["all", "--preset", "nano"], "data", "out");

        Assert.Equal("vss-train-x", args[args.ToList().IndexOf("--name") + 1]);
        Assert.Equal("none", args[args.ToList().IndexOf("--network") + 1]);
        Assert.Equal("ALL", args[args.ToList().IndexOf("--cap-drop") + 1]);
        Assert.Equal("no-new-privileges", args[args.ToList().IndexOf("--security-opt") + 1]);
        Assert.Contains("--pids-limit", args);
        Assert.Equal(["img:1", "all", "--preset", "nano"], args.Skip(args.ToList().IndexOf("img:1")));
    }
}
