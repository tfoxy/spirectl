#!/usr/bin/env bash
# Exercise the working-tree script in isolated repositories, without touching real feedback.
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
mkdir -p "$repo_root/.local"
scratch="$(mktemp -d "$repo_root/.local/test-tool-improvement.XXXXXX")"
trap 'rm -rf "$scratch"' EXIT
export TMPDIR="$scratch/tmp"
mkdir -p "$TMPDIR" "$scratch/main/scripts"
unset SPIRECTL_TOOL_IMPROVEMENT_FILE
export SPIRECTL_TOOL_IMPROVEMENT_NOW='2026-11-13 12:00:00'
git init -q -b main "$scratch/main"
cp "$repo_root/scripts/tool-improvement.sh" "$scratch/main/scripts/"
git -C "$scratch/main" add scripts
git -C "$scratch/main" -c user.name=Test -c user.email=test@example.com \
  -c core.hooksPath=/dev/null -c commit.gpgsign=false commit -qm init
git -C "$scratch/main" worktree add -q "$scratch/linked" -b linked

add() {
  bash "$1/scripts/tool-improvement.sh" add --title "$2" --tool test \
    --problem problem --why reason --example example --suggestion suggestion
}

# Resolve relative to the script's checkout, even when invoked from somewhere else.
cd "$scratch"
add "$scratch/linked" First
python3 - "$scratch" <<'PY'
from pathlib import Path
import sys
root = Path(sys.argv[1])
text = (root / "main/.ai/tool-improvements.md").read_text()
assert text.startswith("# Tool improvement requests\n\n")
assert "- Title: First\n" in text
assert text.endswith("<!-- Add new requests above this line. -->\n")
assert not (root / "linked/.ai").exists()
PY

add "$scratch/linked" Second
python3 - "$scratch/main/.ai/tool-improvements.md" <<'PY'
from pathlib import Path
import sys
text = Path(sys.argv[1]).read_text()
assert text.index("- Title: First\n") < text.index("- Title: Second\n") < text.index("<!-- Add")
PY

SPIRECTL_TOOL_IMPROVEMENT_FILE="$scratch/override/nested/requests.md" add "$scratch/linked" Override
grep -q -- '- Title: Override' "$scratch/override/nested/requests.md"
! grep -q Override "$scratch/main/.ai/tool-improvements.md"

printf 'no marker\n' > "$scratch/invalid.md"
if SPIRECTL_TOOL_IMPROVEMENT_FILE="$scratch/invalid.md" add "$scratch/linked" Invalid \
    > "$scratch/error.log" 2>&1; then
  echo 'FAIL: accepted a file without the marker' >&2
  exit 1
fi
grep -q 'marker not found' "$scratch/error.log"
[[ "$(cat "$scratch/invalid.md")" == 'no marker' ]]
[[ -z "$(ls -A "$TMPDIR")" ]]

mkdir -p "$scratch/outside/scripts"
cp "$repo_root/scripts/tool-improvement.sh" "$scratch/outside/scripts/"
GIT_CEILING_DIRECTORIES="$scratch" add "$scratch/outside" Outside
grep -q -- '- Title: Outside' "$scratch/outside/.ai/tool-improvements.md"
echo 'tool-improvement self-test: 6 checks passed, 0 failures'
