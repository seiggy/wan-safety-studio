# syntax=docker/dockerfile:1
# Hosted WAN Safety Studio portal for a container App Service created outside Terraform.
# Build steps and required app settings: docs/app-service-docker.md. Run from the repo root:
#   docker build --platform linux/amd64 -f app/Dockerfile \
#     --build-context upstream=<patched upstream checkout> -t <image> app
FROM ghcr.io/astral-sh/uv:0.9.5@sha256:f459f6f73a8c4ef5d69f4e6fbbdb8af751d6fa40ec34b39a1ab469acd6e289b7 AS uv

FROM python:3.12-slim-bookworm@sha256:392307d22300de8b5986851a12d9176dfc0fc073e65bf6523ebd7dcbeb23564e
ARG PYPI_INDEX=https://pypi.org/simple
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 PORT=8000
WORKDIR /app

# Same hash-locked wheels as Prepare and Publish.
COPY pyproject.toml uv.lock ./
RUN --mount=from=uv,source=/uv,target=/bin/uv \
    uv --no-config export --locked --offline --no-dev --no-emit-project --no-header --no-annotate \
        --format requirements.txt -o /tmp/requirements.txt && \
    uv --no-config pip install --system --require-hashes --only-binary :all: --no-cache \
        --default-index "$PYPI_INDEX" -r /tmp/requirements.txt && \
    rm /tmp/requirements.txt

COPY *.py ./
COPY web ./web
# No release manifest in the image: WAN_STUDIO_RELEASE_JSON points at the assets that exist in the workspace.
# The patched, pinned upstream runtime; startup re-hashes every file against the manifest.
COPY --from=upstream azureml ./upstream/azureml
COPY --from=upstream LICENSE* NOTICE* ./upstream/

USER 10001
EXPOSE 8000
CMD ["python", "/app/runtime.py", "--cache", "/tmp/wan-studio", "portal", "--profile", "wan", "--hosted"]
