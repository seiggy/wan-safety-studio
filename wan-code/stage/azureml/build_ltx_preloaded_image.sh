#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
STAGING_DIR="$(mktemp -d)"

cleanup() {
  rm -rf "$STAGING_DIR"
}

trap cleanup EXIT

read_trimmed_file() {
  local path="$1"
  tr -d '\r\n' < "$path"
}

ACR_NAME="${ACR_NAME:-sc1mlcr}"
IMAGE_NAME="${IMAGE_NAME:-comfyui-ltx-runtime-preloaded}"
IMAGE_TAG="${IMAGE_TAG:-$(date -u +%Y%m%d%H%M%S)}"
BASE_RUNTIME_TAG="${BASE_RUNTIME_TAG:-$(read_trimmed_file "$ROOT_DIR/azureml/.latest_env_version")}"
LOGIN_SERVER="${LOGIN_SERVER:-$(az acr show --name "$ACR_NAME" --query loginServer -o tsv)}"
BASE_IMAGE="${BASE_IMAGE:-$LOGIN_SERVER/comfyui-runtime:$BASE_RUNTIME_TAG}"
PRELOADED_MODELS_DIR="${PRELOADED_MODELS_DIR:-/opt/comfyui-preloaded-models}"
REGISTER_AZUREML_ENV="${REGISTER_AZUREML_ENV:-1}"
REGISTER_WAN_ENV="${REGISTER_WAN_ENV:-1}"
WRITE_LATEST_ENV_VERSION="${WRITE_LATEST_ENV_VERSION:-0}"
ACR_BUILD_TIMEOUT="${ACR_BUILD_TIMEOUT:-14400}"
LTX_ENV_NAME="${LTX_ENV_NAME:-comfyui-ltx-cu128}"
WAN_ENV_NAME="${WAN_ENV_NAME:-comfyui-wan-cu128}"

mkdir -p "$STAGING_DIR/context"
cp "$ROOT_DIR/azureml/context/Dockerfile.ltx-preloaded" "$STAGING_DIR/context/"
cp "$ROOT_DIR/azureml/context/download_model_manifest.py" "$STAGING_DIR/context/"

ROOT_DIR="$ROOT_DIR" python - <<'PY' > "$STAGING_DIR/context/ltx-model-manifest.json"
import json
import os
import sys
from pathlib import Path

root = Path(os.environ["ROOT_DIR"]).resolve()
if str(root) not in sys.path:
    sys.path.insert(0, str(root))

from azureml import workflow_utils
from azureml.bootstrap_ltx_and_run import LTX_MODEL_URLS

workflow = workflow_utils.load_workflow_payload(
    root / "azureml" / "workflows" / "ltx_2_first_last_ttp_workflow.json"
)
model_refs = sorted(
    workflow_utils.collect_required_model_references(workflow),
    key=lambda item: (item.folder_name, item.filename),
)
manifest = [
    {
        "folder_name": ref.folder_name,
        "filename": ref.filename,
        "url": LTX_MODEL_URLS[ref],
    }
    for ref in model_refs
]
json.dump(manifest, sys.stdout, indent=2, sort_keys=True)
sys.stdout.write("\n")
PY

az acr build \
  --registry "$ACR_NAME" \
  --image "$IMAGE_NAME:$IMAGE_TAG" \
  --timeout "$ACR_BUILD_TIMEOUT" \
  --build-arg "BASE_IMAGE=$BASE_IMAGE" \
  --build-arg "PRELOADED_MODELS_DIR=$PRELOADED_MODELS_DIR" \
  --file "$STAGING_DIR/context/Dockerfile.ltx-preloaded" \
  "$STAGING_DIR/context"

PRELOADED_IMAGE="$LOGIN_SERVER/$IMAGE_NAME:$IMAGE_TAG"
printf 'preloaded_image=%s\n' "$PRELOADED_IMAGE"
printf 'base_image=%s\n' "$BASE_IMAGE"

if [[ "$REGISTER_AZUREML_ENV" == "1" ]]; then
  ROOT_DIR="$ROOT_DIR" \
  PRELOADED_IMAGE="$PRELOADED_IMAGE" \
  BASE_IMAGE="$BASE_IMAGE" \
  IMAGE_TAG="$IMAGE_TAG" \
  LTX_ENV_NAME="$LTX_ENV_NAME" \
  WAN_ENV_NAME="$WAN_ENV_NAME" \
  REGISTER_WAN_ENV="$REGISTER_WAN_ENV" \
  python - <<'PY'
import os
import sys
from pathlib import Path

root = Path(os.environ["ROOT_DIR"]).resolve()
if str(root) not in sys.path:
    sys.path.insert(0, str(root))

from azure.ai.ml import MLClient
from azure.ai.ml.entities import Environment

from azureml.credential import build_credential
from azureml import defaults as aml_defaults

subscription_id = os.getenv("AZUREML_SUBSCRIPTION_ID", aml_defaults.DEFAULT_SUBSCRIPTION_ID)
resource_group = os.getenv("AZUREML_RESOURCE_GROUP", aml_defaults.DEFAULT_RESOURCE_GROUP)
workspace_name = os.getenv("AZUREML_WORKSPACE_NAME", aml_defaults.DEFAULT_WORKSPACE_NAME)

client = MLClient(
    credential=build_credential(),
    subscription_id=subscription_id,
    resource_group_name=resource_group,
    workspace_name=workspace_name,
)

version = os.environ["IMAGE_TAG"]
ltx_env = Environment(
    name=os.environ["LTX_ENV_NAME"],
    version=version,
    image=os.environ["PRELOADED_IMAGE"],
    description="ComfyUI LTX runtime with workflow-required models baked into the container image.",
)
created_ltx = client.environments.create_or_update(ltx_env)
print(f"ltx_environment_id={created_ltx.id}")

if os.getenv("REGISTER_WAN_ENV", "1") == "1":
    wan_env = Environment(
        name=os.environ["WAN_ENV_NAME"],
        version=version,
        image=os.environ["BASE_IMAGE"],
        description="ComfyUI WAN runtime aligned to the preloaded LTX environment version.",
    )
    created_wan = client.environments.create_or_update(wan_env)
    print(f"wan_environment_id={created_wan.id}")
PY
fi

if [[ "$WRITE_LATEST_ENV_VERSION" == "1" ]]; then
  printf '%s\n' "$IMAGE_TAG" > "$ROOT_DIR/azureml/.latest_env_version"
  printf 'updated_latest_env_version=%s\n' "$IMAGE_TAG"
fi
