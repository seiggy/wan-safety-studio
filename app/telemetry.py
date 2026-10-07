"""OpenTelemetry wiring: traces and logs go to the OTLP endpoint Aspire (or any collector) injects."""
from __future__ import annotations

import importlib
import logging
import os

from opentelemetry import trace
from opentelemetry.sdk.resources import Resource
from opentelemetry.sdk.trace import TracerProvider

LOGGER = logging.getLogger("wan.telemetry")


def configure_telemetry(service_name: str):
    """Install the tracer provider; export over OTLP/HTTP only when OTEL_EXPORTER_OTLP_ENDPOINT is set."""
    provider = TracerProvider(resource=Resource.create({"service.name": os.environ.get("OTEL_SERVICE_NAME", service_name)}))
    if os.environ.get("OTEL_EXPORTER_OTLP_ENDPOINT"):
        # Aspire injects OTLP/gRPC; other collectors may use http/protobuf.
        flavor = "http" if os.environ.get("OTEL_EXPORTER_OTLP_PROTOCOL", "grpc").startswith("http") else "grpc"
        try:
            log_module = importlib.import_module(f"opentelemetry.exporter.otlp.proto.{flavor}._log_exporter")
            trace_module = importlib.import_module(f"opentelemetry.exporter.otlp.proto.{flavor}.trace_exporter")
            OTLPLogExporter, OTLPSpanExporter = log_module.OTLPLogExporter, trace_module.OTLPSpanExporter
            from opentelemetry.sdk._logs import LoggerProvider, LoggingHandler
            from opentelemetry.sdk._logs.export import BatchLogRecordProcessor
            from opentelemetry.sdk.trace.export import BatchSpanProcessor
        except ImportError:
            LOGGER.warning("OTEL_EXPORTER_OTLP_ENDPOINT is set but the OTLP exporter is not installed; telemetry stays local.")
        else:
            provider.add_span_processor(BatchSpanProcessor(OTLPSpanExporter()))
            logs = LoggerProvider(resource=provider.resource)
            logs.add_log_record_processor(BatchLogRecordProcessor(OTLPLogExporter()))
            logging.getLogger().addHandler(LoggingHandler(logger_provider=logs))
    trace.set_tracer_provider(provider)
