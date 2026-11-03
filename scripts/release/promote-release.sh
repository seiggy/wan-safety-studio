#!/usr/bin/env bash
# Promote a sealed GPU release to one, several or all configured regions without GitHub.
# The standalone equivalent of .github/workflows/promote.yml. PowerShell twin: Publish-StudioRelease.ps1.
# shellcheck source-path=SCRIPTDIR source=lib/common.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib/common.sh"

usage() {
    cat <<EOF
Usage: scripts/release/promote-release.sh --artifact ID [--region NAME]... [options]

For each region runs scripts/ci/publish_region.py: copies the image by
digest with az acr import, copies and verifies every blob, registers the Azure ML assets and writes the create-only
regional binding last. No GPU is allocated. Promotion is idempotent and resumable: rerunning the same artifact for a
region is safe, and a failed region does not stop the others.

It ends by printing the Release artifact ID and the regions it was promoted to. Then, in Administration, paste it into
each region's 'Release artifact ID' box and choose 'Select release' (the region must be Off; selecting starts no compute).

Promote options:
  --artifact ID                The 64-character artifact ID the build printed. Required.
  --region NAME                A region key from Studio.Regions; repeat for several. Default: every configured region.
  --container-engine NAME      docker, podman or wslc. Accepted for symmetry and unused: promotion copies images inside
                               Azure (az acr import) and runs no container.

$COMMON_OPTIONS_HELP

Examples:
  scripts/release/promote-release.sh --settings ~/wan/settings.json --artifact c26383a8b01bfb720c7fc9774d60af808fc7de7ed7a4f2a57675f33d81736841
  scripts/release/promote-release.sh --settings ~/wan/settings.json --artifact <id> --region westeurope
EOF
}

artifact="" component=gpu
regions=()
while [[ $# -gt 0 ]]; do
    case "$1" in
        -h | --help) usage; exit 0 ;;
        --artifact)
            [[ ${2:-} =~ ^[a-f0-9]{64}$ ]] || usage_error "--artifact must be the 64-character lowercase hex artifact ID."
            artifact=$2; shift 2 ;;
        --component)
            case "${2:-}" in gpu | yolox) component=$2 ;; *) usage_error "--component must be gpu or yolox." ;; esac
            shift 2 ;;
        --region) [[ -n ${2:-} ]] || usage_error "--region needs a value."; regions+=("$2"); shift 2 ;;
        *)
            parse_common_option "$1" "${2:-}" || usage_error "Unknown option $1."
            shift "$SHIFT_BY" ;;
    esac
done
[[ -n $artifact ]] || usage_error "Pass --artifact <64-character artifact ID> (the ID the build printed)."

start_job promote "$component" "$OPT_SETTINGS" "${regions[@]}"
note "promoting $component artifact $artifact to: ${JOB_REGIONS[*]}"
connect_azure "$OPT_LOGIN" "$OPT_CLIENT_ID" "$OPT_TOKEN_FILE" "$OPT_CERTIFICATE"
test_network
if [[ $OPT_PREFLIGHT -eq 1 ]]; then step "Preflight passed. Nothing was promoted (--preflight-only)."; exit 0; fi
init_toolchain "$OPT_VENV"
script=publish_region.py marker="Verified regional binding: "
if [[ $component == yolox ]]; then script=publish_trainer.py marker="Verified trainer binding: "; fi
banner_arguments=()
any_failed=0
for region in "${JOB_REGIONS[@]}"; do
    step "Promoting to $region"
    if run_stage "$script" --settings "$(native_path "$JOB_SETTINGS_FILE")" --artifact "$artifact" --region "$region" &&
        grep -q "^$marker" <<<"$STAGE_LINES"; then
        banner_arguments+=(--ok "$region")
    else
        note "$region was not promoted; it stays unavailable for this release."
        banner_arguments+=(--failed "$region")
        any_failed=1
    fi
done
show_banner promote "$artifact" "${banner_arguments[@]}"
exit "$any_failed"
