using VideoSafetyStudio.Infrastructure.AzureMl;

namespace VideoSafetyStudio.Tests;

public sealed class AzureMlMinuteLimitRegressionTests
{
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(71582789)]
    [InlineData(0)]
    [InlineData(121)]
    public void Foundation_InvalidMinuteBounds_RejectWithSettingName(int minutes)
    {
        var options = AzureMlSamples.Options(("AzureMl:MaxJobMinutes", minutes.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var error = Assert.Throws<InvalidOperationException>(() => AzureMlConfig.Foundation(options));

        Assert.Contains("AzureMl:MaxJobMinutes", error.Message);
        Assert.Contains("between 1 and 120", error.Message);
    }

    [Theory]
    [InlineData(1, 60)]
    [InlineData(120, 7200)]
    public void Foundation_ValidMinuteBounds_ProduceExpectedJobSeconds(int minutes, int expectedSeconds)
    {
        var options = AzureMlSamples.Options(("AzureMl:MaxJobMinutes", minutes.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var foundation = AzureMlConfig.Foundation(options);

        var spec = AzureMlJobSpecBuilder.Build(foundation, AzureMlSamples.Release(), "wan-abc123abc123", "test",
            foundation.DatastorePath("studio/image.png"), foundation.DatastorePath("studio/workflow.json"),
            new AzureMlRunSettings("prompt", "", 42, 20, 3.5, "euler", 768, 768, 5, 81, 16, "image.png"),
            options.MaxJobMinutes * 60);

        Assert.Equal(expectedSeconds, spec.TimeoutSeconds);
        Assert.Equal($"PT{expectedSeconds}S", spec.ToArmBody()["properties"]!["limits"]!["timeout"]!.GetValue<string>());
        Assert.Contains($" --run-timeout {expectedSeconds} ", spec.Command);
    }

    [Fact]
    public void Guard_RejectsRunnerTimeoutThatDiffersFromJobLimit()
    {
        var spec = AzureMlSamples.Spec();
        AzureMlGuard.Validate(spec, AzureMlSamples.Foundation(), AzureMlSamples.Release(), armed: true);
        Assert.Throws<AzureMlSafetyException>(() => AzureMlGuard.Validate(spec with { TimeoutSeconds = 60 }, AzureMlSamples.Foundation(), AzureMlSamples.Release(), armed: true));
    }
}
