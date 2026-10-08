"""Emit a job's stage timings to OpenTelemetry: one span per stage (with the real start/end times) and duration histograms."""
from __future__ import annotations

from opentelemetry import metrics, trace
from opentelemetry.trace import Status, StatusCode

from generation.timing import StageTimer

TRACER = trace.get_tracer("wan-safety-studio.generation")
METER = metrics.get_meter("wan-safety-studio.generation")
STAGE_SECONDS = METER.create_histogram("wan.generation.stage.duration", unit="s", description="Seconds in a workflow stage")
JOB_SECONDS = METER.create_histogram("wan.generation.job.duration", unit="s", description="Seconds for a whole generation job")
STAGE_SECONDS_PER_STEP = METER.create_histogram("wan.generation.sampler.step.duration", unit="s", description="Seconds per sampler step")


def emit(job_name: str, params: dict, timer: StageTimer, phases: dict[str, float], job_start: float, job_end: float, ok: bool):
    """params: width,height,frames,seconds,provider. phases: wall-clock seconds for upload, comfyui, retrieve, store."""
    attributes = {"wan.job": job_name, **{f"wan.{key}": value for key, value in params.items()}}
    with TRACER.start_as_current_span("generation.job", start_time=int(job_start * 1e9), end_on_exit=False, attributes=attributes) as job:
        context = trace.set_span_in_context(job)
        for stage, node, start, end in timer.intervals():
            span = TRACER.start_span(f"stage.{stage}", context=context, start_time=int(start * 1e9),
                                     attributes={**attributes, "wan.stage": stage, "wan.node": node})
            span.end(end_time=int(end * 1e9))
        summary = timer.summary()
        shape = {key: params[key] for key in ("width", "height", "frames") if key in params}
        for stage, entry in summary["stages"].items():
            STAGE_SECONDS.record(entry["seconds"], {"stage": stage, "cached": entry["cached"], **shape})
            if entry["secondsPerStep"] is not None:
                STAGE_SECONDS_PER_STEP.record(entry["secondsPerStep"], {"stage": stage, **shape})
        for phase, seconds in phases.items():
            STAGE_SECONDS.record(seconds, {"stage": f"phase.{phase}", "cached": False, **shape})
        JOB_SECONDS.record(job_end - job_start, {"ok": ok, **shape})
        job.set_status(Status(StatusCode.OK if ok else StatusCode.ERROR))
        job.end(end_time=int(job_end * 1e9))
