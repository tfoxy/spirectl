#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "usage: scripts/verify-release-version.sh <vMAJOR.MINOR.PATCH>" >&2
  exit 2
}

[[ $# == 1 ]] || usage
tag="$1"
[[ "$tag" =~ ^v([0-9]+)\.([0-9]+)\.([0-9]+)$ ]] || {
  echo "release tag must be vMAJOR.MINOR.PATCH, got '$tag'" >&2
  exit 1
}
version="${tag#v}"

cargo_version="$(sed -n 's/^version = "\([^"]*\)"$/\1/p' cli/Cargo.toml | head -n 1)"
bridge_version="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' bridge-mod/Directory.Build.props | head -n 1)"
npm_version="$(node -p 'require("./npm-wrapper/package.json").version')"

for identity in "Rust CLI:$cargo_version" "bridge:$bridge_version" "npm wrapper:$npm_version"; do
  name="${identity%%:*}"
  found="${identity#*:}"
  [[ "$found" == "$version" ]] || {
    echo "$name version '$found' does not match tag '$tag'" >&2
    exit 1
  }
done

assembly_version="$(sed -n 's:.*<AssemblyVersion>\([^<]*\)</AssemblyVersion>.*:\1:p' bridge-mod/Directory.Build.props | head -n 1)"
[[ "$assembly_version" == "$version.0" ]] || {
  echo "bridge AssemblyVersion '$assembly_version' must be '$version.0'" >&2
  exit 1
}

# Release notes are part of a release, not an afterthought: the workflow feeds this same section
# to `gh release create --notes-file`, so a tag with no section would publish an empty release.
scripts/changelog-section.sh "$version" > /dev/null

echo "release version verified: $tag"
