# syntax=docker/dockerfile:1
# YOLOX trainer image. Build from the repo root (context = app):
#   docker build -f app/vision/trainers/yolox/Dockerfile -t wan-trainer-yolox:dev app
# Unverified on hardware: first build/run is part of the Phase 0 spike (record the PyTorch/CUDA pair that works on your GPU).
ARG BASE=pytorch/pytorch:2.5.1-cuda12.4-cudnn9-runtime
FROM ${BASE}
ARG YOLOX_COMMIT=6ddff4824372906469a7fae2dc3206c7aa4bbaee
ARG PIP_INDEX_URL=https://pypi.org/simple
ARG YOLOX_RELEASE=https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0
ENV PYTHONUNBUFFERED=1 YOLOX_HOME=/opt/YOLOX YOLOX_WEIGHTS=/weights
RUN apt-get update && apt-get install -y --no-install-recommends git curl libgl1 libglib2.0-0 build-essential \
    && rm -rf /var/lib/apt/lists/*
# Pinned YOLOX commit (Apache-2.0). ninja/onnx/onnxsim/pycocotools per its requirements.
RUN git clone https://github.com/Megvii-BaseDetection/YOLOX ${YOLOX_HOME} && cd ${YOLOX_HOME} \
    && git checkout ${YOLOX_COMMIT} \
    && pip install --no-cache-dir "numpy<2" opencv-python-headless loguru tqdm thop ninja tabulate psutil tensorboard \
         pycocotools onnx onnx-simplifier==0.4.10 onnxruntime \
    && pip install --no-cache-dir --no-deps -e .
# Release weights (initialised from COCO). Record the hashes of what you shipped in the model card.
RUN mkdir -p ${YOLOX_WEIGHTS} && for w in yolox_nano yolox_tiny yolox_s; do curl -fsSL -o ${YOLOX_WEIGHTS}/$w.pth ${YOLOX_RELEASE}/$w.pth; done \
    && sha256sum ${YOLOX_WEIGHTS}/*.pth > ${YOLOX_WEIGHTS}/SHA256SUMS
WORKDIR /app
COPY vision/__init__.py vision/__init__.py
COPY vision/trainers/__init__.py vision/trainers/__init__.py
COPY vision/trainers/sdk.py vision/trainers/sdk.py
COPY vision/trainers/yolox vision/trainers/yolox
ENTRYPOINT ["python", "-m", "vision.trainers.yolox"]
