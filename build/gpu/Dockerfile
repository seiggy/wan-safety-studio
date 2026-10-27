# syntax=docker/dockerfile:1
FROM pytorch/pytorch:2.8.0-cuda12.8-cudnn9-runtime@sha256:417bd75df6365104c283ea4c1651fb3530d9eb5a4c2fafa51943cff2a94e6385
ARG RUNTIME_LOCK_SHA256
LABEL studio.runtime-lock-sha256=${RUNTIME_LOCK_SHA256}
# Use exactly one explicit customer-approved feed, with no host config or embedded credentials.
RUN python -c "import sys; assert sys.version_info[:2] == (3, 11), 'Review the lock target for this base image'"
COPY requirements-runtime.lock /opt/wan/requirements-runtime.lock
RUN test -n "$RUNTIME_LOCK_SHA256" && echo "$RUNTIME_LOCK_SHA256  /opt/wan/requirements-runtime.lock" | sha256sum -c -
RUN --mount=type=secret,id=netrc,target=/root/.netrc \
    --mount=type=secret,id=pip-index,required=true \
    export PIP_INDEX_URL="$(cat /run/secrets/pip-index)" \
    && test -n "$PIP_INDEX_URL" \
    && python -c "import os,urllib.parse; u=urllib.parse.urlsplit(os.environ['PIP_INDEX_URL']); assert u.scheme=='https' and u.hostname and not u.username and not u.password and not u.query and not u.fragment, 'Feed must be explicit HTTPS with no embedded credentials'" \
    && PIP_CONFIG_FILE=/dev/null PIP_EXTRA_INDEX_URL="" \
       python -m pip install --disable-pip-version-check --no-cache-dir --only-binary=:all: \
       --require-hashes --index-url "$PIP_INDEX_URL" -r /opt/wan/requirements-runtime.lock \
    && python -m pip check
COPY probe.py /opt/wan/probe.py
ENV HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 HF_HUB_DISABLE_TELEMETRY=1 PYTHONDONTWRITEBYTECODE=1
