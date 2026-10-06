FROM pytorch/pytorch:2.8.0-cuda12.8-cudnn9-runtime

ARG DEBIAN_FRONTEND=noninteractive

RUN apt-get update && apt-get install -y --no-install-recommends \
    ffmpeg \
    git \
    libgl1 \
    libglib2.0-0 \
    libsm6 \
    libxext6 \
    libxrender1 \
    && rm -rf /var/lib/apt/lists/*

COPY requirements-azureml.txt /tmp/requirements-azureml.txt

RUN python -m pip install --upgrade pip setuptools wheel && \
    python -m pip install --no-cache-dir -r /tmp/requirements-azureml.txt

ENV PYTHONUNBUFFERED=1
ENV PIP_NO_CACHE_DIR=1

