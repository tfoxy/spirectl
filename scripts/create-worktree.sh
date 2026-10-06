#!/usr/bin/env bash
# Create a configured Spirectl worktree in CouchCoop's shared .worktrees directory.
set -euo pipefail

usage() { printf 'usage: scripts/create-worktree.sh <name>\n'; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }

if [[ $# -eq 1 && ( "$1" == -h || "$1" == --help ) ]]; then usage; exit 0; fi
[[ $# -eq 1 ]] || { usage >&2; exit 2; }

name="$1"
[[ "$name" =~ ^[A-Za-z0-9._-]{1,64}$ && "$name" != .* ]] || die "invalid name '$name'"
case "$name" in auto|all) die "instance name '$name' is reserved";; esac

repo_root="$(git rev-parse --show-toplevel 2>/dev/null)" || die "not inside a git repository"
[[ "$(basename "$repo_root")" == spirectl ]] || die "run from the primary spirectl checkout, got '$repo_root'"
parent="$(dirname "$repo_root")"
couch_root="$parent/sts2-couch-coop"
gsw_root="$parent/godot-scene-web"
[[ -d "$couch_root/.git" || -f "$couch_root/.git" ]] || die "missing CouchCoop checkout: $couch_root"
[[ -d "$gsw_root/.git" || -f "$gsw_root/.git" ]] || die "missing godot-scene-web checkout: $gsw_root"

source_config="$repo_root/sts2.local.yaml"
[[ -f "$source_config" ]] || die "missing $source_config; create local config before making worktrees"
for rel in .vscode .sts2/toolchain .sts2/instances .agents .claude .ai presentation/web/node_modules; do
  [[ -e "$repo_root/$rel" || -L "$repo_root/$rel" ]] || die "missing shared local path: $repo_root/$rel"
done

worktrees_root="$couch_root/.worktrees"
dest="$worktrees_root/spirectl-$name"
branch="worktree/$name"
[[ ! -e "$dest" && ! -L "$dest" ]] || die "destination already exists: $dest"
git -C "$repo_root" show-ref --verify --quiet "refs/heads/$branch" && die "branch already exists: $branch"

check_shared_link() {
  local link="$1" target="$2"
  if [[ -L "$link" ]]; then
    [[ "$(realpath "$link")" == "$(realpath "$target")" ]] || die "$link points somewhere unexpected: $(readlink "$link")"
  elif [[ -e "$link" ]]; then
    die "$link exists and is not the expected symlink"
  fi
}
check_shared_link "$worktrees_root/spirectl" "$repo_root"
check_shared_link "$worktrees_root/godot-scene-web" "$gsw_root"

root_links_created=()
created_worktree=0
cleanup() {
  if (( created_worktree )); then
    git -C "$repo_root" worktree remove --force "$dest" >/dev/null 2>&1 || true
    git -C "$repo_root" branch -D "$branch" >/dev/null 2>&1 || true
  fi
  for link in "${root_links_created[@]}"; do rm -f "$link"; done
  rmdir "$worktrees_root" 2>/dev/null || true
}
on_exit() { local status=$?; if (( status != 0 )); then cleanup; fi; }
trap on_exit EXIT

mkdir -p "$worktrees_root"
[[ ! -L "$worktrees_root" ]] || die "$worktrees_root must be a real directory, not a symlink"
ensure_shared_link() {
  local link="$1" target="$2" relative
  if [[ -L "$link" ]]; then return; fi
  relative="$(realpath --relative-to="$(dirname "$link")" "$target")"
  ln -s "$relative" "$link"
  root_links_created+=("$link")
}
ensure_shared_link "$worktrees_root/spirectl" "$repo_root"
ensure_shared_link "$worktrees_root/godot-scene-web" "$gsw_root"

git -C "$repo_root" worktree add -b "$branch" "$dest" HEAD >&2
created_worktree=1
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
out.extend(["", "instances:", "  dir: ./.sts2/instances", f"  default: {name}", "  isolatedBuild: true"])
path.write_text("\n".join(out) + "\n", encoding="utf-8")
PY

ensure_relative_link() {
  local rel="$1" source="$repo_root/$1" target="$dest/$1" relative
  mkdir -p "$(dirname "$target")"
  if [[ -e "$target" || -L "$target" ]]; then
    [[ -L "$target" && "$(realpath "$target")" == "$(realpath "$source")" ]] || die "$target already exists and is not the expected link"
    return
  fi
  relative="$(realpath --relative-to="$(dirname "$target")" "$source")"
  ln -s "$relative" "$target"
}

ensure_relative_link .vscode
mkdir -p "$dest/.sts2"
ensure_relative_link .sts2/toolchain
ensure_relative_link .sts2/instances
ensure_relative_link .agents
ensure_relative_link .claude
ensure_relative_link .ai
ensure_relative_link presentation/web/node_modules

common_dir="$(git -C "$repo_root" rev-parse --path-format=absolute --git-common-dir)"
exclude_file="$common_dir/info/exclude"
mkdir -p "$(dirname "$exclude_file")"
touch "$exclude_file"
for pattern in node_modules presentation/web/node_modules; do
  grep -qxF "$pattern" "$exclude_file" || printf '%s\n' "$pattern" >> "$exclude_file"
done

trap - EXIT
python3 - "$dest" "$branch" "$name" <<'PY'
import json
import sys

print(json.dumps({
    "status": "ok",
    "worktree": sys.argv[1],
    "branch": sys.argv[2],
    "instance": sys.argv[3],
    "targetCache": "not-copied",
}))
PY
