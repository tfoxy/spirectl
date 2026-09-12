#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat >&2 <<'EOF'
Usage: scripts/package-bridge-release.sh --version <x.y.z> --lane <sts2-api-lane> --output-dir <dir>

Build a release bridge against the locked, build-only STS2 reference SDK. The
output directory receives spirectlbridge-v<version>-<lane>.zip, its sidecar
manifest, and SHA256SUMS. No game or reference assemblies are included in the
archive.

One payload per STS2 API lane. The bridge binds game members that were renamed
or reshaped between game builds, so a payload is only valid for the builds its
lane covers; `scripts/sts2-api-lanes.sh --releasable` lists the lanes to package.
That is a subset of the supported lanes: a release compiles against the pinned,
declaration-only reference SDK, which covers only some game builds. The reference
SDK carries no release_info.json, so the lane cannot be detected here and must be
passed.
EOF
}

die() {
  echo "package-bridge-release: $*" >&2
  exit 1
}

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version=""
lane=""
output_dir=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)
      [[ $# -ge 2 ]] || die "--version requires a value"
      version="$2"
      shift 2
      ;;
    --lane)
      [[ $# -ge 2 ]] || die "--lane requires a value"
      lane="$2"
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

# Checked against the RELEASABLE subset, not every supported lane: this script
# builds against the locked reference SDK, which only declares the game builds that
# subset covers. A lane outside it fails later with a raw CS0234 on whichever type
# the reference package does not declare, which reads as a source bug rather than
# the packaging limit it is.
releasable_lanes="$("$repo_root/scripts/sts2-api-lanes.sh" --releasable)"
all_lanes="$("$repo_root/scripts/sts2-api-lanes.sh")"
[[ -n "$lane" ]] || die "--lane is required (releasable lanes: $(tr '\n' ' ' <<<"$releasable_lanes"))"
if ! grep -Fxq "$lane" <<<"$releasable_lanes"; then
  if grep -Fxq "$lane" <<<"$all_lanes"; then
    die "STS2 API lane '$lane' is supported from source but not releasable: the pinned STS2 reference SDK does not declare its game build. Releasable lanes: $(tr '\n' ' ' <<<"$releasable_lanes"). See Sts2GameApiReleasableLanes in bridge-mod/Sts2GameApi.props."
  fi
  die "unknown STS2 API lane '$lane' (releasable lanes: $(tr '\n' ' ' <<<"$releasable_lanes"))"
fi
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
  # The reference SDK has no release_info.json, so the lane cannot be detected
  # and this is the only place it can come from.
  "-p:Sts2GameApi=$lane"
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

# `builtAgainstGame` is deliberately thinner here than in a source build, and
# the asymmetry is the honest part: this payload compiled against the locked,
# declaration-only reference SDK, which has no release_info.json — so there is no
# game version and no main_assembly_hash to record. It can claim its API lane and
# the reference package version it was built from. Both other fields stay null
# rather than being invented; a source build (see write_bridge_manifest in
# cli/src/lifecycle/lifecycle_mods_settings.rs) fills all of them.
jq -n \
  --arg version "$version" \
  --arg lane "$lane" \
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
      sts2ApiLane: $lane,
      builtAgainstGame: {
        identitySource: "reference-sdk",
        version: null,
        mainAssemblyHash: null,
        referencePackageVersion: $version
      },
      sourceFreshness: {
        status: "release",
        newestModifiedUnixSeconds: null,
        newestPath: null
      }
    }
  }' > "$payload_dir/spirectlbridge.json"

find "$payload_root" -print0 | xargs -0 touch -d "@$source_date_epoch"

archive_name="spirectlbridge-v$version-$lane.zip"
archive_path="$output_dir/$archive_name"
archive_tmp="$work_dir/$archive_name"
(
  cd "$payload_root"
  find spirectlbridge -print | LC_ALL=C sort | zip -X -q "$archive_tmp" -@
)

archive_hash="$(sha256sum "$archive_tmp" | awk '{print $1}')"
manifest_name="spirectlbridge-v$version-$lane.manifest.json"
manifest_path="$output_dir/$manifest_name"
manifest_tmp="$work_dir/$manifest_name"
jq -n \
  --arg version "$version" \
  --arg lane "$lane" \
  --arg archive_name "$archive_name" \
  --arg archive_hash "$archive_hash" \
  '{
    schemaVersion: "spirectl-bridge-release/v1",
    version: $version,
    sts2ApiLane: $lane,
    archive: { name: $archive_name, sha256: $archive_hash }
  }' > "$manifest_tmp"

# One line per packaged lane, appended so packaging every lane into one output
# directory leaves a single SHA256SUMS covering all of them.
checksum_line="$work_dir/SHA256SUMS.line"
printf '%s  %s\n' "$archive_hash" "$archive_name" > "$checksum_line"

mv -f "$archive_tmp" "$archive_path"
mv -f "$manifest_tmp" "$manifest_path"
touch "$output_dir/SHA256SUMS"
grep -Fv "  $archive_name" "$output_dir/SHA256SUMS" > "$work_dir/SHA256SUMS.kept" || true
cat "$work_dir/SHA256SUMS.kept" "$checksum_line" | LC_ALL=C sort -k2 > "$output_dir/SHA256SUMS"
"$repo_root/scripts/verify-bridge-release.sh" --zip "$archive_path" --version "$version" --lane "$lane"

echo "package-bridge-release: wrote $archive_path"
echo "package-bridge-release: wrote $manifest_path"
echo "package-bridge-release: wrote $output_dir/SHA256SUMS"
