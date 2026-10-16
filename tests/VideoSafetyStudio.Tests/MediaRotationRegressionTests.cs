using System.Diagnostics;
using System.Text.Json;
using VideoSafetyStudio.Application;
using VideoSafetyStudio.Application.Vision;
using VideoSafetyStudio.Domain.Vision;
using VideoSafetyStudio.Infrastructure.Media;

namespace VideoSafetyStudio.Tests;

public sealed class MediaRotationRegressionTests
{
    [Fact]
    public void Extraction_explicitly_retains_the_coded_geometry_reported_by_probe()
    {
        var arguments = FfmpegMediaTools.ExtractArguments("video.mp4", "frames", 2, 3).ToList();
        Assert.Contains("-noautorotate", arguments);
        Assert.True(arguments.IndexOf("-noautorotate") < arguments.IndexOf("-i"));
        Assert.Contains("stream=width,height,avg_frame_rate:format=duration", FfmpegMediaTools.ProbeArguments("video.mp4"));
    }

    [FfmpegFixtureFact]
    public async Task Rotated_and_unrotated_videos_match_jpeg_index_coco_and_boundary_boxes()
    {
        using var workspace = new VisionRegressionWorkspace(new FfmpegMediaTools());
        var source = Path.Combine(workspace.Root, "plain.mp4");
        var rotated = Path.Combine(workspace.Root, "rotated.mp4");
        await RegressionProcess.RunAsync("ffmpeg",
            ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=red:size=96x64:rate=2", "-t", "1", "-c:v", "mpeg4", source]);
        await RegressionProcess.RunAsync("ffmpeg",
            ["-nostdin", "-v", "error", "-i", source, "-c", "copy", "-metadata:s:v:0", "rotate=90", rotated]);
        var rotation = await RegressionProcess.RunAsync("ffprobe",
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream_side_data=rotation", "-of", "json", rotated]);
        using (var metadata = JsonDocument.Parse(rotation))
        {
            Assert.Contains(metadata.RootElement.GetProperty("streams")[0].GetProperty("side_data_list").EnumerateArray(),
                item => item.TryGetProperty("rotation", out var value) && Math.Abs(value.GetInt32()) == 90);
        }

        await workspace.Vision.CreateProjectAsync("demo", ["leak"]);
        foreach (var videoPath in new[] { source, rotated })
        {
            var id = await workspace.Vision.ImportVideoAsync("demo", videoPath, new VideoOrigin("upload"));
            var index = await workspace.Vision.ExtractAsync("demo", id, 1);
            Assert.Equal((96, 64), (index.Width, index.Height));
            var jpeg = Path.Combine(workspace.Root, id + ".jpg");
            await File.WriteAllBytesAsync(jpeg, await workspace.Vision.FrameImageAsync("demo", id, index.Frames[0]));
            var geometry = await RegressionProcess.RunAsync("ffprobe",
                ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "json", jpeg]);
            using var dimensions = JsonDocument.Parse(geometry);
            var stream = dimensions.RootElement.GetProperty("streams")[0];
            Assert.Equal(index.Width, stream.GetProperty("width").GetInt32());
            Assert.Equal(index.Height, stream.GetProperty("height").GetInt32());
            await workspace.Vision.PutLabelsAsync("demo", id, index.Frames[0], true,
                [new Box("leak", 0, 0, index.Width, index.Height)]);
        }

        await workspace.Vision.FreezeDatasetAsync("demo", 0);
        var coco = Json.From<CocoDataset>((await workspace.Store.ReadAsync(VisionService.Key("demo", "datasets/v001/train.json")))!)!;
        Assert.Equal(2, coco.Images.Count);
        Assert.All(coco.Images, image => Assert.Equal((96, 64), (image.Width, image.Height)));
        Assert.All(coco.Annotations, box => Assert.Equal(new double[] { 0, 0, 96, 64 }, box.Bbox));
    }
}

public sealed class FfmpegFixtureFactAttribute : FactAttribute
{
    public FfmpegFixtureFactAttribute()
    {
        if (!RegressionProcess.Available("ffmpeg") || !RegressionProcess.Available("ffprobe"))
            Skip = "ffmpeg and ffprobe must be installed to exercise rotated video fixtures.";
    }
}

internal static class RegressionProcess
{
    public static bool Available(string executable) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Any(folder => File.Exists(Path.Combine(folder, executable + (OperatingSystem.IsWindows() ? ".exe" : ""))));

    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var results = await Task.WhenAll(output, error);
            Assert.True(process.ExitCode == 0, $"{executable} exited {process.ExitCode}: {results[1]}");
            return results[0];
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await timeout.CancelAsync();
            try { await Task.WhenAll(output, error); } catch (Exception) { }
            throw;
        }
    }
}
