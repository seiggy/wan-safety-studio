"""Operational logging for the portal: timestamps, per-step timings, memory, and Azure error details.

Nothing here logs request bodies, tokens or URL query values. Azure's HTTP policy redacts query strings itself.
"""
from __future__ import annotations

from contextlib import contextmanager
import atexit
import faulthandler
import logging
import os
from pathlib import Path
import re
import sys
import time
import traceback

from azure.core.exceptions import AzureError

LOGGER = logging.getLogger("wan.studio")
_REQUEST_ID_HEADERS = ("x-ms-request-id", "x-ms-correlation-request-id", "x-ms-routing-request-id")


def configure(verbose: bool = True):
    """Send timestamped logs to stdout. WAN_STUDIO_LOG_LEVEL (default INFO) sets our own verbosity."""
    level = getattr(logging, os.environ.get("WAN_STUDIO_LOG_LEVEL", "INFO").upper(), logging.INFO)
    handler = logging.StreamHandler(sys.stdout)
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)-7s %(name)s: %(message)s"))
    root = logging.getLogger()
    root.handlers[:] = [handler]
    root.setLevel(level)
    logging.captureWarnings(True)
    # Request/response lines (method, URL, status, request IDs) for every Azure call. Capped at INFO so
    # DEBUG can never print request or response bodies.
    azure_level = logging.INFO if verbose else logging.CRITICAL + 1
    logging.getLogger("azure").setLevel(azure_level)
    logging.getLogger("azure.ai.ml._utils._experimental").setLevel(logging.ERROR)
    if verbose:
        # Native crashes (and SIGSEGV/SIGABRT) print Python stacks instead of dying silently.
        faulthandler.enable(all_threads=True)
        atexit.register(lambda: LOGGER.warning("Process exiting normally (pid %s)", os.getpid()))


def memory() -> str:
    """Resident memory against the container limit; a hard OOM kill shows as startup lines with no exit line."""
    try:
        rss = next(int(line.split()[1]) for line in Path("/proc/self/status").read_text().splitlines()
                   if line.startswith("VmRSS:")) // 1024
    except (OSError, StopIteration, ValueError):
        return "mem=n/a"
    for path in ("/sys/fs/cgroup/memory.max", "/sys/fs/cgroup/memory/memory.limit_in_bytes"):
        try:
            limit = Path(path).read_text().strip()
            if limit.isdigit() and int(limit) < 1 << 50:
                return f"mem={rss}MB/{int(limit) // 1048576}MB"
        except OSError:
            continue
    return f"mem={rss}MB"


def error_where(error) -> str:
    """Last frames as file:line(function) only, so a stack location never carries request content."""
    frames = traceback.extract_tb(error.__traceback__)[-5:]
    return " at " + " <- ".join(f"{Path(f.filename).name}:{f.lineno}({f.name})" for f in reversed(frames)) if frames else ""


def error_detail(error) -> str:
    """HTTP status, Azure error code, request IDs and message for Azure errors; the query part of any URL is dropped."""
    if not isinstance(error, AzureError):
        return ""
    facts = []
    status = getattr(error, "status_code", None)
    if status:
        facts.append(f"http={status}")
    code = getattr(getattr(error, "error", None), "code", None)
    if code:
        facts.append(f"code={code}")
    headers = getattr(getattr(error, "response", None), "headers", None) or {}
    for name in _REQUEST_ID_HEADERS:
        if headers.get(name):
            facts.append(f"{name}={headers[name]}")
    message = " ".join(re.sub(r"\?\S*", "", str(error)).split())[:1500]
    return f" [{' '.join(facts)}] {message}" if facts else f": {message}"


def failure(error) -> str:
    return f"{type(error).__name__}{error_detail(error)}{error_where(error)}"


@contextmanager
def step(name: str, **fields):
    """Log start, duration and outcome of one unit of work (an Azure call, an upload, a job submit)."""
    context = " ".join(f"{key}={value}" for key, value in fields.items())
    started = time.perf_counter()
    LOGGER.info("start %s %s %s", name, context, memory())
    try:
        yield
    except BaseException as error:
        LOGGER.error("FAILED %s after %dms: %s %s", name, (time.perf_counter() - started) * 1000, failure(error), memory())
        raise
    LOGGER.info("done %s in %dms %s", name, (time.perf_counter() - started) * 1000, memory())
