#!/usr/bin/env bash
# Builds the pinned, patched upstream code tree and uploads it to a blob folder that an Azure ML job mounts as its
# `repo` input (azureml/ and src/ at the folder root, plus LICENSE and NOTICE.txt).
#
# Usage: scripts/upload-wan-code.sh <storage-account> <container> <prefix> [workdir]
#   e.g. scripts/upload-wan-code.sh mystorageacct azureml-blobstore-<guid> code/dc0d29031b73-8ea1320533d2599f-wan
# Needs: bash, git, az (logged in; Storage Blob Data Contributor on the account). DRY_RUN=1 builds the tree but uploads nothing.
set -euo pipefail

account="${1:?usage: $0 <storage-account> <container> <prefix> [workdir]}"
container="${2:?container required}"
prefix="${3:?prefix required, e.g. code/<version>-wan}"
# Optional [workdir] defaults to a fresh folder on LOCAL disk. Network mounts (Azure ML /cloudfiles, SMB shares) break git.
auto_work=0
if [[ -z "${4:-}" ]]; then work="$(mktemp -d)"; auto_work=1; else work="$4"; fi
root="$(cd "$(dirname "$0")/.." && { pwd -W 2>/dev/null || pwd; })"

export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'
prefix="${prefix#/}"; prefix="${prefix%/}"
[[ "$account" =~ ^[a-z0-9]{3,24}$ ]] || { echo "Storage account must be the bare name, got: '$account'" >&2; exit 2; }
[[ "$container" =~ ^[a-z0-9]([a-z0-9-]{1,61}[a-z0-9])?$ ]] || { echo "Invalid container name: '$container'" >&2; exit 2; }
[[ "$prefix" =~ ^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$ ]] || { echo "Prefix must be folder names separated by '/', got: '$prefix'" >&2; exit 2; }

sha="$(sed -n 's/^UPSTREAM_SHA = "\([0-9a-f]\{40\}\)"/\1/p' "$root/app/cost_guard.py")"
rm -rf "$work"; mkdir -p "$work"; work="$(cd "$work" && { pwd -W 2>/dev/null || pwd; })"
[[ "${DRY_RUN:-}" != 1 && "$auto_work" == 1 ]] && trap 'rm -rf "$work"' EXIT
tree="$work/tree"
echo "Fetching upstream $sha into $work"
git clone -q --filter=blob:none --no-checkout https://github.com/jakeatmsft/azureml_vidgen_comfyui.git "$tree"
[[ -d "$tree/.git" ]] || { echo "git clone did not create $tree/.git. Is $work on a network share? Pass a local folder as the 4th argument, e.g. /tmp/wan-code" >&2; exit 1; }
git -C "$tree" config core.autocrlf false
git -C "$tree" checkout -q --detach "$sha"
git -C "$tree" apply --ignore-space-change "$root/app/upstream-cost.patch"
cp "$root/app/config.py" "$root/app/cost_guard.py" "$tree/azureml/"

# Only the code the job needs, never git metadata or model weights.
stage="$work/stage"; mkdir -p "$stage"
cp -r "$tree/azureml" "$tree/src" "$stage/"
cp "$tree/LICENSE"* "$tree/NOTICE.txt" "$stage/" 2>/dev/null || true
find "$stage" \( -name '.git*' -o -name '__pycache__' \) -prune -exec rm -rf {} + 2>/dev/null || true
echo "Staged $(find "$stage" -type f | wc -l | tr -d ' ') files ($(du -sh "$stage" | cut -f1)) in $stage"

echo "Target: https://${account}.blob.core.windows.net/${container}/${prefix}/"
[[ "${DRY_RUN:-}" == 1 ]] && exit 0
az storage blob upload-batch --account-name "$account" --auth-mode login \
    --destination "$container" --destination-path "$prefix" --source "$(cd "$stage" && { pwd -W 2>/dev/null || pwd; })" \
    --overwrite --only-show-errors

echo
echo "Done. In WAN_STUDIO_RELEASE_JSON set:  \"codeUri\": \"azureml://datastores/<datastoreName>/paths/${prefix}/\""
echo "(<datastoreName> must be the Azure ML datastore that points at ${account}/${container}.)"
