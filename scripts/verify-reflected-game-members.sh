#!/usr/bin/env bash
# Sweep the BY-NAME game member reads in our source trees against two game builds and report the
# names that exist in one build and not the other.
#
# Why this exists as its own check. `sts2 code verify-references` answers the compiler's question:
# it walks a built assembly's TypeRef/MemberRef tables, so it sees every binding the compiler
# emitted — and none of the ones the code spells as a string. A read through
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
# The two builds. Each side is one game build, given as either
#   - a game assemblies dir (the one holding sts2.dll and GodotSharp.dll): a name is "present" when
#     it appears anywhere in those two assemblies' metadata string heap — not necessarily on the
#     type the code reads it from, and as a substring of a longer name too. Or
#   - a decompile corpus (`sts2 project recover --kind decompile` output): a name is "present" when
#     it appears as a whole word anywhere in the decompiled sts2/ or GodotSharp/ sources. This is
#     the only way to compare against a build that is no longer installed, and it is the tighter
#     test of the two (a substring of a longer name does not count).
# Both sides must be the same kind: the differential only cancels out how loose the test is when
# both builds are tested the same way. A build compared against itself is refused, because it can
# only ever come back clean.
#
# Source trees. By default the sweep reads this repo's `bridge-mod/src` for the by-name readers
# (`GetMemberValue`, `TrySetMemberValue`, `InvokeMethod`, `TryInvokeMethod`). `--root` adds trees
# to sweep for the same readers. `--couch-root <checkout>` adds that checkout's `src/`, scanned for
# the readers AND for the by-name shapes CouchCoop uses instead: a name handed to `AccessTools`
# (`Field`, `Method`, `Property*`), the string after a `typeof(...)` (Harmony targets and target
# tables), a `const string ...FieldName|MethodName|PropertyName = "..."` pin, and `.GetField` /
# `.GetMethod` / `.GetProperty("...")`. Names computed at run time are invisible to it; those are
# the ones a reflection test over a target table has to guard. Names inside a lane `#if` region
# are per-build on purpose and will show up as one-sided — read them against the `#if`.
#
# Normally run through `scripts/validate.sh reflected-members`, which finds the two corpora and
# reports SKIP (exit 77) instead of a refusal when they are not on this machine.
# Self-test: scripts/test-verify-reflected-game-members.sh
#
# usage: scripts/verify-reflected-game-members.sh [options] <build-A> <build-B>
# exit:  0 clean, 3 the two builds diverge, 2 the run was refused
set -euo pipefail
# One collation for every sort/comm/join below, or the differential compares differently-ordered lists.
export LC_ALL=C

print_usage() {
  cat <<'EOF'
usage: scripts/verify-reflected-game-members.sh [options] <build-A> <build-B>
  <build-X>            one game build: an assemblies dir (holding sts2.dll and GodotSharp.dll), or a
                       decompile corpus (the `decompile/` dir holding sts2/ and GodotSharp/, or the
                       toolchain dir that holds `decompile/`). Both sides must be the same kind.
  --root <dir>         also sweep <dir> for the by-name readers (repeatable)
  --no-default-root    do not sweep this repo's bridge-mod/src
  --couch-root <dir>   also sweep <dir>/src for CouchCoop's by-name shapes
                       (default from $SPIRECTL_COUCH_ROOT; opt-in, off when neither is set)
exit: 0 clean, 3 the two builds diverge, 2 the run was refused
EOF
}
usage() {
  print_usage >&2
  exit 2
}

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
default_root="$repo_root/bridge-mod/src"
use_default_root=1
extra_roots=()
couch_root="${SPIRECTL_COUCH_ROOT:-}"
builds=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    -h | --help)
      print_usage
      exit 0
      ;;
    --root)
      [[ $# -ge 2 ]] || usage
      extra_roots+=("$2")
      shift 2
      ;;
    --no-default-root)
      use_default_root=0
      shift
      ;;
    --couch-root)
      [[ $# -ge 2 ]] || usage
      couch_root="$2"
      shift 2
      ;;
    --*)
      echo "unknown option: $1" >&2
      usage
      ;;
    *)
      builds+=("$1")
      shift
      ;;
  esac
