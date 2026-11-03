#!/usr/bin/env bash
# Build, verify and seal one studio artifact (the GPU release, the server image or the yolox trainer image) without GitHub.
# The standalone equivalent of .github/workflows/release.yml. PowerShell twin: Build-StudioArtifact.ps1.
# shellcheck source-path=SCRIPTDIR source=lib/common.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib/common.sh"

usage() {
    cat <<EOF
Usage: scripts/release/build-artifact.sh --container-engine docker|podman|wslc [--component gpu|server|yolox] [options]

Checks the settings document, the container engine, the Azure CLI sign-in (publisher principal) and private network
reach, installs the locked pipeline dependencies, builds the contract validator, then runs scripts/ci/build_image.py.
It ends by printing the artifact ID. For the GPU component that is the Release artifact ID: promote it with
promote-release.sh, then select it per region in Administration. Nothing is deployed and no GPU is allocated.

Build options:
  --component NAME             gpu (default), server or yolox.
  --container-engine NAME      docker, podman or wslc: builds, pushes and checks the image. Required.
  --upstream-source DIR        gpu: persistent cache of the pinned upstream source (STUDIO_UPSTREAM_SOURCE).
  --model-cache DIR            gpu: persistent cache of the pinned model weights, about 45 GB (STUDIO_MODEL_CACHE).
  --license-cache DIR          gpu: persistent cache of the pinned model cards (STUDIO_LICENSE_CACHE).
  --approve-trainer-weights    yolox: approve downloading the trainer's pinned release weights. Required for yolox.
  --allow-dirty                Build even with uncommitted changes (the artifact still records HEAD).

$COMMON_OPTIONS_HELP

Examples:
  scripts/release/build-artifact.sh --settings ~/wan/settings.json --container-engine podman
  scripts/release/build-artifact.sh --component server --settings ~/wan/settings.json --container-engine docker
EOF
}

component=gpu upstream="" models="" licenses="" approve=0 allow_dirty=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        -h | --help) usage; exit 0 ;;
        --component)
            case "${2:-}" in gpu | server | yolox) component=$2 ;; *) usage_error "--component must be gpu, server or yolox." ;; esac
            shift 2 ;;
        --upstream-source) [[ -n ${2:-} ]] || usage_error "$1 needs a value."; upstream=$2; shift 2 ;;
        --model-cache) [[ -n ${2:-} ]] || usage_error "$1 needs a value."; models=$2; shift 2 ;;
        --license-cache) [[ -n ${2:-} ]] || usage_error "$1 needs a value."; licenses=$2; shift 2 ;;
        --approve-trainer-weights) approve=1; shift ;;
        --allow-dirty) allow_dirty=1; shift ;;
        *)
            parse_common_option "$1" "${2:-}" || usage_error "Unknown option $1."
            shift "$SHIFT_BY" ;;
    esac
done
[[ -n $OPT_ENGINE ]] || usage_error "Pass --container-engine docker, podman or wslc."
if [[ $component != gpu && -n $upstream$models$licenses ]]; then usage_error "The cache folders apply only to --component gpu."; fi
if [[ $component == yolox && $approve -ne 1 ]]; then
    usage_error "The yolox build downloads the trainer's pinned release weights; approve that with --approve-trainer-weights."
fi

start_job build "$component" "$OPT_SETTINGS"
test_engine "$OPT_ENGINE"
if [[ $component != server ]]; then resolve_feed; fi
connect_azure "$OPT_LOGIN" "$OPT_CLIENT_ID" "$OPT_TOKEN_FILE" "$OPT_CERTIFICATE"
test_network
if [[ $OPT_PREFLIGHT -eq 1 ]]; then step "Preflight passed. Nothing was built (--preflight-only)."; exit 0; fi
init_toolchain "$OPT_VENV"
run_build "$allow_dirty" "$upstream" "$models" "$licenses" "$approve"
show_banner build "$BUILT_ARTIFACT"
