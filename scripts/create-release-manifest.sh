#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "usage: scripts/create-release-manifest.sh --version <MAJOR.MINOR.PATCH> --output-dir <dir>" >&2
  exit 2
}

version=""
output_dir=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) version="${2:-}"; shift 2 ;;
    --output-dir) output_dir="${2:-}"; shift 2 ;;
    *) usage ;;
  esac
done

[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ && -n "$output_dir" ]] || usage

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

assets=(
  "sts2-v${version}-x86_64-unknown-linux-gnu"
  "sts2-v${version}-x86_64-unknown-linux-gnu.tar.gz"
  "sts2-v${version}-x86_64-pc-windows-msvc.exe"
  "sts2-v${version}-x86_64-pc-windows-msvc.zip"
)

# One bridge payload per RELEASABLE STS2 API lane, because the bridge binds game
# members that were renamed between game builds. The lane list comes from
# bridge-mod/Sts2GameApi.props, never from a copy of it here.
while IFS= read -r lane; do
  assets+=(
    "spirectlbridge-v${version}-${lane}.zip"
    "spirectlbridge-v${version}-${lane}.manifest.json"
  )
done < <("$repo_root/scripts/sts2-api-lanes.sh" --releasable)

for asset in "${assets[@]}"; do
  [[ -f "$output_dir/$asset" ]] || {
    echo "release asset is missing: $output_dir/$asset" >&2
    exit 1
  }
done

asset_json="$({
  for asset in "${assets[@]}"; do
    jq -cn \
      --arg name "$asset" \
      --arg sha256 "$(sha256sum "$output_dir/$asset" | cut -d ' ' -f 1)" \
      --argjson size "$(stat -c %s "$output_dir/$asset")" \
      '{name: $name, sha256: $sha256, size: $size}'
  done
} | jq -s '.')"

jq -n \
  --arg schema 'spirectl-release/v1' \
  --arg version "$version" \
  --argjson assets "$asset_json" \
  '{schemaVersion: $schema, version: $version, assets: $assets}' \
  > "$output_dir/spirectl-v${version}-release-manifest.json"

(
  cd "$output_dir"
  sha256sum "${assets[@]}" "spirectl-v${version}-release-manifest.json" > "spirectl-v${version}-SHA256SUMS"
)
