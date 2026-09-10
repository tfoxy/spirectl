#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
json=false

for arg in "$@"; do
  case "$arg" in
    --json)
      json=true
      ;;
    -h|--help)
      cat <<'EOF'
Usage: scripts/verify_parallel.sh [--json]

Runs the repo validation stages and reports passed, failed, or environment-blocked
results without stopping before later independent stages.
EOF
      exit 0
      ;;
    *)
      printf 'error: unknown argument: %s\n' "$arg" >&2
      exit 2
      ;;
  esac
done

json_escape() {
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  value="${value//$'\n'/\\n}"
  value="${value//$'\r'/\\r}"
  value="${value//$'\t'/\\t}"
  printf '%s' "$value"
}

mise_tool_file() {
  if [[ -f "$repo_root/mise.toml" ]]; then
    printf '%s' "$repo_root/mise.toml"
  elif [[ -f "$repo_root/.mise.toml" ]]; then
    printf '%s' "$repo_root/.mise.toml"
  fi
}

read_pinned_tool_commands() {
  local tool_file="$1"
  local in_tools=false
  local line
  local cargo_seen=false
  local dotnet_seen=false
  local node_seen=false
  local npm_seen=false
  while IFS= read -r line; do
    if [[ "$line" =~ ^[[:space:]]*\[tools\][[:space:]]*$ ]]; then
      in_tools=true
      continue
    fi
    if [[ "$line" =~ ^[[:space:]]*\[.*\][[:space:]]*$ ]]; then
      in_tools=false
      continue
    fi
    [[ "$in_tools" == "true" ]] || continue
    [[ "$line" =~ ^[[:space:]]*([A-Za-z0-9_-]+)[[:space:]]*= ]] || continue
    case "${BASH_REMATCH[1]}" in
      rust)
        cargo_seen=true
        ;;
      dotnet)
        dotnet_seen=true
        ;;
      node)
        node_seen=true
        npm_seen=true
        ;;
    esac
  done <"$tool_file"

  [[ "$cargo_seen" == "true" ]] && printf '%s\n' cargo
  [[ "$dotnet_seen" == "true" ]] && printf '%s\n' dotnet
  [[ "$node_seen" == "true" ]] && printf '%s\n' node
  [[ "$npm_seen" == "true" ]] && printf '%s\n' npm
}

is_toolchain_setup_blocker() {
  local status="$1"
  local output_file="$2"
  if (( status == 127 )); then
    return 0
  fi
  rg -qi '(mise ERROR .* is not a valid shim|is not a valid shim|mise install|not installed|command not found|No such file or directory)' "$output_file"
}

