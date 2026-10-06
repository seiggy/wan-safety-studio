#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

APP_ROOT="$ROOT_DIR"
APP_REL=""
if [[ -f "$ROOT_DIR/src/main.py" ]]; then
  APP_ROOT="$ROOT_DIR/src"
  APP_REL="src"
fi

VERSION="${1:-$(date -u +%Y%m%d%H%M%S)}"
PKG_DIR="azureml/.runtime_pkg"
STAGING_DIR="$PKG_DIR/repo"
TARBALL="$PKG_DIR/comfyui-runtime-$VERSION.tar.gz"

rm -rf "$PKG_DIR"
mkdir -p "$STAGING_DIR"

rsync -a \
  --exclude-from=.amlignore \
  --exclude=azureml/.runtime_pkg \
  --exclude=azureml/.upload_code \
  --exclude=azureml/.upload_models_min \
  ./ "$STAGING_DIR"/

CONFIGS_DIR="$STAGING_DIR/models/configs"
APP_PREFIX="repo"
if [[ -n "$APP_REL" ]]; then
  CONFIGS_DIR="$STAGING_DIR/$APP_REL/models/configs"
  APP_PREFIX="repo/$APP_REL"
fi

mkdir -p "$CONFIGS_DIR"
cp -a "$APP_ROOT/models/configs/." "$CONFIGS_DIR/"

tar -C "$PKG_DIR" -czf "$TARBALL" repo

required_paths=(
  "$APP_PREFIX/main.py"
  "$APP_PREFIX/execution.py"
  "$APP_PREFIX/latent_preview.py"
  "repo/azureml/bootstrap_and_run.py"
  "repo/azureml/run_workflow_job.py"
  "$APP_PREFIX/comfy/ldm/models/autoencoder.py"
  "$APP_PREFIX/models/configs/v1-inference.yaml"
)

for required_path in "${required_paths[@]}"; do
  tar -tzf "$TARBALL" "$required_path" >/dev/null
done

printf '%s\n' "$VERSION" > azureml/.latest_runtime_pkg_version
printf '%s\n' "$TARBALL"
