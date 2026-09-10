#!/usr/bin/env bash
# Run a command in every git worktree of this repo.
#
# Usage:
#   scripts/foreach-worktree.sh [--sequential] [--exclude-main] [--json] [--] <command> [args...]
#
# Each command runs with cwd set to the worktree root, so tools that resolve
# config from the working directory (e.g. `sts2`, which picks its instance from
# the worktree's sts2.local.yaml) target the right tree automatically.
#
# Examples:
#   scripts/foreach-worktree.sh cargo build
#   scripts/foreach-worktree.sh sts2 game install-bridge
#   scripts/foreach-worktree.sh -- bash -lc 'cargo build && cargo test'
set -euo pipefail

usage() {
  printf 'usage: scripts/foreach-worktree.sh [--sequential] [--exclude-main] [--json] [--] <command> [args...]\n' >&2
}

die() {
  printf 'error: %s\n' "$*" >&2
  exit 1
}

sequential=false
exclude_main=false
as_json=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    -h|--help)
      usage
      exit 0
      ;;
    --sequential)
      sequential=true
      shift
      ;;
    --exclude-main)
      exclude_main=true
      shift
      ;;
    --json)
      as_json=true
      shift
      ;;
    --)
      shift
      break
      ;;
    -*)
      usage
      die "unknown option '$1'"
      ;;
    *)
      break
      ;;
  esac
done

if [[ $# -eq 0 ]]; then
  usage
  exit 2
fi

repo_root="$(git rev-parse --show-toplevel 2>/dev/null)" || die "not inside a git repository"
repo_name="$(basename "$repo_root")"
if [[ "$repo_name" != "spirectl" && "$repo_name" != spirectl-* ]]; then
  die "expected to run from a spirectl checkout, got '$repo_root'"
fi

# Discover worktrees. `git worktree list --porcelain` lists the main worktree
# first, then linked worktrees, each introduced by a `worktree <path>` line.
worktrees=()
main_worktree=""
while IFS= read -r line; do
  [[ "$line" == worktree\ * ]] || continue
  path="${line#worktree }"
  if [[ -z "$main_worktree" ]]; then
    main_worktree="$path"
    [[ "$exclude_main" == true ]] && continue
  fi
  worktrees+=("$path")
done < <(git worktree list --porcelain)

[[ ${#worktrees[@]} -gt 0 ]] || die "no worktrees found"

# JSON string escaper for the optional --json summary.
json_escape() {
  local s="$1"
  s="${s//\\/\\\\}"
  s="${s//\"/\\\"}"
  printf '%s' "$s"
}

statuses=()  # parallel array to worktrees: exit code per worktree

if [[ "$sequential" == true ]]; then
  for wt in "${worktrees[@]}"; do
    code=0
    if [[ "$as_json" == true ]]; then
      ( cd "$wt" && "$@" ) >/dev/null 2>&1 || code=$?
    else
      printf '===== %s =====\n' "$wt"
      ( cd "$wt" && "$@" ) || code=$?
    fi
    statuses+=("$code")
  done
else
  tmpdir="$(mktemp -d)"
  trap 'rm -rf "$tmpdir"' EXIT
  pids=()
  logs=()
  i=0
  for wt in "${worktrees[@]}"; do
    log="$tmpdir/$i.log"
    logs+=("$log")
    [[ "$as_json" == true ]] || printf '▶ %s\n' "$wt"
    ( cd "$wt" && "$@" ) >"$log" 2>&1 &
    pids+=("$!")
    i=$((i + 1))
  done
  for idx in "${!pids[@]}"; do
    code=0
    wait "${pids[$idx]}" || code=$?
    statuses+=("$code")
  done
  if [[ "$as_json" != true ]]; then
    for idx in "${!worktrees[@]}"; do
      printf '\n===== %s (exit %s) =====\n' "${worktrees[$idx]}" "${statuses[$idx]}"
      cat "${logs[$idx]}"
    done
  fi
fi

# Summary.
ok=0
failed=0
for code in "${statuses[@]}"; do
  if [[ "$code" -eq 0 ]]; then
    ok=$((ok + 1))
  else
    failed=$((failed + 1))
  fi
done

if [[ "$as_json" == true ]]; then
  printf '{"results":['
  for idx in "${!worktrees[@]}"; do
    [[ "$idx" -gt 0 ]] && printf ','
    if [[ "${statuses[$idx]}" -eq 0 ]]; then status="ok"; else status="failed"; fi
    printf '{"worktree":"%s","exitCode":%s,"status":"%s"}' \
      "$(json_escape "${worktrees[$idx]}")" "${statuses[$idx]}" "$status"
  done
  printf '],"ok":%s,"failed":%s}\n' "$ok" "$failed"
else
  printf '\n----- summary -----\n'
  for idx in "${!worktrees[@]}"; do
    if [[ "${statuses[$idx]}" -eq 0 ]]; then
      printf 'PASS  %s\n' "${worktrees[$idx]}"
    else
      printf 'FAIL  %s (exit %s)\n' "${worktrees[$idx]}" "${statuses[$idx]}"
    fi
  done
  printf '%s ok, %s failed\n' "$ok" "$failed"
fi

[[ "$failed" -eq 0 ]] || exit 1
