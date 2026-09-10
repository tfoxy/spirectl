#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat >&2 <<'EOF'
Usage: scripts/package-bridge-release.sh --version <x.y.z> --output-dir <dir>

Build a release bridge against the locked, build-only STS2 reference SDK. The
output directory receives spirectlbridge-v<version>.zip, its sidecar manifest,
and SHA256SUMS. No game or reference assemblies are included in the archive.
EOF
}

die() {
  echo "package-bridge-release: $*" >&2
  exit 1
}

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version=""
output_dir=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)
      [[ $# -ge 2 ]] || die "--version requires a value"
      version="$2"
      shift 2
      ;;
    --output-dir)
      [[ $# -ge 2 ]] || die "--output-dir requires a value"
      output_dir="$2"
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

[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "--version must be x.y.z"
[[ -n "$output_dir" ]] || die "--output-dir is required"
mkdir -p "$output_dir"
output_dir="$(cd "$output_dir" && pwd)"

reference_project="$repo_root/eng/Sts2.ReferenceSdk/Sts2.ReferenceSdk.csproj"
loader_project="$repo_root/bridge-mod/src/Spirectl.BridgeMod.Sts2Host.Loader/Spirectl.BridgeMod.Sts2Host.Loader.csproj"
host_project="$repo_root/bridge-mod/src/Spirectl.BridgeMod.Sts2Host/Spirectl.BridgeMod.Sts2Host.csproj"
[[ -f "$reference_project" && -f "$loader_project" && -f "$host_project" ]] || die "bridge build inputs are missing"

work_dir="$(mktemp -d "${TMPDIR:-/tmp}/spirectl-bridge-release.XXXXXX")"
trap 'rm -rf "$work_dir"' EXIT
reference_dir="$work_dir/reference-sdk"
loader_dir="$work_dir/loader"
host_dir="$work_dir/host"
payload_root="$work_dir/payload"
payload_dir="$payload_root/spirectlbridge"
mkdir -p "$reference_dir" "$loader_dir" "$host_dir" "$payload_dir"

# Pin timestamps used both by MSBuild metadata and the ZIP. The default is the
# earliest timestamp representable by ZIP; callers can opt into another epoch.
source_date_epoch="${SOURCE_DATE_EPOCH:-315532800}"
[[ "$source_date_epoch" =~ ^[0-9]+$ ]] && (( source_date_epoch >= 315532800 )) || \
  die "SOURCE_DATE_EPOCH must be an integer at or after 315532800"
built_at_utc="$(date -u -d "@$source_date_epoch" '+%Y-%m-%dT%H:%M:%S.0000000Z')"
build_properties=(
  "-p:Sts2AssembliesDir=$reference_dir"
  '-p:EnableSts2LiveHost=true'
  "-p:Version=$version"
  "-p:AssemblyVersion=$version.0"
  "-p:FileVersion=$version.0"
  "-p:InformationalVersion=$version"
  "-p:SpirectlBridgeBuiltAtUtc=$built_at_utc"
  '-p:ContinuousIntegrationBuild=true'
  '-p:DebugSymbols=false'
  '-p:DebugType=None'
)

"$repo_root/scripts/verify-sts2-reference-sdk.sh"
dotnet build "$reference_project" --no-restore -m:1 --configuration Release --output "$reference_dir" \
  -p:ContinuousIntegrationBuild=true -p:DebugSymbols=false -p:DebugType=None

dotnet restore "$loader_project" -m:1
dotnet restore "$host_project" -m:1
dotnet build "$loader_project" --no-restore -m:1 --configuration Release --output "$loader_dir" "${build_properties[@]}"
dotnet publish "$host_project" --no-restore -m:1 --configuration Release --output "$host_dir" "${build_properties[@]}"

loader_dll="$loader_dir/spirectlbridge.dll"
[[ -f "$loader_dll" ]] || die "loader build did not produce spirectlbridge.dll"
cp "$loader_dll" "$payload_dir/"

while IFS= read -r -d '' source; do
  cp "$source" "$payload_dir/"
done < <(find "$host_dir" -maxdepth 1 -type f -name '*.dll' -print0 | LC_ALL=C sort -z)

required_files=(
  spirectlbridge.dll
  Spirectl.BridgeMod.Sts2Host.dll
  Spirectl.BridgeMod.dll
  Spirectl.Sts2.dll
)
for required in "${required_files[@]}"; do
  [[ -f "$payload_dir/$required" ]] || die "bridge publish did not produce required payload file: $required"
done

forbidden_pattern='(^|/)(sts2|GodotSharp|0Harmony|MonoMod\.Backports|MonoMod\.ILHelpers|Sentry|SmartFormat|SmartFormat\.ZString|Steamworks\.NET)\.dll$|\.deps\.json$|\.runtimeconfig\.json$|\.pdb$'
while IFS= read -r -d '' source; do
  name="$(basename "$source")"
  if [[ "$name" =~ $forbidden_pattern ]]; then
    die "refusing to package forbidden bridge output: $name"
  fi
done < <(find "$payload_dir" -type f -print0)

# The deployed manifest participates in the layout but not the content hash it
# records. Hash file names and contents so the value is stable and unambiguous.
content_hash="$({
  while IFS= read -r -d '' relative; do
    printf '%s\0' "$relative"
    sha256sum "$payload_dir/$relative" | awk '{print $1}'
  done < <(cd "$payload_dir" && find . -type f -printf '%P\0' | LC_ALL=C sort -z)
} | sha256sum | awk '{print $1}')"

jq -n \
  --arg version "$version" \
  --arg content_hash "$content_hash" \
  '{
    id: "spirectlbridge",
    version: $version,
    has_pck: false,
    has_dll: true,
    affects_gameplay: false,
    buildIdentity: {
      bridgeSemVer: $version,
      bridgeVersion: ("spirectl-bridge/" + $version),
      assemblyInformationalVersion: $version,
      contentHash: $content_hash,
      sourceFreshness: {
        status: "release",
        newestModifiedUnixSeconds: null,
        newestPath: null
      }
    }
  }' > "$payload_dir/spirectlbridge.json"

find "$payload_root" -print0 | xargs -0 touch -d "@$source_date_epoch"

archive_name="spirectlbridge-v$version.zip"
archive_path="$output_dir/$archive_name"
archive_tmp="$work_dir/$archive_name"
(
  cd "$payload_root"
  find spirectlbridge -print | LC_ALL=C sort | zip -X -q "$archive_tmp" -@
)

archive_hash="$(sha256sum "$archive_tmp" | awk '{print $1}')"
manifest_name="spirectlbridge-v$version.manifest.json"
manifest_path="$output_dir/$manifest_name"
manifest_tmp="$work_dir/$manifest_name"
jq -n \
  --arg version "$version" \
  --arg archive_name "$archive_name" \
  --arg archive_hash "$archive_hash" \
  '{
    schemaVersion: "spirectl-bridge-release/v1",
    version: $version,
    archive: { name: $archive_name, sha256: $archive_hash }
  }' > "$manifest_tmp"

checksum_tmp="$work_dir/SHA256SUMS"
printf '%s  %s\n' "$archive_hash" "$archive_name" > "$checksum_tmp"

mv -f "$archive_tmp" "$archive_path"
mv -f "$manifest_tmp" "$manifest_path"
mv -f "$checksum_tmp" "$output_dir/SHA256SUMS"
"$repo_root/scripts/verify-bridge-release.sh" --zip "$archive_path" --version "$version"

echo "package-bridge-release: wrote $archive_path"
echo "package-bridge-release: wrote $manifest_path"
echo "package-bridge-release: wrote $output_dir/SHA256SUMS"
