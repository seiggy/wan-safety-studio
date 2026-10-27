# syntax=docker/dockerfile:1
# YOLOX trainer image. Build from the repo root (context = trainers); the package index is required (one Azure Artifacts feed, no public default):
#   docker build -f trainers/yolox/Dockerfile --build-arg PIP_INDEX_URL=<feed pypi/simple url> -t wan-trainer-yolox:dev trainers
# Unverified on hardware: first build/run is part of the Phase 0 spike (record the PyTorch/CUDA pair that works on your GPU).
# Base: pytorch/pytorch:2.5.1-cuda12.4-cudnn9-runtime, pinned by manifest digest (resolved with `docker buildx imagetools inspect`).
ARG BASE=pytorch/pytorch:2.5.1-cuda12.4-cudnn9-runtime@sha256:c8268a92a69bd500f8be0e665b2630ee006dadaf7bfbc24249141b15ff622755
FROM ${BASE}
ARG YOLOX_COMMIT=6ddff4824372906469a7fae2dc3206c7aa4bbaee
ARG PIP_INDEX_URL
ARG YOLOX_RELEASE=https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0
ENV PYTHONUNBUFFERED=1 YOLOX_HOME=/opt/YOLOX YOLOX_WEIGHTS=/weights HOME=/home/trainer
RUN apt-get update && apt-get install -y --no-install-recommends git curl libgl1 libglib2.0-0 build-essential \
    && rm -rf /var/lib/apt/lists/*
COPY yolox/requirements.txt /tmp/requirements.txt
# Pinned YOLOX commit (Apache-2.0); its own dependencies come from the pinned requirements file.
RUN --mount=type=secret,id=netrc,target=/root/.netrc \
    --mount=type=secret,id=pip-index \
    if [ -f /run/secrets/pip-index ]; then export PIP_INDEX_URL="$(cat /run/secrets/pip-index)"; fi \
    && test -n "${PIP_INDEX_URL}" \
    && git clone https://github.com/Megvii-BaseDetection/YOLOX ${YOLOX_HOME} && cd ${YOLOX_HOME} \
    && git checkout ${YOLOX_COMMIT} \
    && PIP_CONFIG_FILE=/dev/null PIP_EXTRA_INDEX_URL="" pip install --no-cache-dir --index-url "${PIP_INDEX_URL}" -r /tmp/requirements.txt \
    && pip install --no-cache-dir --no-index --no-deps --no-build-isolation -e .
# Release weights (initialised from COCO), verified against the SHA-256 values in weights.sha256 (taken from the 0.1.1rc0 release assets).
COPY yolox/weights.sha256 /tmp/weights.sha256
RUN mkdir -p ${YOLOX_WEIGHTS} && for w in yolox_nano yolox_tiny yolox_s; do curl -fsSL -o ${YOLOX_WEIGHTS}/$w.pth ${YOLOX_RELEASE}/$w.pth; done \
    && cd ${YOLOX_WEIGHTS} && sha256sum -c /tmp/weights.sha256
WORKDIR /app
# The trainer code keeps the module path vision.trainers.* it was written with.
RUN mkdir -p vision/trainers && touch vision/__init__.py vision/trainers/__init__.py
COPY sdk.py vision/trainers/sdk.py
COPY yolox vision/trainers/yolox
# Non-root. /data is mounted read-only; /out must be writable by uid 10001 (a bind mount keeps the host directory's ownership).
RUN useradd --uid 10001 --create-home --home-dir /home/trainer --shell /usr/sbin/nologin trainer \
    && mkdir -p /out && chown -R trainer:trainer /out /home/trainer ${YOLOX_HOME}
USER 10001
ENTRYPOINT ["python", "-m", "vision.trainers.yolox"]
