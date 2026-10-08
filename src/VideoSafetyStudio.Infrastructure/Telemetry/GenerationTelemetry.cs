using System.Diagnostics;
using System.Diagnostics.Metrics;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Domain.Generation;

namespace VideoSafetyStudio.Infrastructure.Telemetry;

/// <summary>Emits a job's stage timings as OpenTelemetry: a span per stage with the real start/end times, plus duration histograms.</summary>
public sealed class GenerationTelemetry : IGenerationTelemetry
{
    public const string SourceName = "VideoSafetyStudio.Generation";

    private static readonly ActivitySource Source = new(SourceName);
    private static readonly Meter Meter = new(SourceName);
    private static readonly Histogram<double> StageSeconds = Meter.CreateHistogram<double>("wan.generation.stage.duration", "s", "Seconds in a workflow stage");
    private static readonly Histogram<double> JobSeconds = Meter.CreateHistogram<double>("wan.generation.job.duration", "s", "Seconds for a whole generation job");
    private static readonly Histogram<double> StepSeconds = Meter.CreateHistogram<double>("wan.generation.sampler.step.duration", "s", "Seconds per sampler step");

    private static DateTime Utc(double unixSeconds) => DateTime.UnixEpoch.AddSeconds(unixSeconds);

    public void Emit(string jobName, GenerationParams parameters, StageTimer timer, IReadOnlyDictionary<string, double> phases, double jobStart, double jobEnd, bool ok)
    {
        KeyValuePair<string, object?>[] tags =
        [
            new("wan.job", jobName), new("wan.width", parameters.Width), new("wan.height", parameters.Height),
            new("wan.frames", parameters.Frames), new("wan.seconds", parameters.Seconds), new("wan.provider", parameters.Provider),
        ];
        using var job = Source.StartActivity("generation.job", ActivityKind.Internal, default(ActivityContext), tags, null, new DateTimeOffset(Utc(jobStart)));
        foreach (var (stage, node, start, end) in timer.Intervals())
        {
            using var span = Source.StartActivity($"stage.{stage}", ActivityKind.Internal, job?.Context ?? default, tags, null, new DateTimeOffset(Utc(start)));
            span?.SetTag("wan.stage", stage);
            span?.SetTag("wan.node", node);
            span?.SetTag("wan.cached", false);
            span?.SetEndTime(Utc(end));
        }

        // Cached nodes never run, so ComfyUI sends no events for them; record a zero-length span so every stage shows up.
        foreach (var (stage, node, at) in timer.CachedIntervals())
        {
            using var span = Source.StartActivity($"stage.{stage}", ActivityKind.Internal, job?.Context ?? default, tags, null, new DateTimeOffset(Utc(at)));
            span?.SetTag("wan.stage", stage);
            span?.SetTag("wan.node", node);
            span?.SetTag("wan.cached", true);
            span?.SetEndTime(Utc(at));
        }

        KeyValuePair<string, object?>[] shape = [new("width", parameters.Width), new("height", parameters.Height), new("frames", parameters.Frames)];
        foreach (var (stage, entry) in timer.Summary().Stages)
        {
            StageSeconds.Record(entry.Seconds, [.. shape, new("stage", stage), new("cached", entry.Cached)]);
            if (entry.SecondsPerStep is { } perStep)
            {
                StepSeconds.Record(perStep, [.. shape, new("stage", stage)]);
            }
        }

        foreach (var (phase, seconds) in phases)
        {
            StageSeconds.Record(seconds, [.. shape, new("stage", "phase." + phase), new("cached", false)]);
        }

        JobSeconds.Record(jobEnd - jobStart, [.. shape, new("ok", ok)]);
        job?.SetStatus(ok ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
        job?.SetEndTime(Utc(jobEnd));
    }
}