emit_mise_preflight_blocked() {
  local tool_file="$1"
  shift
  local -a names=()
  local -a commands=()
  local -a codes=()
  local -a outputs=()
  while (( $# > 0 )); do
    names+=("$1")
    commands+=("$2")
    codes+=("$3")
    outputs+=("$4")
    shift 4
  done

  local guidance="Run mise install from the repository root, then rerun scripts/verify_parallel.sh --json."
  if [[ "$json" == "true" ]]; then
    printf '{"command":"verify_parallel","status":"blocked","code":"environment_blocked","preflight":{"name":"mise-tools","status":"blocked","code":"environment_blocked","toolFile":"%s","guidance":"%s","tools":[' \
      "$(json_escape "${tool_file#$repo_root/}")" \
      "$(json_escape "$guidance")"
    local i
    for i in "${!names[@]}"; do
      (( i == 0 )) || printf ','
      printf '{"tool":"%s","command":"%s","status":"blocked","exitCode":%s,"diagnostics":"%s"}' \
        "$(json_escape "${names[$i]}")" \
        "$(json_escape "${commands[$i]}")" \
        "${codes[$i]}" \
        "$(json_escape "$(tail -n 10 "${outputs[$i]}")")"
    done
    printf ']},"stages":[],"message":"validation blocked before stages by missing or invalid repo-pinned mise tools"}\n'
  else
    printf '[verify] preflight=mise-tools status=blocked code=environment_blocked\n' >&2
    local i
    for i in "${!names[@]}"; do
      printf '[verify] tool=%s command=%s exitCode=%s\n' "${names[$i]}" "${commands[$i]}" "${codes[$i]}" >&2
      tail -n 10 "${outputs[$i]}" >&2
    done
    printf '%s\n' "$guidance" >&2
  fi
}

preflight_mise_tools() {
  local tool_file
  tool_file="$(mise_tool_file)"
  [[ -n "$tool_file" ]] || return 0

  local -a blocked=()
  local command
  while IFS= read -r command; do
    [[ -n "$command" ]] || continue
    local output_file
    output_file="$(mktemp "${TMPDIR:-/tmp}/spirectl-verify-preflight-${command}.XXXXXX")"
    local status=0
    if ! command -v "$command" >/dev/null 2>&1; then
      printf '%s: command not found\n' "$command" >"$output_file"
      status=127
    else
      set +e
      "$command" --version >"$output_file" 2>&1
      status=$?
      set -e
    fi

    if (( status != 0 )) && is_toolchain_setup_blocker "$status" "$output_file"; then
      blocked+=("$command" "$command" "$status" "$output_file")
    else
      rm -f "$output_file"
    fi
  done < <(read_pinned_tool_commands "$tool_file")

  if (( ${#blocked[@]} > 0 )); then
    emit_mise_preflight_blocked "$tool_file" "${blocked[@]}"
    local i=3
    while (( i < ${#blocked[@]} )); do
      rm -f "${blocked[$i]}"
      i=$((i + 4))
    done
    exit 125
  fi
}

classify_stage() {
  local status="$1"
  local output_file="$2"
  if (( status == 0 )); then
    printf 'passed'
    return 0
  fi
  if (( status == 125 )) || rg -qi '(environment_blocked|System\.Net\.Sockets\.SocketException.*\(13\).*Permission denied|SocketServer\.Start|bind socket:.*PermissionDenied|kind: PermissionDenied|Operation not permitted|Permission denied|mise ERROR .* is not a valid shim|command not found|No such file or directory)' "$output_file"; then
    printf 'blocked'
    return 0
  fi
  printf 'failed'
}

preflight_mise_tools

stage_names=()
stage_statuses=()
stage_codes=()
stage_outputs=()
stage_log_paths=()
stage_argvs=()

validation_log_dir="$repo_root/.sts2/artifacts/validation/verify_parallel/$(date -u +%Y%m%dT%H%M%SZ)-$$"

run_stage() {
  local name="$1"
  shift
  local output_file
  output_file="$(mktemp "${TMPDIR:-/tmp}/spirectl-verify-${name}.XXXXXX")"

  if [[ "$json" != "true" ]]; then
    printf '[verify] stage=%s\n' "$name"
  fi

  set +e
  (
    unset SPIRECTL_BRIDGE_SOCKET_PATH SPIRECTL_BRIDGE_PIPE_NAME SPIRECTL_BRIDGE_TCP_ADDRESS
    unset STS2_AGENT_WORKSPACE STS2_HOST_CONTROL_SOCKET STS2_AGENT_IPC_DIR
    "$@"
  ) >"$output_file" 2>&1
  local command_status=$?
  set -e

  local classified
  classified="$(classify_stage "$command_status" "$output_file")"
  stage_names+=("$name")
  stage_statuses+=("$classified")
  stage_codes+=("$command_status")
  stage_outputs+=("$output_file")
  stage_argvs+=("$*")
  if [[ "$classified" == "failed" || "$classified" == "blocked" ]]; then
    mkdir -p "$validation_log_dir"
    local log_path="$validation_log_dir/${name}.log"
    cp "$output_file" "$log_path"
    stage_log_paths+=("${log_path#$repo_root/}")
  else
    stage_log_paths+=("")
  fi

  if [[ "$json" != "true" ]]; then
    cat "$output_file"
    if [[ "$classified" == "blocked" ]]; then
      printf '[verify] stage=%s status=blocked code=environment_blocked\n' "$name"
    else
      printf '[verify] stage=%s status=%s exitCode=%s\n' "$name" "$classified" "$command_status"
    fi
  fi
}

run_stage rust cargo test -p sts2
run_stage bridge "$repo_root/scripts/validate.sh" bridge-tests --json
run_stage npm "$repo_root/scripts/validate.sh" npm-wrapper-tests --json

overall_status="passed"
overall_code="ok"
has_blocked=false
has_failed=false
for status in "${stage_statuses[@]}"; do
  if [[ "$status" == "failed" ]]; then
    has_failed=true
  fi
  if [[ "$status" == "blocked" ]]; then
    has_blocked=true
  fi
done
if [[ "$has_blocked" == "true" ]]; then
  overall_status="blocked"
  overall_code="environment_blocked"
elif [[ "$has_failed" == "true" ]]; then
  overall_status="failed"
  overall_code="validation_failed"
fi

if [[ "$json" == "true" ]]; then
  printf '{"command":"verify_parallel","status":"%s","code":"%s","stages":[' "$overall_status" "$overall_code"
  for i in "${!stage_names[@]}"; do
    (( i == 0 )) || printf ','
    printf '{"name":"%s","status":"%s",' "$(json_escape "${stage_names[$i]}")" "$(json_escape "${stage_statuses[$i]}")"
    if [[ "${stage_statuses[$i]}" == "blocked" ]]; then
      printf '"code":"environment_blocked",'
    elif [[ "${stage_statuses[$i]}" == "passed" ]]; then
      printf '"code":"ok",'
    else
      printf '"code":"validation_failed",'
    fi
    printf '"exitCode":%s,"argv":"%s",' \
      "${stage_codes[$i]}" \
      "$(json_escape "${stage_argvs[$i]}")"
    if [[ -n "${stage_log_paths[$i]}" ]]; then
      printf '"logPath":"%s",' "$(json_escape "${stage_log_paths[$i]}")"
    fi
    printf '"outputSnippet":"%s"}' \
      "$(json_escape "$(tail -n 120 "${stage_outputs[$i]}")")"
  done
  printf '],"message":"'
  case "$overall_status" in
    passed) printf 'validation passed' ;;
    blocked) printf 'validation blocked by local environment restrictions' ;;
    failed) printf 'validation failed' ;;
  esac
  printf '"}\n'
fi

for output_file in "${stage_outputs[@]}"; do
  rm -f "$output_file"
done

case "$overall_status" in
  passed) exit 0 ;;
  blocked) exit 125 ;;
  *) exit 1 ;;
esac
