#!/usr/bin/env bash
# Deploy a verified server artifact to the studio App Service, optionally building it first, without GitHub.
# The standalone equivalent of .github/workflows/deploy-server.yml (with --build, release.yml component server first).
# PowerShell twin: Deploy-StudioServer.ps1.
# shellcheck source-path=SCRIPTDIR source=lib/common.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib/common.sh"

usage() {
    cat <<EOF
Usage: scripts/release/deploy-server.sh (--artifact ID | --build --container-engine docker|podman|wslc) [options]

Runs scripts/ci/deploy_server.py: points the App Service at the server image by registry digest, starts it, waits for
/healthz and confirms /api/status requires sign-in (if not, the site is stopped). It never arms submissions and never
changes a region's release. The site must be disarmed (AzureMl__Armed=false) and have Auth__ClientId and Auth__TenantId
set first.

The server artifact ID is not the Release artifact ID: never paste it into Administration.

Deploy options:
  --artifact ID                The 64-character server artifact ID that build-artifact.sh --component server printed.
  --build                      Build, verify and seal the server image from this checkout first, then deploy it.
  --container-engine NAME      docker, podman or wslc. Required with --build; unused otherwise.
  --allow-dirty                With --build: build even with uncommitted changes.

$COMMON_OPTIONS_HELP

Examples:
  scripts/release/deploy-server.sh --settings ~/wan/settings.json --build --container-engine wslc
  scripts/release/deploy-server.sh --settings ~/wan/settings.json --artifact <server artifact id>
EOF
}

artifact="" build=0 allow_dirty=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        -h | --help) usage; exit 0 ;;
        --artifact)
            [[ ${2:-} =~ ^[a-f0-9]{64}$ ]] || usage_error "--artifact must be the 64-character lowercase hex server artifact ID."
            artifact=$2; shift 2 ;;
        --build) build=1; shift ;;
        --allow-dirty) allow_dirty=1; shift ;;
        *)
            parse_common_option "$1" "${2:-}" || usage_error "Unknown option $1."
            shift "$SHIFT_BY" ;;
    esac
done
if [[ -n $artifact && $build -eq 1 || -z $artifact && $build -eq 0 ]]; then
    usage_error "Pass exactly one of --artifact <server artifact ID> or --build."
fi
if [[ $build -eq 1 && -z $OPT_ENGINE ]]; then usage_error "--build needs --container-engine docker, podman or wslc."; fi

start_job deploy server "$OPT_SETTINGS"
if [[ $build -eq 1 ]]; then test_engine "$OPT_ENGINE"; identity_stage=build; else identity_stage=deploy; fi
connect_azure "$OPT_LOGIN" "$OPT_CLIENT_ID" "$OPT_TOKEN_FILE" "$OPT_CERTIFICATE" "$identity_stage"
test_network
if [[ $OPT_PREFLIGHT -eq 1 ]]; then step "Preflight passed. Nothing was built or deployed (--preflight-only)."; exit 0; fi
init_toolchain "$OPT_VENV"
if [[ $build -eq 1 ]]; then
    run_build "$allow_dirty" "" "" "" 0
    artifact=$BUILT_ARTIFACT
    note "Sealed server artifact $artifact."
fi
step "Deploying server artifact $artifact to the App Service in ${VALUES[CONTROL_REGION]}"
run_stage deploy_server.py --settings "$(native_path "$JOB_SETTINGS_FILE")" --artifact "$artifact" --region "${VALUES[CONTROL_REGION]}" ||
    die "Server deployment did not verify; see the output above."
show_banner deploy "$artifact"