done
[[ ${#builds[@]} == 2 ]] || usage

work="$(mktemp -d "${TMPDIR:-/tmp}/spirectl-reflected-members.XXXXXX")"
trap 'rm -rf "$work"' EXIT

for tool in rg strings comm md5sum; do
  command -v "$tool" >/dev/null || {
    echo "missing required tool: $tool" >&2
    exit 2
  }
done

# Prints "<kind><TAB><dir>" for a build directory: `assemblies` or `corpus`.
resolve_build() {
  local dir="$1"
  if [[ -f "$dir/sts2.dll" && -f "$dir/GodotSharp.dll" ]]; then
    printf 'assemblies\t%s\n' "$dir"
  elif [[ -d "$dir/sts2" && -d "$dir/GodotSharp" ]]; then
    printf 'corpus\t%s\n' "$dir"
  elif [[ -d "$dir/decompile/sts2" && -d "$dir/decompile/GodotSharp" ]]; then
    printf 'corpus\t%s\n' "$dir/decompile"
  else
    return 1
  fi
}

kinds=()
paths=()
for dir in "${builds[@]}"; do
  resolved="$(resolve_build "$dir")" || {
    {
      echo "not a game build: $dir"
      echo "  want an assemblies dir (sts2.dll + GodotSharp.dll) or a decompile corpus (sts2/ + GodotSharp/)"
    } >&2
    exit 2
  }
  kinds+=("${resolved%%$'\t'*}")
  paths+=("${resolved#*$'\t'}")
done

if [[ "${kinds[0]}" != "${kinds[1]}" ]]; then
  {
    echo "the two builds are different kinds (A is ${kinds[0]}, B is ${kinds[1]})"
    echo "  the presence test is looser for assemblies than for a decompile corpus, so a mixed pair"
    echo "  would report the difference between the tests as a difference between the builds."
  } >&2
  exit 2
fi
kind="${kinds[0]}"

# Content fingerprint of one build, so a build compared against itself is refused.
fingerprint() {
  local dir="$1"
  if [[ "$kind" == assemblies ]]; then
    cat "$dir/sts2.dll" "$dir/GodotSharp.dll" | md5sum
  else
    (cd "$dir" && find sts2 GodotSharp -type f -print0 | LC_ALL=C sort -z | xargs -0 md5sum | md5sum)
  fi
}
if [[ "$(fingerprint "${paths[0]}")" == "$(fingerprint "${paths[1]}")" ]]; then
  echo "the two builds are byte-identical, so the differential can only come back clean: ${paths[0]} and ${paths[1]}" >&2
  exit 2
fi

# Every pattern below captures the member name as the named group `n`. `**/GameApi/V*` (not
# `GameApi/V*`, which ripgrep anchors to the search root) is what excludes the lane seam wherever
# the tree nests it.
member='(?P<n>[A-Za-z_][A-Za-z0-9_]*)'
scan() { # scan <root> <multiline 0|1> <regex>
  local multiline=()
  [[ "$2" == 1 ]] && multiline=(-U)
  rg ${multiline[@]+"${multiline[@]}"} -o --no-filename --text --glob '*.cs' --glob '!**/GameApi/V*' \
    -e "$3" -r '${n}' "$1" || true
}

# Every member name handed to one of the by-name readers as a literal. Two passes: the common
# single-line form, and a multiline form for call sites the argument list wraps. `[^;()"]*` keeps
# the multiline pass inside one argument list, which the single-line pass then backfills for the
# nested-call sites it cannot cross.
readers='GetMemberValue|TrySetMemberValue|InvokeMethod|TryInvokeMethod'
sweep_readers() {
  scan "$1" 0 "($readers)\([^,]+,\s*\"$member\""
  scan "$1" 1 "($readers)\([^;()\"]*\"$member\""
}

# The by-name shapes CouchCoop spells instead of the four readers.
sweep_couch() {
  scan "$1" 0 "AccessTools\.[A-Za-z]+\([^,]+,\s*\"$member\""
  scan "$1" 1 "AccessTools\.[A-Za-z]+\([^;()\"]*\"$member\""
  scan "$1" 1 "typeof\([A-Za-z0-9_.<>]+\),\s*\"$member\""
  scan "$1" 1 "const string \w*(Field|Method|Property)Name\w*\s*=\s*\"$member\""
  scan "$1" 0 "\.(GetField|GetMethod|GetProperty)\(\s*\"$member\""
}

labels=()
files=()
required=()
add_names() { # add_names <label> <required 0|1> <file>
  labels+=("$1")
  required+=("$2")
  files+=("$3")
}

idx=0
if [[ "$use_default_root" == 1 ]]; then
  sweep_readers "$default_root" | sort -u > "$work/names.$idx"
  add_names "spirectl bridge-mod/src" 1 "$work/names.$idx"
  idx=$((idx + 1))
fi
for root in ${extra_roots[@]+"${extra_roots[@]}"}; do
  [[ -d "$root" ]] || {
    echo "not a directory: --root $root" >&2
    exit 2
  }
  sweep_readers "$root" | sort -u > "$work/names.$idx"
  add_names "root $root" 1 "$work/names.$idx"
  idx=$((idx + 1))
done
if [[ -n "$couch_root" ]]; then
  [[ -d "$couch_root/src" ]] || {
    echo "not a CouchCoop checkout (no src/): --couch-root $couch_root" >&2
    exit 2
  }
  { sweep_readers "$couch_root/src"; sweep_couch "$couch_root/src"; } | sort -u > "$work/names.$idx"
  add_names "couch src" 0 "$work/names.$idx"
  idx=$((idx + 1))
fi
[[ ${#files[@]} -gt 0 ]] || {
  echo "nothing to sweep: --no-default-root leaves no source tree (add --root or --couch-root)" >&2
  exit 2
}

# A required tree with no reads means the readers were renamed, not that the tree is clean.
for i in "${!files[@]}"; do
  if [[ "${required[$i]}" == 1 && ! -s "${files[$i]}" ]]; then
    echo "found no by-name member reads under ${labels[$i]} — did the readers get renamed?" >&2
    exit 2
  fi
done

sort -u "${files[@]}" > "$work/names"

# A property accessor is `get_X` / `set_X` in an assembly's metadata but plain `X` in decompiled
# source, so a corpus is probed by the property name. Each line is "<probe><TAB><name>".
if [[ "$kind" == corpus ]]; then
  sed -E 's/^(get|set)_//' "$work/names" | paste - "$work/names" | sort -u > "$work/probes"
  cut -f1 "$work/probes" | sort -u > "$work/probe-names"
fi

# Names present in one build, one per line, sorted.
present() {
  local dir="$1"
  [[ -s "$work/names" ]] || return 0
  if [[ "$kind" == assemblies ]]; then
    strings -a "$dir/sts2.dll" "$dir/GodotSharp.dll" | grep -oFf "$work/names" | sort -u || true
  else
    { rg -o -w -F -f "$work/probe-names" --no-filename --text "$dir/sts2" "$dir/GodotSharp" || true; } |
      sort -u | join -t $'\t' - "$work/probes" | cut -f2 | sort -u
  fi
}
present "${paths[0]}" > "$work/a"
present "${paths[1]}" > "$work/b"

for i in "${!files[@]}"; do
  printf 'swept %s by-name member reads under %s (lane seam excluded)\n' "$(wc -l < "${files[$i]}")" "${labels[$i]}"
done
printf '  A (%s) %s: %s present\n' "$kind" "${paths[0]}" "$(wc -l < "$work/a")"
printf '  B (%s) %s: %s present\n' "$kind" "${paths[1]}" "$(wc -l < "$work/b")"

if [[ ! -s "$work/names" ]]; then
  echo
  echo 'clean: no by-name member reads under the optional trees, so there is nothing to compare'
  exit 0
fi

# Which swept trees read a name, as " [spirectl bridge-mod/src, couch src]".
readers_of() {
  local name="$1" i joined=""
  for i in "${!files[@]}"; do
    if grep -qxF "$name" "${files[$i]}"; then
      joined+="${joined:+, }${labels[$i]}"
    fi
  done
  printf ' [%s]' "$joined"
}
list_names() {
  local line
  while IFS= read -r line; do
    printf '  - %s%s\n' "$line" "$(readers_of "$line")"
  done
}

sort -u "$work/a" "$work/b" > "$work/either"
comm -23 "$work/names" "$work/either" > "$work/neither"
if [[ -s "$work/neither" ]]; then
  printf '\n%s name(s) in neither build — fallback spellings and non-game names live here, not a failure:\n' \
    "$(wc -l < "$work/neither")"
  list_names < "$work/neither"
fi

# One direction of the differential: names the `have` build declares and the `missing` build does not.
report_divergence() {
  local label="$1" have="$2" missing="$3" have_file="$4" missing_file="$5"
  local diverged
  diverged="$(comm -23 "$have_file" "$missing_file")"
  [[ -n "$diverged" ]] || return 0
  printf '\npresent in %s (%s) and MISSING from %s:\n' "$label" "$have" "$missing"
  printf '%s\n' "$diverged" | list_names
  return 1
}

status=0
report_divergence A "${paths[0]}" "${paths[1]}" "$work/a" "$work/b" || status=3
report_divergence B "${paths[1]}" "${paths[0]}" "$work/b" "$work/a" || status=3

if [[ "$status" == 0 ]]; then
  echo
  echo 'clean: every by-name member read outside the lane seam resolves the same on both builds'
else
  cat >&2 <<'EOF'

Each name above is read by string, so the reader returns null on the build that lacks it and the
code keeps running with a wrong answer. Move the read behind the lane seam
(bridge-mod/src/Spirectl.Sts2/GameApi/<lane>/GameApiMembers.cs), give each lane the name its build
has, and declare it in that lane's GameApiManifest so a future rename refuses the bridge instead.
A read that is a deliberate fallback spelling still needs the check: confirm the primary name
resolves on both builds. A name read under CouchCoop's src/ is guarded by its reflection test
instead: gate it behind the lane `#if` and pin it in the target table that test walks.
EOF
fi
exit "$status"
