# shellcheck shell=bash
# The OPT_*, STAGE_LINES, SHIFT_BY and COMMON_OPTIONS_HELP variables are read by the scripts that source this file.
# shellcheck disable=SC2034
# Shared implementation of the standalone release scripts (build-artifact.sh, promote-release.sh, deploy-server.sh).
# It reproduces what the GitHub workflows' private-context action does before a stage runs: materialize the settings
# document outside the checkout, sign in to Azure CLI, build the contract validator, install the locked pipeline
# dependencies into a virtual environment, isolate the job's credentials and remove them afterwards.
# scripts/release/StudioRelease.psm1 is the PowerShell twin of this file; keep them at parity. Needs bash 4.4 or later.

set -euo pipefail

STUDIO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)
STUDIO_HELPER="$STUDIO_ROOT/scripts/release/studio_release.py"
declare -A VALUES=()
JOB_WORK=""
JOB_KEEP=0
JOB_SETTINGS_FILE=""
JOB_SETTINGS_ARGUMENT=""
JOB_STAGE=""
JOB_COMPONENT="gpu"
JOB_REGIONS=()
SYSTEM_PYTHON=""
STAGE_PYTHON=""
STAGE_LINES=""
BUILT_ARTIFACT=""

step() { printf '\033[36m==> %s\033[0m\n' "$*"; }
note() { printf '    %s\n' "$*"; }
die() { printf '\033[31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }
usage_error() { printf 'ERROR: %s\nRun with --help for usage.\n' "$*" >&2; exit 2; }

# A path the native tools (Python, dotnet, az) can open: on Git Bash for Windows they need the Windows form.
native_path() {
    if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s\n' "$1"; fi
}

absolute_path() {
    case "$1" in
        /* | [A-Za-z]:*) printf '%s\n' "$1" ;;
        *) printf '%s/%s\n' "$PWD" "$1" ;;
    esac
}

find_python() {
    local candidate version
    local candidates=("$@")
    [[ ${#candidates[@]} -gt 0 ]] || candidates=(python3.12 python3.11 python3 python)
    for candidate in "${candidates[@]}"; do
        command -v "$candidate" >/dev/null 2>&1 || continue
        version=$("$candidate" -c 'import sys; print("%d.%d" % sys.version_info[:2])' 2>/dev/null || true)
        version=${version%$'\r'}
        if [[ $version == 3.11 || $version == 3.12 ]]; then
            command -v "$candidate"
            return 0
        fi
        [[ $# -eq 0 ]] || die "Python $candidate is $version; the pipeline needs Python 3.12 (or 3.11)."
    done
    die "Python 3.12 (or 3.11) was not found. Install it or pass --python <path>."
}

# Run one studio_release.py subcommand; its KEY=VALUE lines land in VALUES, its explanation of a refusal on stderr.
helper() {
    local output line
    output=$("$SYSTEM_PYTHON" "$(native_path "$STUDIO_HELPER")" "$@") || die "Preflight failed: $1 (see the error above)."
    while IFS= read -r line; do
        line=${line%$'\r'}
        if [[ $line == *=* ]]; then VALUES["${line%%=*}"]="${line#*=}"; fi
    done <<<"$output"
}

cleanup_job() {
    local status=$?
    set +e
    local log="${STUDIO_REGISTRY_LOG:-}"
    if [[ -n $log && -f $log ]]; then
        local cli="" candidate registry output
        local failed=()
        case "${STUDIO_CONTAINER_ENGINE:-wslc}" in
            wslc)
                for candidate in "${STUDIO_WSLC:-}" "$(command -v wslc.exe)" "$(command -v wslc)" "/mnt/c/Program Files/WSL/wslc.exe"; do
                    if [[ -n $candidate && -f $candidate ]]; then cli=$candidate; break; fi
                done ;;
            podman) cli="${STUDIO_PODMAN:-$(command -v podman)}" ;;
            docker) cli="${STUDIO_DOCKER:-$(command -v docker)}" ;;
        esac
        while IFS= read -r registry; do
            registry=${registry//[[:space:]]/}
            [[ -n $registry ]] || continue
            if [[ -z $cli ]]; then failed+=("$registry"); continue; fi
            if output=$("$cli" logout "$registry" 2>&1) || [[ $output == *[Nn]"ot logged in"* ]]; then
                echo "Logged out of $registry."
            else
                echo "WARNING: ${STUDIO_CONTAINER_ENGINE:-wslc} logout $registry failed: $output" >&2
                failed+=("$registry")
            fi
        done < <(sort -u "$log")
        rm -f "$log"
        if [[ ${#failed[@]} -gt 0 ]]; then
            echo "ERROR: registry login(s) not removed: ${failed[*]}. Run '<engine> logout <registry>' yourself." >&2
            status=1
        fi
    fi
    if [[ -n $JOB_WORK && -d $JOB_WORK ]]; then
        rm -rf "$JOB_WORK/studio-azure"
        if [[ $JOB_KEEP -eq 1 ]]; then
            echo "Job folder kept: $JOB_WORK (it holds the settings document; delete it when done)."
        else
            rm -rf "$JOB_WORK"
        fi
    fi
    exit "$status"
}

# new_job STAGE COMPONENT SETTINGS [REGION...]
new_job() {
    JOB_STAGE=$1 JOB_COMPONENT=$2 JOB_SETTINGS_ARGUMENT=$3
    shift 3
    local tool region
    for tool in az dotnet git; do
        command -v "$tool" >/dev/null 2>&1 || die "$tool was not found on PATH; it is required."
    done
    # Variables that would turn on CI-only behavior (federated identity checks, OIDC relogin) or a stale client binding.
    unset GITHUB_ACTIONS STUDIO_FEDERATED_IDENTITY AZURE_CLIENT_ID ACTIONS_ID_TOKEN_REQUEST_URL ACTIONS_ID_TOKEN_REQUEST_TOKEN \
          GITHUB_SHA GITHUB_RUN_ID GITHUB_RUN_ATTEMPT GITHUB_OUTPUT
    JOB_WORK=$(mktemp -d "${TMPDIR:-/tmp}/wan-studio-$JOB_STAGE-XXXXXXXX")
    chmod 700 "$JOB_WORK"
    trap cleanup_job EXIT
    export STUDIO_REGISTRY_LOG="$JOB_WORK/studio-registries"
    JOB_SETTINGS_FILE="$JOB_WORK/settings.json"

    step "Checking the settings document"
    local arguments=(settings --out "$(native_path "$JOB_SETTINGS_FILE")" --stage "$JOB_STAGE")
    if [[ -n $JOB_SETTINGS_ARGUMENT ]]; then
        arguments+=(--settings "$(native_path "$(absolute_path "$JOB_SETTINGS_ARGUMENT")")")
    fi
    for region in "$@"; do arguments+=(--region "$region"); done
    helper "${arguments[@]}"
    read -r -a JOB_REGIONS <<<"${VALUES[REGIONS]}"
    note "tenant ${VALUES[TENANT_ID]}, subscription ${VALUES[SUBSCRIPTION_ID]}, control region ${VALUES[CONTROL_REGION]}"
}

# connect_azure LOGIN CLIENT_ID FEDERATED_TOKEN_FILE CERTIFICATE [IDENTITY_STAGE]
connect_azure() {
    local login=$1 client_id=$2 token_file=$3 certificate=$4 identity_stage=${5:-$JOB_STAGE}
    local tenant=${VALUES[TENANT_ID]} subscription=${VALUES[SUBSCRIPTION_ID]}
    if [[ $login != current ]]; then
        export AZURE_CONFIG_DIR="$JOB_WORK/studio-azure"
    fi
    step "Signing in to Azure CLI ($login)"
    case "$login" in
        current)
            local current
            current=$(az account show --query id -o tsv --only-show-errors 2>/dev/null) ||
                die "Azure CLI is not signed in. Run az login as the publisher principal, or pass --login interactive, device-code, managed-identity or service-principal."
            current=${current%$'\r'}
            [[ $current == "$subscription" ]] || note "Selecting subscription $subscription in your current Azure CLI profile (it was $current)."
            ;;
        interactive) az login --tenant "$tenant" --only-show-errors -o none || die "az login failed." ;;
        device-code) az login --tenant "$tenant" --use-device-code --only-show-errors -o none || die "az login failed." ;;
        managed-identity)
            local identity=(--identity)
            [[ -z $client_id ]] || identity+=(--client-id "$client_id")
            az login "${identity[@]}" --only-show-errors -o none || die "az login --identity failed."
            ;;
        service-principal)
            [[ -n $client_id ]] || usage_error "--login service-principal needs --client-id (the application/client ID)."
            if [[ -n $token_file && -z $certificate ]]; then
                az login --service-principal --username "$client_id" --tenant "$tenant" \
                    --federated-token "$(<"$token_file")" --only-show-errors -o none || die "az login failed."
            elif [[ -z $token_file && -n $certificate ]]; then
                az login --service-principal --username "$client_id" --tenant "$tenant" \
                    --certificate "$(native_path "$(absolute_path "$certificate")")" --only-show-errors -o none || die "az login failed."
            else
                usage_error "--login service-principal needs exactly one of --federated-token-file or --certificate. Client secrets are not supported here; sign in yourself and use --login current if you must use one."
            fi
            ;;
        *) usage_error "Unknown --login $login." ;;
    esac
    az account set --subscription "$subscription" --only-show-errors ||
        die "The signed-in identity cannot select subscription $subscription."
    helper identity --settings "$(native_path "$JOB_SETTINGS_FILE")" --stage "$identity_stage"
    note "Azure CLI caller: ${VALUES[CALLER]} (${VALUES[CALLER_TYPE]})"
}

test_network() {
    step "Checking private DNS and reachability of the studio endpoints"
    local arguments=(network --settings "$(native_path "$JOB_SETTINGS_FILE")" --stage "$JOB_STAGE" --component "$JOB_COMPONENT")
    local region
    for region in "${JOB_REGIONS[@]}"; do arguments+=(--region "$region"); done
    helper "${arguments[@]}"
}

test_engine() {
    local engine=${1:-}
    [[ -n $engine ]] || usage_error "Building needs --container-engine docker, podman or wslc (the engine that builds, pushes and checks the image)."
    step "Checking container engine $engine"
    helper engine --engine "$engine"
    note "${VALUES[ENGINE_CLI]}: ${VALUES[ENGINE_VERSION]}"
    export STUDIO_CONTAINER_ENGINE="$engine"
}

resolve_feed() {
    local url=${PIP_INDEX_URL:-}
    [[ -n $url ]] || url=$("$SYSTEM_PYTHON" -m pip config get global.index-url 2>/dev/null | head -n 1 || true)
    helper feed --url "${url%$'\r'}"
    export PIP_INDEX_URL="${VALUES[PIP_INDEX_URL]}"
}

init_toolchain() {
    local venv=${1:-$STUDIO_ROOT/.artifacts/release-venv}
    venv=$(absolute_path "$venv")
    if [[ ! -x $venv/bin/python && ! -f $venv/Scripts/python.exe ]]; then
        step "Creating the pipeline virtual environment in $venv"
        "$SYSTEM_PYTHON" -m venv "$(native_path "$venv")" || die "Could not create a virtual environment in $venv."
    fi
    if [[ -x $venv/bin/python ]]; then STAGE_PYTHON="$venv/bin/python"; else STAGE_PYTHON="$venv/Scripts/python.exe"; fi
    local lock stamp=""
    local locks=("$STUDIO_ROOT/scripts/ci/requirements.lock" "$STUDIO_ROOT/build/gpu/requirements-build.lock")
    for lock in "${locks[@]}"; do
        stamp+=$("$SYSTEM_PYTHON" -c 'import hashlib,sys; print(hashlib.sha256(open(sys.argv[1],"rb").read()).hexdigest(), end=",")' \
                 "$(native_path "$lock")")
    done
    if [[ ! -f $venv/.studio-locks || $(<"$venv/.studio-locks") != "$stamp" ]]; then
        step "Installing the locked pipeline dependencies (hash-checked, from your configured pip index)"
        for lock in "${locks[@]}"; do
            "$STAGE_PYTHON" -m pip install --quiet --disable-pip-version-check --require-hashes -r "$(native_path "$lock")" ||
                die "Installing $lock failed. Check that pip's index (pip config list) is your approved feed and reachable."
        done
        printf '%s' "$stamp" >"$venv/.studio-locks"
    fi
    step "Building the shared contract validator"
    local contracts="$STUDIO_ROOT/.artifacts/release-contracts"
    dotnet build "$(native_path "$STUDIO_ROOT/tools/VideoSafetyStudio.Contracts")" --artifacts-path "$(native_path "$contracts")" -v quiet -nologo ||
        die "Building tools/VideoSafetyStudio.Contracts failed."
    STUDIO_CONTRACT_TOOL=$(native_path "$contracts/bin/VideoSafetyStudio.Contracts/debug/VideoSafetyStudio.Contracts.dll")
    export STUDIO_CONTRACT_TOOL
}

# run_stage SCRIPT ARGS...: streams the stage's output and keeps it in STAGE_LINES; returns the stage's exit code.
run_stage() {
    local script=$1
    shift
    local log="$JOB_WORK/stage.log" status=0
    "$STAGE_PYTHON" "$(native_path "$STUDIO_ROOT/scripts/ci/$script")" "$@" 2>&1 | tee "$log" || status=$?
    STAGE_LINES=$(<"$log")
    return "$status"
}

# run_build ALLOW_DIRTY UPSTREAM MODELS LICENSES APPROVE_TRAINER: sets BUILT_ARTIFACT.
run_build() {
    local allow_dirty=$1 upstream=$2 models=$3 licenses=$4 approve=$5 commit
    commit=$(git -C "$STUDIO_ROOT" rev-parse HEAD) || die "The build must run from a git checkout of the studio."
    commit=${commit%$'\r'}
    [[ $commit =~ ^[a-f0-9]{40}$ ]] || die "The build must run from a git checkout of the studio."
    if [[ -n $(git -C "$STUDIO_ROOT" status --porcelain --untracked-files=no) ]]; then
        [[ $allow_dirty -eq 1 ]] ||
            die "The checkout has uncommitted changes, but the artifact records commit $commit as its source. Commit or stash them, or pass --allow-dirty."
        echo "WARNING: building with uncommitted changes; the artifact still records commit $commit." >&2
    fi
    export STUDIO_BUILD_SOURCE_SHA="$commit"
    STUDIO_BUILD_RUN_ID=$(date -u +%Y%m%d%H%M%S)
    export STUDIO_BUILD_RUN_ID STUDIO_BUILD_RUN_ATTEMPT=1
    local output="$JOB_WORK/build-output.txt"
    STUDIO_BUILD_OUTPUT=$(native_path "$output")
    export STUDIO_BUILD_OUTPUT
    if [[ -n $upstream ]]; then STUDIO_UPSTREAM_SOURCE=$(absolute_path "$upstream"); export STUDIO_UPSTREAM_SOURCE; fi
    if [[ -n $models ]]; then STUDIO_MODEL_CACHE=$(absolute_path "$models"); export STUDIO_MODEL_CACHE; fi
    if [[ -n $licenses ]]; then STUDIO_LICENSE_CACHE=$(absolute_path "$licenses"); export STUDIO_LICENSE_CACHE; fi
    if [[ $approve -eq 1 ]]; then export STUDIO_APPROVE_TRAINER_WEIGHTS=true; fi
    step "Building, verifying and sealing the $JOB_COMPONENT artifact from commit $commit"
    run_stage build_image.py --component "$JOB_COMPONENT" --settings "$(native_path "$JOB_SETTINGS_FILE")" \
        --work "$(native_path "$JOB_WORK/build")" ||
        die "The $JOB_COMPONENT build did not seal an artifact; see the output above."
    BUILT_ARTIFACT=$(sed -n 's/^artifact=\([a-f0-9]\{64\}\).*$/\1/p' "$output" 2>/dev/null | tail -n 1 || true)
    [[ -n $BUILT_ARTIFACT ]] || die "The build finished but reported no artifact ID."
}

# show_banner STAGE ARTIFACT [--ok REGION]... [--failed REGION]...
show_banner() {
    local stage=$1 artifact=$2
    shift 2
    local arguments=(banner --stage "$stage" --component "$JOB_COMPONENT" --artifact "$artifact" --shell bash)
    if [[ -n $JOB_SETTINGS_ARGUMENT ]]; then arguments+=(--settings "$JOB_SETTINGS_ARGUMENT"); fi
    "$SYSTEM_PYTHON" "$(native_path "$STUDIO_HELPER")" "${arguments[@]}" "$@"
}

# Options every script shares. parse_common_option NAME [VALUE]: sets the option and SHIFT_BY, or returns 1.
OPT_SETTINGS="" OPT_LOGIN=current OPT_CLIENT_ID="" OPT_TOKEN_FILE="" OPT_CERTIFICATE="" OPT_PYTHON="" OPT_VENV=""
OPT_ENGINE="" OPT_PREFLIGHT=0 OPT_KEEP=0 SHIFT_BY=1
parse_common_option() {
    local name=$1 value=${2:-}
    SHIFT_BY=2
    case "$name" in
        --settings | --login | --client-id | --federated-token-file | --certificate | --python | --venv | --container-engine)
            [[ -n $value ]] || usage_error "$name needs a value." ;;
    esac
    case "$name" in
        --settings) OPT_SETTINGS=$value ;;
        --login)
            case "$value" in
                current | interactive | device-code | managed-identity | service-principal) OPT_LOGIN=$value ;;
                *) usage_error "--login must be current, interactive, device-code, managed-identity or service-principal." ;;
            esac ;;
        --client-id) OPT_CLIENT_ID=$value ;;
        --federated-token-file) OPT_TOKEN_FILE=$value ;;
        --certificate) OPT_CERTIFICATE=$value ;;
        --python) OPT_PYTHON=$value ;;
        --venv) OPT_VENV=$value ;;
        --container-engine)
            case "$value" in
                docker | podman | wslc) OPT_ENGINE=$value ;;
                *) usage_error "--container-engine must be docker, podman or wslc." ;;
            esac ;;
        --preflight-only) OPT_PREFLIGHT=1 SHIFT_BY=1 ;;
        --keep-work) OPT_KEEP=1 SHIFT_BY=1 ;;
        *) return 1 ;;
    esac
}

# Starts a job with the common options already parsed: finds Python and remembers --keep-work.
start_job() {
    if [[ -n $OPT_PYTHON ]]; then SYSTEM_PYTHON=$(find_python "$OPT_PYTHON"); else SYSTEM_PYTHON=$(find_python); fi
    JOB_KEEP=$OPT_KEEP
    new_job "$@"
}

COMMON_OPTIONS_HELP='Common options:
  --settings PATH              Settings document (Studio and Pipeline sections), kept outside the checkout.
                               Defaults to STUDIO_SETTINGS_PATH or STUDIO_SETTINGS_JSON.
  --login MODE                 current (default: your existing az login), interactive, device-code,
                               managed-identity or service-principal. Every mode except current uses a
                               job-only Azure CLI profile that is deleted at the end.
  --client-id ID               Client ID for managed-identity (user-assigned) or service-principal.
  --federated-token-file PATH  service-principal: file holding a federated (workload identity) token.
  --certificate PATH           service-principal: PEM certificate with its private key.
  --python PATH                Python 3.12 (or 3.11) for the virtual environment. Default: first on PATH.
  --venv DIR                   Virtual environment. Default: .artifacts/release-venv in the checkout.
  --preflight-only             Run every check and stop before changing anything.
  --keep-work                  Keep the job folder (holds a copy of the settings) for troubleshooting.
  -h, --help                   Show this help.

Azure CLI must be signed in as Pipeline.PublisherPrincipalId to build or promote. Run from a machine that
resolves the studio private DNS zones and reaches its private endpoints.'
