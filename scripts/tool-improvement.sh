#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
target_file="${SPIRECTL_TOOL_IMPROVEMENT_FILE:-$repo_root/.ai/tool-improvements.md}"

usage() {
  cat <<'EOF'
Usage: scripts/tool-improvement.sh add --title <title> --tool <tool> --problem <problem> --why <why> --example <example> --suggestion <suggestion>

Adds a request to .ai/tool-improvements.md with the current local timestamp.
EOF
}

json_escape() {
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  value="${value//$'\n'/\\n}"
  value="${value//$'\r'/\\r}"
  value="${value//$'\t'/\\t}"
  printf '%s' "$value"
}

now_timestamp() {
  if [[ -n "${SPIRECTL_TOOL_IMPROVEMENT_NOW:-}" ]]; then
    printf '%s' "$SPIRECTL_TOOL_IMPROVEMENT_NOW"
  else
    date '+%Y-%m-%d %H:%M:%S'
  fi
}

add_request() {
  local title="" tool="" problem="" why="" example="" suggestion=""
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --title)
        title="${2:-}"
        shift 2
        ;;
      --tool)
        tool="${2:-}"
        shift 2
        ;;
      --problem)
        problem="${2:-}"
        shift 2
        ;;
      --why)
        why="${2:-}"
        shift 2
        ;;
      --example)
        example="${2:-}"
        shift 2
        ;;
      --suggestion)
        suggestion="${2:-}"
        shift 2
        ;;
      -h|--help)
        usage
        exit 0
        ;;
      *)
        printf 'unknown argument: %s\n' "$1" >&2
        usage >&2
        exit 2
        ;;
    esac
  done

  local field
  for field in title tool problem why example suggestion; do
    if [[ -z "${!field}" ]]; then
      printf 'missing required --%s\n' "$field" >&2
      usage >&2
      exit 2
    fi
  done

  local entry
  entry="$(mktemp "${TMPDIR:-/tmp}/spirectl-tool-improvement.XXXXXX")"
  local timestamp
  timestamp="$(now_timestamp)"
  {
    printf -- '- Date: %s\n' "$timestamp"
    printf -- '- Title: %s\n' "$title"
    printf -- '- Tool: %s\n' "$tool"
    printf -- '- Problem: %s\n' "$problem"
    printf -- '- Why it matters: %s\n' "$why"
    printf -- '- Example command/output: %s\n' "$example"
    printf -- '- Suggested improvement: %s\n\n' "$suggestion"
  } >"$entry"

  python3 - "$target_file" "$entry" <<'PY'
from pathlib import Path
import sys

target = Path(sys.argv[1])
entry = Path(sys.argv[2]).read_text(encoding="utf-8")
text = target.read_text(encoding="utf-8")
marker = "<!-- Add new requests above this line. -->"
if marker not in text:
    raise SystemExit(f"marker not found in {target}")
target.write_text(text.replace(marker, entry + marker, 1), encoding="utf-8")
PY
  rm -f "$entry"

  if [[ "${SPIRECTL_TOOL_IMPROVEMENT_JSON:-}" == "true" ]]; then
    printf '{"status":"added","path":"%s","timestamp":"%s","title":"%s"}\n' \
      "$(json_escape "${target_file#$repo_root/}")" \
      "$(json_escape "$timestamp")" \
      "$(json_escape "$title")"
  fi
}

case "${1:-}" in
  add)
    shift
    add_request "$@"
    ;;
  -h|--help|"")
    usage
    ;;
  *)
    printf 'unknown command: %s\n' "$1" >&2
    usage >&2
    exit 2
    ;;
esac
