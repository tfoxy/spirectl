#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat >&2 <<'EOF'
Usage: scripts/verify-bridge-release.sh --zip <path> --version <x.y.z>

Validate a released bridge ZIP's layout and reject references, PDBs, and .NET
runtime metadata that must never be shipped with the bridge payload.
EOF
}

die() {
  echo "verify-bridge-release: $*" >&2
  exit 1
}

zip_path=""
version=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --zip)
      [[ $# -ge 2 ]] || die "--zip requires a value"
      zip_path="$2"
      shift 2
      ;;
    --version)
      [[ $# -ge 2 ]] || die "--version requires a value"
      version="$2"
      shift 2
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      usage
      die "unknown argument: $1"
      ;;
  esac
done

[[ -f "$zip_path" ]] || die "--zip must name an existing file"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "--version must be x.y.z"
unzip -tqq "$zip_path" || die "invalid ZIP archive: $zip_path"

work_dir="$(mktemp -d "${TMPDIR:-/tmp}/spirectl-bridge-verify.XXXXXX")"
entries="$work_dir/entries.txt"
trap 'rm -rf "$work_dir"' EXIT
unzip -Z1 "$zip_path" | LC_ALL=C sort > "$entries"

[[ "$(head -n 1 "$entries")" == "spirectlbridge/" ]] || die "archive must be rooted at spirectlbridge/"
while IFS= read -r entry; do
  [[ "$entry" == spirectlbridge/* ]] || die "archive contains entry outside spirectlbridge/: $entry"
  [[ "$entry" != *'\'* && "$entry" != *'../'* && "$entry" != ../* ]] || die "archive contains unsafe entry: $entry"
done < "$entries"

required_files=(
  spirectlbridge/spirectlbridge.json
  spirectlbridge/spirectlbridge.dll
  spirectlbridge/Spirectl.BridgeMod.Sts2Host.dll
  spirectlbridge/Spirectl.BridgeMod.dll
  spirectlbridge/Spirectl.Sts2.dll
)
for required in "${required_files[@]}"; do
  grep -Fxq "$required" "$entries" || die "archive is missing required payload file: $required"
done

forbidden_pattern='(^|/)(sts2|GodotSharp|0Harmony|MonoMod\.Backports|MonoMod\.ILHelpers|Sentry|SmartFormat|SmartFormat\.ZString|Steamworks\.NET)\.dll$|\.deps\.json$|\.runtimeconfig\.json$|\.pdb$'
while IFS= read -r entry; do
  [[ "$entry" =~ /$ ]] && continue
  [[ "$entry" =~ $forbidden_pattern ]] && die "archive contains forbidden reference or runtime artifact: $entry"
done < "$entries"

extract_dir="$work_dir/extracted"
unzip -qq "$zip_path" -d "$extract_dir"
payload_dir="$extract_dir/spirectlbridge"
content_hash="$({
  while IFS= read -r -d '' relative; do
    printf '%s\0' "$relative"
    sha256sum "$payload_dir/$relative" | awk '{print $1}'
  done < <(cd "$payload_dir" && find . -type f ! -name spirectlbridge.json -printf '%P\0' | LC_ALL=C sort -z)
} | sha256sum | awk '{print $1}')"

manifest="$(unzip -p "$zip_path" spirectlbridge/spirectlbridge.json)"
printf '%s' "$manifest" | jq -e --arg version "$version" --arg content_hash "$content_hash" '
  .id == "spirectlbridge"
  and .version == $version
  and .has_pck == false
  and .has_dll == true
  and .buildIdentity.bridgeSemVer == $version
  and .buildIdentity.bridgeVersion == ("spirectl-bridge/" + $version)
  and .buildIdentity.assemblyInformationalVersion == $version
  and .buildIdentity.contentHash == $content_hash
' >/dev/null || die "bridge manifest does not match version $version"

echo "verify-bridge-release: ok ($zip_path)"
