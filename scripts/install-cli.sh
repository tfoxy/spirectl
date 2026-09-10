#!/usr/bin/env bash
# Build the `sts2` CLI and link it onto your PATH.
#
# Why this exists: every downstream script and polling loop spawns a bare `sts2` from PATH, so
# whatever that name resolves to is what they pay for. Hand-rolled shims tend to resolve to
# `target/debug/sts2`, which makes a tight `waitFor` loop several times slower than it needs to be.
# This installs the release build by default and says exactly what it linked.
#
# Usage:
#   scripts/install-cli.sh [--debug] [--print-path] [--force]
#
# Options:
#   --debug       Build and link the debug profile instead of release.
#   --print-path  Print where the binary and link would be, then exit. Builds nothing, links nothing.
#   --force       Replace an existing non-symlink at the destination (e.g. a hand-written shim).
#                 Without it, this script refuses rather than clobbering a real file.
#
# Environment:
#   PREFIX             Directory to link into (default: $HOME/.local/bin).
#   CARGO_TARGET_DIR   Honoured when locating the built binary (default: <repo>/target).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

profile="release"
print_path=false
force=false

usage() {
  printf 'usage: scripts/install-cli.sh [--debug] [--print-path] [--force]\n' >&2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --debug) profile="debug" ;;
    --print-path) print_path=true ;;
    --force) force=true ;;
    -h|--help) usage; exit 0 ;;
    *)
      printf 'unknown argument: %s\n' "$1" >&2
      usage
      exit 2
      ;;
  esac
  shift
done

bin_dir="${PREFIX:-$HOME/.local/bin}"
target_dir="${CARGO_TARGET_DIR:-$repo_root/target}"
binary_path="$target_dir/$profile/sts2"
link_path="$bin_dir/sts2"

json_escape() {
  printf '%s' "$1" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read())[1:-1])'
}

if [[ "$print_path" == "true" ]]; then
  printf '{"command":"install-cli","status":"passed","profile":"%s","binaryPath":"%s","linkPath":"%s","installed":false,"message":"path only; nothing built or linked"}\n' \
    "$(json_escape "$profile")" "$(json_escape "$binary_path")" "$(json_escape "$link_path")"
  exit 0
fi

# Refuse to destroy something we did not create. A regular file here is almost always a
# hand-written shim the user still depends on; replacing it silently is not ours to decide.
if [[ -e "$link_path" && ! -L "$link_path" && "$force" != "true" ]]; then
  printf '{"command":"install-cli","status":"failed","code":"destination_not_a_symlink","linkPath":"%s","message":"%s exists and is not a symlink; re-run with --force to replace it"}\n' \
    "$(json_escape "$link_path")" "$(json_escape "$link_path")" >&2
  exit 3
fi

if [[ "$profile" == "release" ]]; then
  cargo build -p sts2 --release
else
  cargo build -p sts2
fi

if [[ ! -x "$binary_path" ]]; then
  printf '{"command":"install-cli","status":"failed","code":"binary_missing","binaryPath":"%s","message":"cargo build did not produce an executable at the expected path"}\n' \
    "$(json_escape "$binary_path")" >&2
  exit 4
fi

mkdir -p "$bin_dir"
ln -sfn "$binary_path" "$link_path"

on_path=true
case ":${PATH}:" in
  *":$bin_dir:"*) ;;
  *)
    on_path=false
    printf 'warning: %s is not on your PATH; add it (e.g. export PATH="%s:$PATH") or `sts2` will not resolve.\n' \
      "$bin_dir" "$bin_dir" >&2
    ;;
esac

printf '{"command":"install-cli","status":"passed","profile":"%s","binaryPath":"%s","linkPath":"%s","installed":true,"binDirOnPath":%s,"message":"linked sts2 onto PATH"}\n' \
  "$(json_escape "$profile")" "$(json_escape "$binary_path")" "$(json_escape "$link_path")" "$on_path"
