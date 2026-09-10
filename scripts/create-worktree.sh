#!/usr/bin/env bash
# Create or refresh a persistent sibling worktree.
#
# Usage:
#   scripts/create-worktree.sh <name>
#
# Creates ../spirectl-<name> on branch worktree/<name>, copies local config,
# pins instances.default to <name>, and links shared local-only caches/tools.
set -euo pipefail

usage() {
  printf 'usage: scripts/create-worktree.sh <name>\n' >&2
}

die() {
  printf 'error: %s\n' "$*" >&2
  exit 1
}

if [[ $# -eq 1 && ( "${1:-}" == "-h" || "${1:-}" == "--help" ) ]]; then
  usage
  exit 0
fi

if [[ $# -ne 1 ]]; then
  usage
  exit 2
fi

name="$1"
if [[ ! "$name" =~ ^[A-Za-z0-9._-]{1,64}$ || "$name" == .* ]]; then
  die "instance name '$name' is invalid; use 1-64 chars from [A-Za-z0-9._-], not starting with '.'."
fi
case "$name" in
  auto|all)
    die "instance name '$name' is reserved"
    ;;
esac

repo_root="$(git rev-parse --show-toplevel 2>/dev/null)" || die "not inside a git repository"
repo_name="$(basename "$repo_root")"
if [[ "$repo_name" != "spirectl" && "$repo_name" != spirectl-* ]]; then
  die "expected to run from a spirectl checkout, got '$repo_root'"
fi

source_config="$repo_root/sts2.local.yaml"
[[ -f "$source_config" ]] || die "missing $source_config; create local config before making worktrees"

parent_dir="$(dirname "$repo_root")"
dest="$parent_dir/spirectl-$name"
branch="worktree/$name"
source_head="$(git -C "$repo_root" rev-parse HEAD)"
source_common="$(realpath "$(git -C "$repo_root" rev-parse --git-common-dir)")"

ensure_branch_at_source_head() {
  if git -C "$repo_root" show-ref --verify --quiet "refs/heads/$branch"; then
    git -C "$repo_root" branch -f "$branch" "$source_head" >/dev/null
  else
    git -C "$repo_root" branch "$branch" "$source_head" >/dev/null
  fi
}

require_clean_tracked_files() {
  local worktree="$1"
  if ! git -C "$worktree" diff --quiet --ignore-submodules --; then
    die "$worktree has unstaged tracked changes; commit, stash, or clean them before updating"
  fi
  if ! git -C "$worktree" diff --cached --quiet --ignore-submodules --; then
    die "$worktree has staged tracked changes; commit, stash, or clean them before updating"
  fi
}

if [[ -e "$dest" ]]; then
  [[ -d "$dest" ]] || die "$dest exists but is not a directory"
  git -C "$dest" rev-parse --is-inside-work-tree >/dev/null 2>&1 || die "$dest is not a git worktree"
  dest_common="$(realpath "$(git -C "$dest" rev-parse --git-common-dir)")"
  [[ "$dest_common" == "$source_common" ]] || die "$dest belongs to a different git repository"
  require_clean_tracked_files "$dest"
  current_branch="$(git -C "$dest" branch --show-current)"
  if [[ "$current_branch" != "$branch" ]]; then
    ensure_branch_at_source_head
    git -C "$dest" checkout "$branch" >/dev/null
  fi
  git -C "$dest" reset --hard "$source_head" >/dev/null
else
  ensure_branch_at_source_head
  git -C "$repo_root" worktree add "$dest" "$branch"
fi

cp "$source_config" "$dest/sts2.local.yaml"

python3 - "$dest/sts2.local.yaml" "$name" <<'PY'
from pathlib import Path
import sys

path = Path(sys.argv[1])
name = sys.argv[2]
lines = path.read_text(encoding="utf-8").splitlines()
out = []
i = 0
while i < len(lines):
    line = lines[i]
    if line.strip() == "instances:" and not line[: len(line) - len(line.lstrip())]:
        i += 1
        while i < len(lines):
            candidate = lines[i]
            stripped = candidate.strip()
            if stripped and not candidate.startswith((" ", "\t")) and not stripped.startswith("#"):
                break
            if not stripped or candidate.startswith((" ", "\t")):
                i += 1
                continue
            break
        continue
    out.append(line)
    i += 1

while out and out[-1] == "":
    out.pop()

out.extend([
    "",
    "instances:",
    "  dir: ./.sts2/instances",
    f"  default: {name}",
    "  isolatedBuild: true",
])
path.write_text("\n".join(out) + "\n", encoding="utf-8")
PY

cp -r "$repo_root/target" "$dest/target"

ensure_link() {
  local rel="$1"
  local source="$repo_root/$rel"
  local target="$dest/$rel"
  mkdir -p "$(dirname "$target")"
  if [[ -L "$target" ]]; then
    current="$(readlink "$target")"
    if [[ "$current" == "$source" ]]; then
      return
    fi
    die "$target is a symlink to '$current', expected '$source'"
  fi
  if [[ -e "$target" ]]; then
    die "$target already exists and is not the expected symlink"
  fi
  ln -s "$source" "$target"
}

ensure_link ".vscode"
ensure_link ".sts2/toolchain"
ensure_link ".sts2/instances"
ensure_link ".agents"
ensure_link ".claude"
ensure_link ".ai"
ensure_link "presentation/web/node_modules"

printf '{"status":"ok","worktree":"%s","branch":"%s","instance":"%s"}\n' "$dest" "$branch" "$name"
