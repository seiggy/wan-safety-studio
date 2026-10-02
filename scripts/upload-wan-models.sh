#!/usr/bin/env bash
# Downloads the four pinned WAN 2.2 model files from Hugging Face, verifies size and SHA-256 against
# app/wan-pinned-manifest.json, and uploads them to a blob container with Microsoft Entra sign-in.
#
# Usage: scripts/upload-wan-models.sh <storage-account> [container=wan-studio] [prefix=models/wan] [workdir=./wan-models]
# Needs: bash, curl, python3, sha256sum, az (logged in: `az login`; your user needs Storage Blob Data Contributor).
# DRY_RUN=1 prints the plan without downloading or uploading.
set -euo pipefail

account="${1:?usage: $0 <storage-account> [container] [prefix] [workdir]}"
container="${2:-wan-studio}"
prefix="${3:-models/wan}"
work="${4:-./wan-models}"
manifest="${MANIFEST:-$(cd "$(dirname "$0")/.." && pwd)/app/wan-pinned-manifest.json}"

# Git Bash on Windows rewrites path-like arguments before az sees them.
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'
prefix="${prefix#/}"; prefix="${prefix%/}"
mkdir -p "$work"
# Native Windows tools (curl, az) can't open /c/... paths; Git Bash's `pwd -W` gives C:/... which works for everything.
work="$(cd "$work" && { pwd -W 2>/dev/null || pwd; })"

# Fail here, with the offending value, instead of with Azure's generic "invalid characters".
[[ "$account" =~ ^[a-z0-9]{3,24}$ ]] || { echo "Storage account must be the bare name (3-24 lowercase letters/digits), got: '$account'" >&2; exit 2; }
[[ "$container" =~ ^[a-z0-9]([a-z0-9-]{1,61}[a-z0-9])?$ ]] || { echo "Container must be 3-63 lowercase letters, digits or hyphens, got: '$container'" >&2; exit 2; }
[[ "$prefix" =~ ^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$ ]] || { echo "Prefix must be folder names separated by '/', got: '$prefix'" >&2; exit 2; }

# One tab-separated line per file: folder, filename, size, sha256, url.
py=python3; "$py" -c '' 2>/dev/null || py=python
entries="$("$py" -c '
import json, sys
for e in json.load(sys.stdin):
    print("\t".join([e["folder_name"], e["filename"], str(e["size"]), e["sha256"], e["url"]]))
' < "$manifest")"

echo "Target: https://${account}.blob.core.windows.net/${container}/${prefix}/"
while IFS=$'\t' read -r folder name size sha url; do
    dest="${work}/${folder}/${name}"
    if [[ "${DRY_RUN:-}" == 1 ]]; then
        echo "would fetch ${folder}/${name} ($((size / 1048576)) MiB) -> ${prefix}/${folder}/${name}"
        continue
    fi
    mkdir -p "${work}/${folder}"
    have=0; [[ -f "$dest" ]] && have="$(wc -c < "$dest")"
    if [[ "$have" != "$size" ]]; then
        echo "Downloading ${folder}/${name} ($((size / 1048576)) MiB)"
        curl -L --fail --retry 5 -C - --progress-bar -o "$dest" "$url"
        rm -f "$dest.verified"
    fi
    # Hashing a 14 GB file takes minutes; remember a passed check so reruns skip it.
    if [[ ! -f "$dest.verified" ]]; then
        echo "Verifying SHA-256 of ${name} (a few minutes for large files)..."
        echo "${sha}  ${dest}" | sha256sum -c - || { echo "Checksum mismatch for ${name}; delete it and rerun." >&2; exit 1; }
        touch "$dest.verified"
    else
        echo "Already verified: ${folder}/${name}"
    fi
done <<< "$entries"

[[ "${DRY_RUN:-}" == 1 ]] && exit 0

echo "Uploading to ${account}/${container}/${prefix}/ (this can take a long time; az shows no per-file progress)"
az storage blob upload-batch --account-name "$account" --auth-mode login \
    --destination "$container" --destination-path "$prefix" --source "$work" \
    --pattern '*.safetensors' --overwrite --only-show-errors

echo
echo "Done. Register a model asset over datastore path: ${prefix}/ (it must contain diffusion_models/, text_encoders/, vae/)."
