#!/usr/bin/env bash
# Sweep the bridge's BY-NAME game member reads against two game builds and report the names that
# exist in one build and not the other.
#
# Why this exists as its own check. `sts2 code verify-references` answers the compiler's question:
# it walks a built assembly's TypeRef/MemberRef tables, so it sees every binding the compiler
# emitted — and none of the ones the bridge spells as a string. A read through
# Sts2LiveIntrospection returns null when the name is gone, the state projection turns that into
# `0` / `false` / `null`, and the bridge starts, runs and reports plausible wrong numbers. That is
# the failure this sweep is for: it found `_playersReadyToBeginEnemyTurn`, present on v0.107.1 and
# gone on v0.111.0, which left the host-local seat turn watcher silently inert on the newer build.
#
# What it does NOT cover. The lane seam (`GameApi/V*/`) is excluded: those names are per-build ON
# PURPOSE, so "differs between builds" is not a finding there. The lane seam has its own, stronger
# gate — every name it reads is declared in that lane's `GameApiManifest` and resolved against the
# real assembly before the live composition is built, so a rename refuses the bridge at startup
# (`scripts/validate.sh bridge-live-host-tests --filter FullyQualifiedName~GameApi` per install).
# A name absent from BOTH builds is reported for information only: a fallback chain that tries two
# spellings, or a name that is not a game member at all, lands there legitimately.
#
# Presence is tested against the assemblies' own metadata string heap, so a name is "present" when
# it appears anywhere in sts2.dll or GodotSharp.dll — not necessarily on the type the bridge reads
# it from. The check is deliberately loose in that direction: a false "present" only costs
# sensitivity, while the differential cancels out every name that is equally loose on both sides.
#
# usage: scripts/verify-reflected-game-members.sh <assemblies-dir> <assemblies-dir>
# exit:  0 clean, 3 the two builds diverge, 2 the run was refused
set -euo pipefail

usage() {
  echo "usage: scripts/verify-reflected-game-members.sh <assemblies-dir> <assemblies-dir>" >&2
  echo "  each directory is a game install's data dir (the one holding sts2.dll)" >&2
  exit 2
}

[[ $# == 2 ]] || usage
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d "${TMPDIR:-/tmp}/spirectl-reflected-members.XXXXXX")"
trap 'rm -rf "$work"' EXIT

for tool in rg strings comm; do
  command -v "$tool" >/dev/null || {
    echo "missing required tool: $tool" >&2
    exit 2
  }
done

for dir in "$1" "$2"; do
  for dll in sts2.dll GodotSharp.dll; do
    [[ -f "$dir/$dll" ]] || {
      echo "not a game assemblies directory (no $dll): $dir" >&2
      exit 2
    }
  done
done

# Every member name handed to one of the by-name readers as a literal. Two passes: the common
# single-line form, and a multiline form for call sites the argument list wraps. `[^;()"]*` keeps
# the multiline pass inside one argument list, which the single-line pass then backfills for the
# nested-call sites it cannot cross.
readers='GetMemberValue|TrySetMemberValue|InvokeMethod|TryInvokeMethod'
{
  rg -o --no-filename --text --glob '*.cs' --glob '!GameApi/V*' \
    -e "($readers)\([^,]+,\s*\"([A-Za-z_][A-Za-z0-9_]*)\"" -r '$2' \
    "$repo_root/bridge-mod/src"
  rg -U -o --no-filename --text --glob '*.cs' --glob '!GameApi/V*' \
    -e "($readers)\([^;()\"]*\"([A-Za-z_][A-Za-z0-9_]*)\"" -r '$2' \
    "$repo_root/bridge-mod/src"
} | sort -u > "$work/names"

[[ -s "$work/names" ]] || {
  echo "found no by-name member reads under bridge-mod/src — did the readers get renamed?" >&2
  exit 2
}

present() {
  strings -a "$1/sts2.dll" "$1/GodotSharp.dll" | grep -oFf "$work/names" | sort -u
}
present "$1" > "$work/a"
present "$2" > "$work/b"

printf 'swept %s by-name member reads under bridge-mod/src (lane seam excluded)\n' "$(wc -l < "$work/names")"
printf '  A %s: %s present\n' "$1" "$(wc -l < "$work/a")"
printf '  B %s: %s present\n' "$2" "$(wc -l < "$work/b")"

sort -u "$work/a" "$work/b" > "$work/either"
comm -23 "$work/names" "$work/either" > "$work/neither"
if [[ -s "$work/neither" ]]; then
  printf '\n%s name(s) in neither build — fallback spellings and non-game names live here, not a failure:\n' \
    "$(wc -l < "$work/neither")"
  sed 's/^/  - /' "$work/neither"
fi

# One direction of the differential: names the `have` build declares and the `missing` build does not.
report_divergence() {
  local label="$1" have="$2" missing="$3" have_file="$4" missing_file="$5"
  local diverged
  diverged="$(comm -23 "$have_file" "$missing_file")"
  [[ -n "$diverged" ]] || return 0
  printf '\npresent in %s (%s) and MISSING from %s:\n' "$label" "$have" "$missing"
  printf '%s\n' "$diverged" | sed 's/^/  - /'
  return 1
}

status=0
report_divergence A "$1" "$2" "$work/a" "$work/b" || status=3
report_divergence B "$2" "$1" "$work/b" "$work/a" || status=3

if [[ "$status" == 0 ]]; then
  echo
  echo 'clean: every by-name member read outside the lane seam resolves the same on both builds'
else
  cat >&2 <<'EOF'

Each name above is read by string, so the reader returns null on the build that lacks it and the
bridge keeps running with a wrong answer. Move the read behind the lane seam
(bridge-mod/src/Spirectl.Sts2/GameApi/<lane>/GameApiMembers.cs), give each lane the name its build
has, and declare it in that lane's GameApiManifest so a future rename refuses the bridge instead.
EOF
fi
exit "$status"
