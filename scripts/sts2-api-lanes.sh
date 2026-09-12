#!/usr/bin/env bash
# Print the STS2 API lanes, one per line, oldest first.
#
#   scripts/sts2-api-lanes.sh              every supported lane
#   scripts/sts2-api-lanes.sh --releasable only the lanes a released payload exists for
#
# The lists live in `bridge-mod/Sts2GameApi.props` — the single source of truth for
# "which game build needs which bridge sources". Four readers share that one file:
# MSBuild here, MSBuild in the CouchCoop repo (via import), `cli/build.rs`, and this
# script. Release packaging is per lane, so the release scripts and the release
# workflow need the lists without restating them.
#
# `--releasable` is a SUBSET. A release compiles against the locked,
# declaration-only STS2 reference SDK, which pins one game build's declarations; a
# lane whose sources need a type that package does not declare cannot be packaged at
# all. Such a lane is still fully supported from a source checkout. The props file
# carries the current reason.
set -euo pipefail

property="Sts2GameApiLanes"
case "${1:-}" in
  "") ;;
  --releasable) property="Sts2GameApiReleasableLanes" ;;
  --help | -h)
    sed -n '2,20p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
    exit 0
    ;;
  *)
    echo "sts2-api-lanes: unknown argument: $1" >&2
    exit 2
    ;;
esac

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
props_file="$repo_root/bridge-mod/Sts2GameApi.props"
[[ -f "$props_file" ]] || {
  echo "sts2-api-lanes: missing $props_file" >&2
  exit 1
}

# The properties' values are flat text by contract (see the file's header), so a
# single-line extraction is exact rather than a guess at XML.
lanes="$(sed -n "s:.*<$property>\\([^<]*\\)</$property>.*:\\1:p" "$props_file" | head -n 1)"
[[ -n "$lanes" ]] || {
  echo "sts2-api-lanes: no $property property in $props_file" >&2
  exit 1
}

printf '%s\n' "$lanes" | tr ';' '\n' | sed '/^[[:space:]]*$/d'
