#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

usage() {
  cat <<'EOF'
Usage: scripts/validate.sh <command> [options]

Commands:
  dotnet-format           Verify whitespace and style formatting for selected .NET/C# paths.
  bridge-tests            Run the default serial bridge validation command.
  bridge-build            Build the bridge host with serial MSBuild.
  bridge-live-host-tests  Run live-host-gated bridge tests.
  npm-wrapper-tests       Run npm-wrapper tests with sandbox-blocker diagnostics.
  cli-tests               Run cargo fmt and the normal sts2 CLI integration test set.
  rust-proto-selected     Validate selected Rust/protobuf paths in isolation.
  cargo-test-filter       Run one focused Rust integration test filter and fail if it executes zero tests.
  cargo-unit-test-filter  Run one focused Rust package/unit test filter and fail if it executes zero tests.
  cargo-package-sts2      Run the sts2 crate packaging gate with structured unsupported diagnostics.
  m78-live-encounter-artifacts Preflight live bridge and export standard encounter visual artifacts.
  producer-walk-profile   Emit the producer-walk perf report envelope from the scene watcher's profiler counters.
  docs-map-paths          Verify every backticked repo path in docs/maps/*.md still exists.

Examples:
  scripts/validate.sh dotnet-format --include bridge-mod/src/Foo.cs --json
  scripts/validate.sh bridge-tests --json
  scripts/validate.sh bridge-live-host-tests --filter FullyQualifiedName~MapScreenInspector --assemblies-dir /tmp/sts2-assemblies --json
  scripts/validate.sh npm-wrapper-tests --json
  scripts/validate.sh cli-tests --json
  scripts/validate.sh rust-proto-selected --path proto/spirectl/v0/runtime.proto --json
  scripts/validate.sh cargo-test-filter --package sts2 --test cli_snapshots --filter inspect_commands --json
  scripts/validate.sh cargo-unit-test-filter --package sts2 --filter state_actions --json
  scripts/validate.sh cargo-package-sts2 --json
  scripts/validate.sh m78-live-encounter-artifacts --encounter kaiser_crab_boss --json
  scripts/validate.sh producer-walk-profile --log .sts2/perf-reports/producer-walk.log --json
  scripts/validate.sh docs-map-paths --json
EOF
}

resolve_sts2_assemblies_dir() {
  local explicit_dir="${1:-}"
  if [[ -n "$explicit_dir" ]]; then
    printf '%s' "$explicit_dir"
    return 0
  fi
  if [[ -n "${STS2_ASSEMBLIES_DIR:-}" ]]; then
    printf '%s' "$STS2_ASSEMBLIES_DIR"
    return 0
  fi

  local config_file
  for config_file in "sts2.local.yaml" "sts2.config.yaml"; do
    if [[ -f "$repo_root/$config_file" ]]; then
      local resolved
      resolved="$(awk '
        /^[[:space:]]*game:[[:space:]]*$/ { in_game = 1; next }
        /^[^[:space:]]/ { in_game = 0 }
        /^[[:space:]]*game[[:space:]]*:[[:space:]]*[^[:space:]]/ { in_game = 0 }
        /^[[:space:]]*assembliesDir:[[:space:]]*/ {
          if (in_game || $0 ~ /^[^[:space:]]/) {
            sub(/^[^:]*:[[:space:]]*/, "", $0)
            print $0
            exit
          }
        }
      ' "$repo_root/$config_file" | sed -E 's/^[[:space:]]*"?([^"]*)"?[[:space:]]*$/\1/')"
      if [[ -n "$resolved" ]]; then
        printf '%s' "$resolved"
        return 0
      fi
      resolved="$(awk -F': ' '/^[[:space:]]*assemblies_dir:[[:space:]]*/ { print $2; exit }' "$repo_root/$config_file" | sed -E 's/^[[:space:]]*"?([^"]*)"?[[:space:]]*$/\1/')"
      if [[ -n "$resolved" ]]; then
        printf '%s' "$resolved"
        return 0
      fi
      resolved="$(awk -F': ' '/^[[:space:]]*assembliesDir:[[:space:]]*/ { print $2; exit }' "$repo_root/$config_file" | sed -E 's/^[[:space:]]*"?([^"]*)"?[[:space:]]*$/\1/')"
      if [[ -n "$resolved" ]]; then
        printf '%s' "$resolved"
        return 0
      fi
    fi
  done

  return 1
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

emit_json_file_or_text_field() {
  local json_field="$1"
  local text_field="$2"
  local file="$3"

  if command -v python3 >/dev/null 2>&1; then
    local json_value
    if json_value="$(python3 - "$file" <<'PY'
import json
import sys

with open(sys.argv[1], "r", encoding="utf-8", errors="replace") as fh:
    text = fh.read()
value = None
for line in reversed(text.splitlines()):
    line = line.strip()
    if not line.startswith("{"):
        continue
    try:
        value = json.loads(line)
        break
    except json.JSONDecodeError:
        continue
if value is None:
    raise SystemExit(1)
print(json.dumps(value, separators=(",", ":")))
PY
    2>/dev/null)"; then
      printf ',"%s":%s' "$(json_escape "$json_field")" "$json_value"
      return
    fi
  fi

  printf ',"%s":"%s"' "$(json_escape "$text_field")" "$(json_escape "$(cat "$file")")"
}

m78_structured_isolation_failure_json() {
  local extract_output="$1"

  command -v python3 >/dev/null 2>&1 || return 1
  python3 - "$extract_output" <<'PY'
import json
import re
import sys

with open(sys.argv[1], "r", encoding="utf-8", errors="replace") as handle:
    text = handle.read()

payload = None
for line in reversed(text.splitlines()):
    line = line.strip()
    if not line.startswith("{"):
        continue
    try:
        payload = json.loads(line)
        break
    except json.JSONDecodeError:
        continue

if not isinstance(payload, dict):
    raise SystemExit(1)

results = payload.get("results")
if not isinstance(results, list):
    raise SystemExit(1)

failures = []
unsupported = []
for result in results:
    if not isinstance(result, dict):
        continue
    status = result.get("status")
    result_id = result.get("id")
    if status not in ("failed", "partial") and not isinstance(result.get("error"), dict):
        continue
    if not (isinstance(result_id, str) and (result_id.endswith("-rocket-overlay") or result_id.endswith("-rocket-part"))):
        unsupported.append(result_id)
        continue
    details = (result.get("error") or {}).get("details")
    diagnostic = details.get("renderDiagnostics") if isinstance(details, dict) else None
    message = " ".join(
        str(value)
        for value in (
            (result.get("error") or {}).get("code") if isinstance(result.get("error"), dict) else None,
            (result.get("error") or {}).get("message") if isinstance(result.get("error"), dict) else None,
        )
        if value
    )
    if isinstance(diagnostic, dict) and (
        "selector" in message.lower()
        or "isolation" in message.lower()
        or diagnostic.get("renderTargetDecision") is not None
    ):
        failures.append({
            "id": result_id,
            "status": status or "failed",
            "error": result.get("error"),
            "renderDiagnostics": diagnostic,
            "notes": [],
        })
        continue
    exports = result.get("exports")
    if not isinstance(exports, list) or not exports:
        unsupported.append(result_id)
        continue
    matched = False
    for export in exports:
        if not isinstance(export, dict):
            continue
        details = (result.get("error") or {}).get("details")
        diagnostic = details.get("renderDiagnostics") if isinstance(details, dict) else None
        diagnostic = diagnostic or export.get("renderDiagnostics")
        diagnostic = diagnostic or export.get("diagnostic")
        if export.get("status") == "live_render_failed" and isinstance(diagnostic, dict):
            message = " ".join(
                str(value)
                for value in (
                    export.get("message"),
                    (result.get("error") or {}).get("message") if isinstance(result.get("error"), dict) else None,
                    " ".join(export.get("notes") or []),
                )
                if value
            )
            if (
                "selector" in message.lower()
                or "isolation" in message.lower()
                or "fully transparent" in message.lower()
                or diagnostic.get("renderTargetDecision") is not None
            ):
                failures.append({
                    "id": result_id,
                    "status": export.get("status"),
                    "error": result.get("error"),
                    "renderDiagnostics": diagnostic,
                    "notes": export.get("notes") or [],
                })
                matched = True
                break
        notes = export.get("notes") or []
        note_text = " ".join(str(note) for note in notes)
        if export.get("status") == "live_render_failed" and "fully transparent" in note_text.lower():
            kept = re.findall(r"Resolved and kept catalog selector '([^']+)' for visual part '([^']+)'", note_text)
            hidden = re.findall(r"Resolved and hidden catalog selector '([^']+)' for visual part '([^']+)'", note_text)
            decision = None
            decision_match = re.search(r"Encounter render target decision '([^']+)' for '([^']+)': ([^.]+(?:\.)?)", note_text)
            if decision_match:
                decision = {
                    "decision": decision_match.group(1),
                    "targetId": decision_match.group(2),
                    "reason": decision_match.group(3),
                }
            if kept or hidden or decision:
                failures.append({
                    "id": result_id,
                    "status": export.get("status"),
                    "error": result.get("error"),
                    "renderTargetId": (decision or {}).get("targetId") or (result.get("metadata") or {}).get("renderTarget"),
                    "keptPartIds": [part_id for _, part_id in kept],
                    "hiddenPartIds": [part_id for _, part_id in hidden],
                    "renderTargetDecision": decision,
                    "notes": notes,
                })
                matched = True
                break
    if not matched:
        unsupported.append(result_id)

if not failures or unsupported:
    raise SystemExit(1)

print(json.dumps(failures, separators=(",", ":")))
PY
}

emit_m78_preflight_failure_json() {
  local command_name="$1"
  local encounter_id="$2"
  local preflight_output="$3"
  local preflight_status="$4"
  local fallback_next1="$5"
  local fallback_next2="$6"
  shift 6

  if command -v python3 >/dev/null 2>&1; then
    python3 - "$command_name" "$encounter_id" "$preflight_output" "$preflight_status" "$fallback_next1" "$fallback_next2" "$@" <<'PY'
import json
import sys

command_name, encounter_id, output_path, exit_code, fallback_next1, fallback_next2, *argv = sys.argv[1:]
with open(output_path, "r", encoding="utf-8", errors="replace") as fh:
    raw = fh.read()

payload = None
stripped_raw = raw.strip()
if stripped_raw:
    try:
        payload = json.loads(stripped_raw)
    except json.JSONDecodeError:
        payload = None

if payload is None:
    decoder = json.JSONDecoder()
    for index, char in enumerate(raw):
        if char != "{":
            continue
        try:
            candidate, _ = decoder.raw_decode(raw[index:])
        except json.JSONDecodeError:
            continue
        if isinstance(candidate, dict):
            payload = candidate
            if "preflight" in candidate:
                break

if payload is None:
    for line in reversed(raw.splitlines()):
        stripped = line.strip()
        if not stripped:
            continue
        try:
            payload = json.loads(stripped)
            break
        except json.JSONDecodeError:
            continue

preflight = {}
if isinstance(payload, dict):
    nested = payload.get("preflight")
    if isinstance(nested, dict):
        preflight.update(nested)
    for key in ("status", "code", "latestLog", "diagnostics", "nextCommands"):
        if key in payload and key not in preflight:
            preflight[key] = payload[key]
    error = payload.get("error")
    if isinstance(error, dict):
        if "code" not in preflight and "code" in error:
            preflight["code"] = error["code"]
        if "latestLog" not in preflight and "latestLog" in error:
            preflight["latestLog"] = error["latestLog"]
        if "diagnostics" not in preflight and "diagnostics" in error:
            preflight["diagnostics"] = error["diagnostics"]

preflight_status = preflight.get("status")
preflight_code = preflight.get("code")
if not isinstance(preflight_status, str):
    preflight_status = preflight_code if isinstance(preflight_code, str) else None
if not isinstance(preflight_code, str):
    preflight_code = preflight_status if isinstance(preflight_status, str) else "live_host_unavailable"

readiness_failure_codes = {
    "stale_live_host",
    "endpoint_refused_or_stale",
    "endpoint_missing",
    "ipc_socket_missing",
    "ipc_connection_failed",
    "rpc_timeout",
    "bridge_rpc_timeout",
    "launch_exited_before_ipc",
    "launch_timeout",
    "live_host_unavailable",
}
readiness_failure_statuses = readiness_failure_codes | {"unavailable", "blocked"}
is_environment_blocked = (
    preflight_code in readiness_failure_codes
    or (isinstance(preflight_status, str) and preflight_status in readiness_failure_statuses)
)

if "nextCommands" not in preflight or not isinstance(preflight["nextCommands"], list):
    preflight["nextCommands"] = [fallback_next1, fallback_next2]
if "diagnostics" not in preflight:
    preflight["diagnostics"] = "\n".join(raw.splitlines()[-30:])
preflight["argv"] = argv
preflight["exitCode"] = int(exit_code)
if preflight_status is not None:
    preflight["status"] = preflight_status
preflight["code"] = preflight_code

result = {
    "command": command_name,
    "status": "blocked" if is_environment_blocked else "unavailable",
    "code": "environment_blocked" if is_environment_blocked else "live_host_unavailable",
    "encounter": encounter_id,
    "preflight": preflight,
    "generatedOutputs": [],
    "nextCommands": preflight["nextCommands"],
}
print(json.dumps(result, separators=(",", ":")))
PY
    return
  fi

  printf '{"command":"%s","status":"unavailable","code":"live_host_unavailable","encounter":"%s","preflight":{"argv":[' "$(json_escape "$command_name")" "$(json_escape "$encounter_id")"
  local first=1
  local arg
  for arg in "$@"; do
    if (( first )); then
      first=0
    else
      printf ','
    fi
    printf '"%s"' "$(json_escape "$arg")"
  done
  printf '],"exitCode":%s,"diagnostics":"%s"},"generatedOutputs":[],"nextCommands":["%s","%s"]}\n' \
    "$preflight_status" \
    "$(json_escape "$(tail -n 30 "$preflight_output")")" \
    "$(json_escape "$fallback_next1")" \
    "$(json_escape "$fallback_next2")"
}

emit_json_failure() {
  local command="$1"
  local code="$2"
  local message="$3"
  shift 3
  printf '{"command":"%s","status":"failed","code":"%s","argv":[' "$(json_escape "$command")" "$(json_escape "$code")"
  local first=1
  for arg in "$@"; do
    if (( first )); then
      first=0
    else
      printf ','
    fi
    printf '"%s"' "$(json_escape "$arg")"
  done
  printf '],"message":"%s"}\n' "$(json_escape "$message")"
}

emit_json_success() {
  local command="$1"
  local message="$2"
  shift 2
  printf '{"command":"%s","status":"passed","code":"ok","argv":[' "$(json_escape "$command")"
  local first=1
  for arg in "$@"; do
    if (( first )); then
      first=0
    else
      printf ','
    fi
    printf '"%s"' "$(json_escape "$arg")"
  done
  printf '],"message":"%s"}\n' "$(json_escape "$message")"
}

fail() {
  local command="$1"
  local code="$2"
  local message="$3"
  shift 3
  local json="${VALIDATE_JSON:-false}"
  if [[ "$json" == "true" ]]; then
    emit_json_failure "$command" "$code" "$message" "$@"
  else
    printf 'error: %s\n' "$message" >&2
  fi
  exit 2
}

normalize_repo_path() {
  local path="$1"
  local normalized
  if [[ "$path" = /* ]]; then
    normalized="$(realpath -m --relative-to="$repo_root" "$path")"
  else
    normalized="$(realpath -m --relative-to="$repo_root" "$repo_root/$path")"
  fi
  if [[ "$normalized" == .. || "$normalized" == ../* ]]; then
    return 1
  fi
  printf '%s' "$normalized"
}

rust_proto_selected_overlay_candidate() {
  local dirty_path="$1"
  case "$dirty_path" in
    cli/Cargo.toml|cli/*.rs|cli/src/*.rs|cli/tests/*.rs|proto/*.proto|proto/spirectl/*.proto|proto/spirectl/v0/*.proto)
      return 0
      ;;
    *)
      return 1
      ;;
  esac
}

copy_or_remove_overlay_path() {
  local tmpdir="$1"
  local dirty_path="$2"
  if [[ -e "$repo_root/$dirty_path" ]]; then
    mkdir -p "$tmpdir/$(dirname "$dirty_path")"
    cp -a "$repo_root/$dirty_path" "$tmpdir/$dirty_path"
  else
    rm -rf "$tmpdir/$dirty_path"
  fi
}

git_status_dirty_paths_z() {
  local record path extra code
  while IFS= read -r -d '' record; do
    code="${record:0:2}"
    path="${record:3}"
    case "$code" in
      R*|*R)
        IFS= read -r -d '' extra || true
        printf '%s\0' "$path"
        [[ -n "$extra" ]] && printf '%s\0' "$extra"
        ;;
      C*|*C)
        IFS= read -r -d '' extra || true
        printf '%s\0' "$path"
        ;;
      *)
        printf '%s\0' "$path"
        ;;
    esac
  done < <(git -C "$repo_root" status --porcelain=v1 -z)
}

run_and_report() {
  local command_name="$1"
  shift
  if (
    unset SPIRECTL_BRIDGE_SOCKET_PATH SPIRECTL_BRIDGE_PIPE_NAME SPIRECTL_BRIDGE_TCP_ADDRESS
    unset STS2_AGENT_WORKSPACE STS2_HOST_CONTROL_SOCKET STS2_AGENT_IPC_DIR
    "$@"
  ); then
    if [[ "$VALIDATE_JSON" == "true" ]]; then
      emit_json_success "$command_name" "validation passed" "$@"
    fi
  else
    local status=$?
    if [[ "$VALIDATE_JSON" == "true" ]]; then
      emit_json_failure "$command_name" "validation_failed" "validation command failed with exit code $status" "$@"
    else
      printf 'validation command failed with exit code %s\n' "$status" >&2
    fi
    return "$status"
  fi
}

run_command_capture() {
  local output_file="$1"
  shift
  set +e
  (
    unset SPIRECTL_BRIDGE_SOCKET_PATH SPIRECTL_BRIDGE_PIPE_NAME SPIRECTL_BRIDGE_TCP_ADDRESS
    unset STS2_AGENT_WORKSPACE STS2_HOST_CONTROL_SOCKET STS2_AGENT_IPC_DIR
    "$@"
  ) >"$output_file" 2>&1
  local status=$?
  set -e
  return "$status"
}

run_cargo_test_filter() {
  local command_name="$1"
  local package="$2"
  local test_target="$3"
  local filter="$4"

  local output_file
  output_file="$(mktemp "${TMPDIR:-/tmp}/spirectl-cargo-test-filter.XXXXXX")"
  local -a argv=(cargo test --color never -p "$package" --test "$test_target" "$filter")

  local status=0
  if run_command_capture "$output_file" "${argv[@]}"; then
    if ! rg -q '^test result: ok\. [1-9][0-9]* passed;' "$output_file"; then
      if [[ "$VALIDATE_JSON" == "true" ]]; then
        printf '{"command":"%s","status":"failed","code":"zero_tests_matched","argv":[' "$(json_escape "$command_name")"
        local i
        for i in "${!argv[@]}"; do
          (( i == 0 )) || printf ','
          printf '"%s"' "$(json_escape "${argv[$i]}")"
        done
        printf '],"package":"%s","test":"%s","filter":"%s","diagnostics":"%s","message":"cargo test filter executed zero tests"}\n' \
          "$(json_escape "$package")" \
          "$(json_escape "$test_target")" \
          "$(json_escape "$filter")" \
          "$(json_escape "$(tail -n 80 "$output_file")")"
      else
        cat "$output_file" >&2
        printf 'cargo test filter executed zero tests\n' >&2
      fi
      rm -f "$output_file"
      return 1
    fi

    if [[ "$VALIDATE_JSON" == "true" ]]; then
      printf '{"command":"%s","status":"passed","code":"ok","argv":[' "$(json_escape "$command_name")"
      local i
      for i in "${!argv[@]}"; do
        (( i == 0 )) || printf ','
        printf '"%s"' "$(json_escape "${argv[$i]}")"
      done
      printf '],"package":"%s","test":"%s","filter":"%s","message":"validation passed"}\n' \
        "$(json_escape "$package")" \
        "$(json_escape "$test_target")" \
        "$(json_escape "$filter")"
    fi
    rm -f "$output_file"
    return 0
  else
    status=$?
  fi

  if [[ "$VALIDATE_JSON" == "true" ]]; then
    printf '{"command":"%s","status":"failed","code":"validation_failed","argv":[' "$(json_escape "$command_name")"
    local i
    for i in "${!argv[@]}"; do
      (( i == 0 )) || printf ','
      printf '"%s"' "$(json_escape "${argv[$i]}")"
    done
    printf '],"package":"%s","test":"%s","filter":"%s","exitCode":%s,"diagnostics":"%s","message":"cargo test filter failed with exit code %s"}\n' \
      "$(json_escape "$package")" \
      "$(json_escape "$test_target")" \
      "$(json_escape "$filter")" \
      "$status" \
      "$(json_escape "$(tail -n 80 "$output_file")")" \
      "$status"
  else
    cat "$output_file" >&2
    printf 'cargo test filter failed with exit code %s\n' "$status" >&2
  fi
  rm -f "$output_file"
  return "$status"
}

run_cargo_unit_test_filter() {
  local command_name="$1"
  local package="$2"
  local filter="$3"

  local output_file
  output_file="$(mktemp "${TMPDIR:-/tmp}/spirectl-cargo-unit-test-filter.XXXXXX")"
  local -a argv=(cargo test --color never -p "$package" "$filter")

  local status=0
  if run_command_capture "$output_file" "${argv[@]}"; then
    if ! rg -q '^test result: ok\. [1-9][0-9]* passed;' "$output_file"; then
      if [[ "$VALIDATE_JSON" == "true" ]]; then
        printf '{"command":"%s","status":"failed","code":"zero_tests_matched","argv":[' "$(json_escape "$command_name")"
        local i
        for i in "${!argv[@]}"; do
          (( i == 0 )) || printf ','
          printf '"%s"' "$(json_escape "${argv[$i]}")"
        done
        printf '],"package":"%s","filter":"%s","diagnostics":"%s","message":"cargo unit test filter executed zero tests"}\n' \
          "$(json_escape "$package")" \
          "$(json_escape "$filter")" \
          "$(json_escape "$(tail -n 80 "$output_file")")"
      else
        cat "$output_file" >&2
        printf 'cargo unit test filter executed zero tests\n' >&2
      fi
      rm -f "$output_file"
      return 1
    fi

    if [[ "$VALIDATE_JSON" == "true" ]]; then
      printf '{"command":"%s","status":"passed","code":"ok","argv":[' "$(json_escape "$command_name")"
      local i
      for i in "${!argv[@]}"; do
        (( i == 0 )) || printf ','
        printf '"%s"' "$(json_escape "${argv[$i]}")"
      done
      printf '],"package":"%s","filter":"%s","message":"validation passed"}\n' \
        "$(json_escape "$package")" \
        "$(json_escape "$filter")"
    fi
    rm -f "$output_file"
    return 0
  else
    status=$?
  fi

  if [[ "$VALIDATE_JSON" == "true" ]]; then
    printf '{"command":"%s","status":"failed","code":"validation_failed","argv":[' "$(json_escape "$command_name")"
    local i
    for i in "${!argv[@]}"; do
      (( i == 0 )) || printf ','
      printf '"%s"' "$(json_escape "${argv[$i]}")"
    done
    printf '],"package":"%s","filter":"%s","exitCode":%s,"diagnostics":"%s","message":"cargo unit test filter failed with exit code %s"}\n' \
      "$(json_escape "$package")" \
      "$(json_escape "$filter")" \
      "$status" \
      "$(json_escape "$(tail -n 80 "$output_file")")" \
      "$status"
  else
    cat "$output_file" >&2
    printf 'cargo unit test filter failed with exit code %s\n' "$status" >&2
  fi
  rm -f "$output_file"
  return "$status"
}

detect_locked_bridge_ref_output() {
  local output_file="$1"
  if ! rg -q '(MSB3883|IOException|MSB3021|MSB3027|process cannot access the file)' "$output_file"; then
    return 1
  fi
  rg -q 'bridge-mod/.*/obj/.*/ref/[^/]+\.dll' "$output_file"
}

detect_socket_environment_block() {
  local output_file="$1"
  rg -qi '(System\.Net\.Sockets\.SocketException.*\(13\).*Permission denied|SocketServer\.Start|bind socket:.*PermissionDenied|kind: PermissionDenied|Operation not permitted|Permission denied)' "$output_file"
}

npm_environment_block_pattern='(service_bind_failed|spawnSync [^[:space:]]+ EPERM|spawn [^[:space:]]+ EPERM|EPERM: operation not permitted|EACCES: permission denied|Operation not permitted|Permission denied|listen EACCES|listen EPERM|bind EACCES|bind EPERM)'

detect_npm_environment_block() {
  local output_file="$1"
  rg -qi "$npm_environment_block_pattern" "$output_file"
}

npm_environment_block_diagnostics() {
  local aggregate_output="$1"
  local direct_output="$2"
  {
    rg -i "$npm_environment_block_pattern" "$aggregate_output" "$direct_output" || true
  } | tail -n 20
}

emit_bridge_environment_blocked() {
  local command_name="$1"
  local output_file="$2"
  local retry_performed="$3"
  shift 3
  local message="validation could not run because this environment blocks local test socket binding"
  local guidance="rerun on a host that permits local Unix/loopback test sockets, or use this blocked result as infrastructure evidence instead of a regression"
  if [[ "$VALIDATE_JSON" == "true" ]]; then
    printf '{"command":"%s","status":"blocked","code":"environment_blocked","argv":[' "$(json_escape "$command_name")"
    local first=1
    for arg in "$@"; do
      if (( first )); then first=0; else printf ','; fi
      printf '"%s"' "$(json_escape "$arg")"
    done
    printf '],"retried":%s,"message":"%s","guidance":"%s","diagnostics":"%s"}\n' \
      "$retry_performed" \
      "$(json_escape "$message")" \
      "$(json_escape "$guidance")" \
      "$(json_escape "$(tail -n 20 "$output_file")")"
  else
    cat "$output_file" >&2
    printf 'environment blocked: %s\n%s\n' "$message" "$guidance" >&2
  fi
}

cleanup_bridge_ref_outputs() {
  local -n attempted_ref=$1
  local -n removed_ref=$2
  attempted_ref=()
  removed_ref=()

  local find_roots=(
    "bridge-mod/src"
    "bridge-mod/tests/Spirectl.BridgeMod.Tests"
  )
  local root
  local file
  for root in "${find_roots[@]}"; do
    [[ -d "$repo_root/$root" ]] || continue
    while IFS= read -r file; do
      local bridge_src_ref='^bridge-mod/src/Spirectl\.BridgeMod[^/]*/obj/.*/ref/[^/]+\.dll$'
      local bridge_test_ref='^bridge-mod/tests/Spirectl\.BridgeMod\.Tests/obj/.*/ref/[^/]+\.dll$'
      if [[ ! "$file" =~ $bridge_src_ref && ! "$file" =~ $bridge_test_ref ]]; then
        continue
      fi
      attempted_ref+=("$file")
      if rm -f "$repo_root/$file"; then
        removed_ref+=("$file")
      fi
    done < <(
      cd "$repo_root" &&
        find "$root" -type f -name '*.dll' -path '*/obj/*/ref/*.dll' -print
    )
  done
}

# Every dotnet leg below adds `-nodeReuse:false -p:UseSharedCompilation=false`. Both node reuse and
# the shared Roslyn compiler leave long-lived MSBuild/VBCSCompiler processes behind after the build
# exits. Those orphans hold file locks on the bridge outputs (the "locked bridge ref" retry below is
# the symptom) and inherit whatever file descriptors their parent had — which is how a build run
# under a lock ends up holding that lock after the build is gone.
run_bridge_command_with_locked_retry() {
  local command_name="$1"
  shift
  local output_file
  output_file="$(mktemp "${TMPDIR:-/tmp}/spirectl-validate-bridge.XXXXXX")"
  local retry_performed=false
  local -a cleanup_attempted=()
  local -a cleanup_removed=()

  local status=0
  if run_command_capture "$output_file" "$@"; then
    if [[ "$VALIDATE_JSON" == "true" ]]; then
      printf '{"command":"%s","status":"passed","code":"ok","argv":[' "$(json_escape "$command_name")"
      local first=1
      for arg in "$@"; do
        if (( first )); then first=0; else printf ','; fi
        printf '"%s"' "$(json_escape "$arg")"
      done
      printf '],"retried":false,"cleanup":{"attempted":[],"removed":[]},"message":"validation passed"}\n'
    fi
    rm -f "$output_file"
    return 0
  else
    status=$?
  fi
  if detect_socket_environment_block "$output_file"; then
    emit_bridge_environment_blocked "$command_name" "$output_file" false "$@"
    rm -f "$output_file"
    return 125
  fi
  if detect_locked_bridge_ref_output "$output_file"; then
    retry_performed=true
    cleanup_bridge_ref_outputs cleanup_attempted cleanup_removed
    if run_command_capture "$output_file" "$@"; then
      if [[ "$VALIDATE_JSON" == "true" ]]; then
        printf '{"command":"%s","status":"passed","code":"ok","argv":[' "$(json_escape "$command_name")"
        local first=1
        for arg in "$@"; do
          if (( first )); then first=0; else printf ','; fi
          printf '"%s"' "$(json_escape "$arg")"
        done
        printf '],"retried":true,"cleanup":{"attempted":['
        local idx
        for idx in "${!cleanup_attempted[@]}"; do
          (( idx == 0 )) || printf ','
          printf '"%s"' "$(json_escape "${cleanup_attempted[$idx]}")"
        done
        printf '],"removed":['
        for idx in "${!cleanup_removed[@]}"; do
          (( idx == 0 )) || printf ','
          printf '"%s"' "$(json_escape "${cleanup_removed[$idx]}")"
        done
        printf ']},"message":"validation passed after locked-ref cleanup retry"}\n'
      fi
      rm -f "$output_file"
      return 0
    else
      status=$?
    fi
    if detect_socket_environment_block "$output_file"; then
      emit_bridge_environment_blocked "$command_name" "$output_file" true "$@"
      rm -f "$output_file"
      return 125
    fi
  fi

  if [[ "$VALIDATE_JSON" == "true" ]]; then
    printf '{"command":"%s","status":"failed","code":"validation_failed","argv":[' "$(json_escape "$command_name")"
    local first=1
    for arg in "$@"; do
      if (( first )); then first=0; else printf ','; fi
      printf '"%s"' "$(json_escape "$arg")"
    done
    printf '],"retried":%s,"cleanup":{"attempted":[' "$retry_performed"
    local idx
    for idx in "${!cleanup_attempted[@]}"; do
      (( idx == 0 )) || printf ','
      printf '"%s"' "$(json_escape "${cleanup_attempted[$idx]}")"
    done
    printf '],"removed":['
    for idx in "${!cleanup_removed[@]}"; do
      (( idx == 0 )) || printf ','
      printf '"%s"' "$(json_escape "${cleanup_removed[$idx]}")"
    done
    printf ']},"diagnostics":"%s","message":"validation command failed with exit code %s"}\n' \
      "$(json_escape "$(tail -n 80 "$output_file")")" \
      "$status"
  else
    cat "$output_file" >&2
    printf 'validation command failed with exit code %s\n' "$status" >&2
  fi
  rm -f "$output_file"
  return "$status"
}

run_npm_direct_diagnostics() {
  local output_file="$1"
  set +e
  (
    unset SPIRECTL_BRIDGE_SOCKET_PATH SPIRECTL_BRIDGE_PIPE_NAME SPIRECTL_BRIDGE_TCP_ADDRESS
    unset STS2_AGENT_WORKSPACE STS2_HOST_CONTROL_SOCKET STS2_AGENT_IPC_DIR
    cd "$repo_root/npm-wrapper"
    node --test --test-isolation=none test/*.test.js
  ) >"$output_file" 2>&1
  local status=$?
  set -e
  return "$status"
}

emit_npm_environment_blocked() {
  local aggregate_output="$1"
  local direct_output="$2"
  shift 2
  local message="npm-wrapper validation could not run because this environment blocks child-process spawning or local service binding"
  local guidance="rerun on a host that permits npm child processes and local loopback service sockets, or use this blocked result as infrastructure evidence instead of a wrapper regression"
  if [[ "$VALIDATE_JSON" == "true" ]]; then
    printf '{"command":"npm-wrapper-tests","status":"blocked","code":"environment_blocked","argv":['
    local first=1
    for arg in "$@"; do
      if (( first )); then first=0; else printf ','; fi
      printf '"%s"' "$(json_escape "$arg")"
    done
    printf '],"diagnosticArgv":["node","--test","--test-isolation=none","test/*.test.js"],"message":"%s","guidance":"%s","blockerDiagnostics":"%s","aggregateDiagnostics":"%s","directDiagnostics":"%s"}\n' \
      "$(json_escape "$message")" \
      "$(json_escape "$guidance")" \
      "$(json_escape "$(npm_environment_block_diagnostics "$aggregate_output" "$direct_output")")" \
      "$(json_escape "$(tail -n 20 "$aggregate_output")")" \
      "$(json_escape "$(tail -n 40 "$direct_output")")"
  else
    cat "$aggregate_output" >&2
    cat "$direct_output" >&2
    printf 'environment blocked: %s\n%s\n' "$message" "$guidance" >&2
  fi
}

run_npm_wrapper_tests() {
  local command_name="$1"
  shift
  if (( $# != 0 )); then
    fail "$command_name" "invalid_argument" "npm-wrapper-tests does not accept arguments: $*"
  fi

  local aggregate_output
  local direct_output
  aggregate_output="$(mktemp "${TMPDIR:-/tmp}/spirectl-validate-npm.XXXXXX")"
  direct_output="$(mktemp "${TMPDIR:-/tmp}/spirectl-validate-npm-direct.XXXXXX")"
  local -a argv=(npm --prefix "$repo_root/npm-wrapper" test)
  local status=0
  if run_command_capture "$aggregate_output" "${argv[@]}"; then
    if [[ "$VALIDATE_JSON" == "true" ]]; then
      emit_json_success "$command_name" "validation passed" "${argv[@]}"
    fi
    rm -f "$aggregate_output" "$direct_output"
    return 0
  else
    status=$?
  fi

  local direct_status=0
  if run_npm_direct_diagnostics "$direct_output"; then
    direct_status=0
  else
    direct_status=$?
  fi

  if detect_npm_environment_block "$aggregate_output" || detect_npm_environment_block "$direct_output"; then
    emit_npm_environment_blocked "$aggregate_output" "$direct_output" "${argv[@]}"
    rm -f "$aggregate_output" "$direct_output"
    return 125
  fi

  if [[ "$VALIDATE_JSON" == "true" ]]; then
    printf '{"command":"%s","status":"failed","code":"validation_failed","argv":[' "$(json_escape "$command_name")"
    local first=1
    local arg
    for arg in "${argv[@]}"; do
      if (( first )); then first=0; else printf ','; fi
      printf '"%s"' "$(json_escape "$arg")"
    done
    printf '],"diagnosticArgv":["node","--test","--test-isolation=none","test/*.test.js"],"exitCode":%s,"directExitCode":%s,"message":"npm-wrapper validation failed with exit code %s","aggregateDiagnostics":"%s","directDiagnostics":"%s"}\n' \
      "$status" \
      "$direct_status" \
      "$status" \
      "$(json_escape "$(tail -n 20 "$aggregate_output")")" \
      "$(json_escape "$(tail -n 40 "$direct_output")")"
  else
    cat "$aggregate_output" >&2
    cat "$direct_output" >&2
    printf 'npm-wrapper validation failed with exit code %s\n' "$status" >&2
  fi
  rm -f "$aggregate_output" "$direct_output"
  return "$status"
}

run_cli_tests() {
  local command_name="$1"
  shift
  if (( $# != 0 )); then
    fail "$command_name" "invalid_argument" "cli-tests does not accept arguments: $*"
  fi

  local output_file
  output_file="$(mktemp "${TMPDIR:-/tmp}/spirectl-validate-cli.XXXXXX")"
  local -a steps=(
    "cargo fmt --check"
    "cargo test --color never -p sts2 --test assets_commands"
    "cargo test --color never -p sts2 --test dev_workflows"
    "cargo test --color never -p sts2 --test cli_snapshots"
    "cargo test --color never -p sts2 --test game_lifecycle"
  )

  local step
  local status=0
  for step in "${steps[@]}"; do
    if run_command_capture "$output_file" bash -lc "$step"; then
      status=0
    else
      status=$?
    fi
    if (( status != 0 )); then
      if [[ "$VALIDATE_JSON" == "true" ]]; then
        printf '{"command":"%s","status":"failed","code":"validation_failed","failedStep":"%s","exitCode":%s,"steps":[' \
          "$(json_escape "$command_name")" \
          "$(json_escape "$step")" \
          "$status"
        local i
        for i in "${!steps[@]}"; do
          (( i == 0 )) || printf ','
          printf '"%s"' "$(json_escape "${steps[$i]}")"
        done
        printf '],"diagnostics":"%s","message":"CLI validation failed"}\n' \
          "$(json_escape "$(tail -n 80 "$output_file")")"
      else
        cat "$output_file" >&2
        printf 'CLI validation failed in step: %s\n' "$step" >&2
      fi
      rm -f "$output_file"
      return "$status"
    fi
  done

  if [[ "$VALIDATE_JSON" == "true" ]]; then
    printf '{"command":"%s","status":"passed","code":"ok","steps":[' "$(json_escape "$command_name")"
    local i
    for i in "${!steps[@]}"; do
      (( i == 0 )) || printf ','
      printf '"%s"' "$(json_escape "${steps[$i]}")"
    done
    printf '],"message":"validation passed"}\n'
  fi
  rm -f "$output_file"
}

detect_cargo_package_workspace_input_gap() {
  local output_file="$1"
  rg -qi '(read bridge version props|bridge-mod/Directory\.Build\.props|\.\./bridge-mod/Directory\.Build\.props|cargo:rerun-if-changed=\.\./proto|could not find .*\.\./proto)' "$output_file"
}

run_cargo_package_sts2() {
  local command_name="$1"
  shift
  if (( $# != 0 )); then
    fail "$command_name" "invalid_argument" "cargo-package-sts2 does not accept arguments: $*"
  fi

  local full_output
  local smoke_output
  full_output="$(mktemp "${TMPDIR:-/tmp}/spirectl-cargo-package-full.XXXXXX")"
  smoke_output="$(mktemp "${TMPDIR:-/tmp}/spirectl-cargo-package-smoke.XXXXXX")"
  local -a full_argv=(cargo package -p sts2 --allow-dirty)
  local -a smoke_argv=(cargo package -p sts2 --allow-dirty --no-verify)

  local full_status=0
  if run_command_capture "$full_output" "${full_argv[@]}"; then
    if [[ "$VALIDATE_JSON" == "true" ]]; then
      printf '{"command":"%s","status":"passed","code":"ok","argv":[' "$(json_escape "$command_name")"
      local i
      for i in "${!full_argv[@]}"; do
        (( i == 0 )) || printf ','
        printf '"%s"' "$(json_escape "${full_argv[$i]}")"
      done
      printf '],"message":"crate package verification passed"}\n'
    fi
    rm -f "$full_output" "$smoke_output"
    return 0
  else
    full_status=$?
  fi

  if ! detect_cargo_package_workspace_input_gap "$full_output"; then
    if [[ "$VALIDATE_JSON" == "true" ]]; then
      printf '{"command":"%s","status":"failed","code":"cargo_package_failed","argv":[' "$(json_escape "$command_name")"
      local i
      for i in "${!full_argv[@]}"; do
        (( i == 0 )) || printf ','
        printf '"%s"' "$(json_escape "${full_argv[$i]}")"
      done
      printf '],"exitCode":%s,"message":"cargo package verification failed","diagnostics":"%s"}\n' \
        "$full_status" \
        "$(json_escape "$(tail -n 30 "$full_output")")"
    else
      cat "$full_output" >&2
      printf 'cargo package verification failed with exit code %s\n' "$full_status" >&2
    fi
    rm -f "$full_output" "$smoke_output"
    return "$full_status"
  fi

  local smoke_status=0
  if run_command_capture "$smoke_output" "${smoke_argv[@]}"; then
    if [[ "$VALIDATE_JSON" == "true" ]]; then
      printf '{"command":"%s","status":"unsupported","code":"package_verification_unsupported","fullVerification":{"argv":[' "$(json_escape "$command_name")"
      local i
      for i in "${!full_argv[@]}"; do
        (( i == 0 )) || printf ','
        printf '"%s"' "$(json_escape "${full_argv[$i]}")"
      done
      printf '],"exitCode":%s,"diagnostics":"%s"},"acceptedSmoke":{"argv":[' \
        "$full_status" \
        "$(json_escape "$(tail -n 30 "$full_output")")"
      for i in "${!smoke_argv[@]}"; do
        (( i == 0 )) || printf ','
        printf '"%s"' "$(json_escape "${smoke_argv[$i]}")"
      done
      printf '],"exitCode":0},"message":"full cargo package verification is unsupported until workspace-relative proto/version inputs are staged into the crate package"}\n'
    else
      cat "$full_output" >&2
      printf 'package verification unsupported; accepted smoke command passed: %s\n' "${smoke_argv[*]}" >&2
    fi
    rm -f "$full_output" "$smoke_output"
    return 125
  else
    smoke_status=$?
  fi

  if [[ "$VALIDATE_JSON" == "true" ]]; then
    printf '{"command":"%s","status":"failed","code":"cargo_package_smoke_failed","fullVerification":{"argv":[' "$(json_escape "$command_name")"
    local i
    for i in "${!full_argv[@]}"; do
      (( i == 0 )) || printf ','
      printf '"%s"' "$(json_escape "${full_argv[$i]}")"
    done
    printf '],"exitCode":%s,"diagnostics":"%s"},"acceptedSmoke":{"argv":[' \
      "$full_status" \
      "$(json_escape "$(tail -n 30 "$full_output")")"
    for i in "${!smoke_argv[@]}"; do
      (( i == 0 )) || printf ','
      printf '"%s"' "$(json_escape "${smoke_argv[$i]}")"
    done
    printf '],"exitCode":%s,"diagnostics":"%s"},"message":"cargo package --no-verify smoke failed after full verification hit the known workspace input gap"}\n' \
      "$smoke_status" \
      "$(json_escape "$(tail -n 30 "$smoke_output")")"
  else
    cat "$full_output" >&2
    cat "$smoke_output" >&2
    printf 'cargo package --no-verify smoke failed with exit code %s\n' "$smoke_status" >&2
  fi
  rm -f "$full_output" "$smoke_output"
  return "$smoke_status"
}

run_m78_live_encounter_artifacts() {
  local command_name="$1"
  local encounter_id="$2"
  local print_only="$3"
  local repair_stale_endpoint="$4"
  local launch="$5"
  local attach="$6"

  local -a preflight_argv=()
  local -a extract_batch_argv=()
  local query1="encounter:${encounter_id}:background:image"
  local query2="encounter:${encounter_id}:visual-state:rocket-charge-up:overlay:image"
  local query3="encounter:${encounter_id}:visual-part:rocket:state:rocket-charge-up:image"
  local manifest_path=".sts2/artifacts/encounters/${encounter_id}/manifest.json"
  local output_dir=".sts2/artifacts/encounters/${encounter_id}"

  if [[ -n "${STS2_BIN:-}" ]]; then
    preflight_argv=("${STS2_BIN}" --json dev visual-preflight --encounter "$encounter_id")
    extract_batch_argv=("${STS2_BIN}" --json assets extract-batch --manifest "$manifest_path" --output "$output_dir" --execution live --format png)
  else
    preflight_argv=(cargo run -p sts2 -- --json dev visual-preflight --encounter "$encounter_id")
    extract_batch_argv=(cargo run -p sts2 -- --json assets extract-batch --manifest "$manifest_path" --output "$output_dir" --execution live --format png)
  fi
  [[ "$print_only" == "true" ]] && preflight_argv+=(--print-only)
  [[ "$repair_stale_endpoint" == "true" ]] && preflight_argv+=(--repair-stale-endpoint)
  [[ "$launch" == "true" ]] && preflight_argv+=(--launch)
  [[ "$attach" == "true" ]] && preflight_argv+=(--attach)

  local out1="${output_dir}/${encounter_id}-background/virtual/encounter/${encounter_id}/background.png"
  local out2="${output_dir}/${encounter_id}-rocket-overlay/virtual/encounter/${encounter_id}/visual-state/rocket-charge-up/overlay.png"
  local out3="${output_dir}/${encounter_id}-rocket-part/virtual/encounter/${encounter_id}/visual-part/rocket/state/rocket-charge-up.png"
  local next1="scripts/validate.sh m78-live-encounter-artifacts --encounter ${encounter_id} --json --repair-stale-endpoint --attach"
  local next2="scripts/validate.sh m78-live-encounter-artifacts --encounter ${encounter_id} --json --launch"

  local preflight_output
  preflight_output="$(mktemp "${TMPDIR:-/tmp}/spirectl-m78-preflight.XXXXXX")"
  if run_command_capture "$preflight_output" "${preflight_argv[@]}"; then
    :
  else
    local preflight_status=$?
    if [[ "$VALIDATE_JSON" == "true" ]]; then
      emit_m78_preflight_failure_json "$command_name" "$encounter_id" "$preflight_output" "$preflight_status" "$next1" "$next2" "${preflight_argv[@]}"
    else
      cat "$preflight_output" >&2
      printf 'live host unavailable for encounter artifacts. Try: %s\n' "$next1" >&2
      printf 'or: %s\n' "$next2" >&2
    fi
    rm -f "$preflight_output"
    return 0
  fi
  rm -f "$preflight_output"

  mkdir -p "$(dirname "$manifest_path")"
  printf '{"version":0,"assets":[{"id":"%s-background","query":"%s","execution":"live","format":"png","metadata":{"encounter":"%s","renderTarget":"background","artifactChecks":["nonblank","framing"]}},{"id":"%s-rocket-overlay","query":"%s","execution":"live","format":"png","metadata":{"encounter":"%s","renderTarget":"overlay","artifactChecks":["nonblank","transparent-background","isolation"]}},{"id":"%s-rocket-part","query":"%s","execution":"live","format":"png","metadata":{"encounter":"%s","renderTarget":"part","artifactChecks":["nonblank","transparent-background","bounds","isolation"]}}]}\n' \
    "$(json_escape "$encounter_id")" "$(json_escape "$query1")" "$(json_escape "$encounter_id")" \
    "$(json_escape "$encounter_id")" "$(json_escape "$query2")" "$(json_escape "$encounter_id")" \
    "$(json_escape "$encounter_id")" "$(json_escape "$query3")" "$(json_escape "$encounter_id")" \
    > "$manifest_path"

  local -a generated_outputs=("$out1" "$out2" "$out3")
  local -a check_statuses=()
  local extract_output
  extract_output="$(mktemp "${TMPDIR:-/tmp}/spirectl-m78-extract-batch.XXXXXX")"
  if run_command_capture "$extract_output" "${extract_batch_argv[@]}"; then
    :
  else
    local extract_status=$?
    local structured_isolation_failures=""
    if structured_isolation_failures="$(m78_structured_isolation_failure_json "$extract_output" 2>/dev/null)"; then
      if [[ "$VALIDATE_JSON" == "true" ]]; then
        printf '{"command":"%s","status":"passed","code":"structured_isolation_failure","encounter":"%s","manifest":"%s","batchCommand":[' \
          "$(json_escape "$command_name")" \
          "$(json_escape "$encounter_id")" \
          "$(json_escape "$manifest_path")"
        local isolated_failure_argv_index=0
        for isolated_failure_argv_index in "${!extract_batch_argv[@]}"; do
          (( isolated_failure_argv_index == 0 )) || printf ','
          printf '"%s"' "$(json_escape "${extract_batch_argv[$isolated_failure_argv_index]}")"
        done
        printf '],"exitCode":%s,"generatedOutputs":["%s","%s","%s"],"isolationFailureSummaries":%s' \
          "$extract_status" \
          "$(json_escape "$out1")" \
          "$(json_escape "$out2")" \
          "$(json_escape "$out3")" \
          "$structured_isolation_failures"
        emit_json_file_or_text_field "diagnostics" "diagnosticsText" "$extract_output"
        printf ',"message":"encounter overlay/part artifacts failed with structured isolation diagnostics; no isolated artifact was emitted"}\n'
      else
        cat "$extract_output" >&2
        printf 'encounter overlay/part artifacts failed with structured isolation diagnostics; no isolated artifact was emitted\n' >&2
      fi
      rm -f "$extract_output"
      return 0
    fi

    if [[ "$VALIDATE_JSON" == "true" ]]; then
      printf '{"command":"%s","status":"failed","code":"validation_failed","encounter":"%s","manifest":"%s","batchCommand":[' \
        "$(json_escape "$command_name")" \
        "$(json_escape "$encounter_id")" \
        "$(json_escape "$manifest_path")"
      local failed_argv_index=0
      for failed_argv_index in "${!extract_batch_argv[@]}"; do
        (( failed_argv_index == 0 )) || printf ','
        printf '"%s"' "$(json_escape "${extract_batch_argv[$failed_argv_index]}")"
      done
      printf '],"exitCode":%s,"generatedOutputs":["%s","%s","%s"]' \
        "$extract_status" \
        "$(json_escape "$out1")" \
        "$(json_escape "$out2")" \
        "$(json_escape "$out3")"
      emit_json_file_or_text_field "diagnostics" "diagnosticsText" "$extract_output"
      printf ',"message":"encounter batch artifact extraction failed with exit code %s"}\n' "$extract_status"
    else
      cat "$extract_output" >&2
      printf 'encounter batch artifact extraction failed with exit code %s\n' "$extract_status" >&2
    fi
    rm -f "$extract_output"
    return "$extract_status"
  fi

  local output_index=0
  for output_index in "${!generated_outputs[@]}"; do
    if [[ -s "${generated_outputs[$output_index]}" ]]; then
      check_statuses+=("present")
    else
      check_statuses+=("missing")
    fi
  done

  if rg -q '"artifactChecks"[[:space:]]*:[^]]*"status"[[:space:]]*:[[:space:]]*"failed"' "$extract_output"; then
    if [[ "$VALIDATE_JSON" == "true" ]]; then
      printf '{"command":"%s","status":"failed","code":"artifact_check_failed","encounter":"%s","manifest":"%s","generatedOutputs":[' \
        "$(json_escape "$command_name")" \
        "$(json_escape "$encounter_id")" \
        "$(json_escape "$manifest_path")"
      local failed_output_index=0
      for failed_output_index in "${!generated_outputs[@]}"; do
        (( failed_output_index == 0 )) || printf ','
        printf '"%s"' "$(json_escape "${generated_outputs[$failed_output_index]}")"
      done
      printf ']'
      emit_json_file_or_text_field "diagnostics" "diagnosticsText" "$extract_output"
      printf '}\n'
    else
      cat "$extract_output" >&2
      printf 'encounter artifact checks failed\n' >&2
    fi
    rm -f "$extract_output"
    return 1
  fi

  if [[ "$VALIDATE_JSON" == "true" ]]; then
    printf '{"command":"%s","status":"passed","code":"ok","encounter":"%s","manifest":"%s","batchCommand":[' \
      "$(json_escape "$command_name")" \
      "$(json_escape "$encounter_id")" \
      "$(json_escape "$manifest_path")"
    local argv_index=0
    for argv_index in "${!extract_batch_argv[@]}"; do
      (( argv_index == 0 )) || printf ','
      printf '"%s"' "$(json_escape "${extract_batch_argv[$argv_index]}")"
    done
    printf '],"generatedOutputs":["%s","%s","%s"]' \
      "$(json_escape "$out1")" \
      "$(json_escape "$out2")" \
      "$(json_escape "$out3")"
    if command -v python3 >/dev/null 2>&1; then
      local summaries_json
      summaries_json="$(python3 - "$extract_output" <<'PY'
import json
import sys

with open(sys.argv[1], "r", encoding="utf-8", errors="replace") as handle:
    text = handle.read()

payload = None
for line in reversed(text.splitlines()):
    line = line.strip()
    if not line.startswith("{"):
        continue
    try:
        payload = json.loads(line)
        break
    except json.JSONDecodeError:
        continue
if payload is None:
    print("[]")
    raise SystemExit(0)

summaries = []
for result in payload.get("results", []):
    result_id = result.get("id")
    for export in result.get("exports", []):
        checks = export.get("artifactChecks")
        if not isinstance(checks, list):
            continue
        statuses = [check.get("status") for check in checks if isinstance(check, dict)]
        summaries.append({
            "id": result_id,
            "path": export.get("path") or export.get("outputPath"),
            "status": "failed" if "failed" in statuses else "passed",
            "artifactChecks": checks,
        })

print(json.dumps(summaries, separators=(",", ":")))
PY
)"
      printf ',"artifactCheckSummaries":%s' "$summaries_json"
    fi
    printf ',"diagnostics":'
    cat "$extract_output"
    printf ',"nextCommands":[]}\n'
  fi
  rm -f "$extract_output"
}

classify_dotnet_format_includes() {
  local command_name="$1"
  local paths_ref="$2"
  local types_ref="$3"
  shift 3
  local -n _include_paths="$paths_ref"
  local -n _include_types="$types_ref"
  _include_paths=()
  _include_types=()

  local include
  for include in "$@"; do
    if [[ -f "$repo_root/$include" ]]; then
      _include_paths+=("$include")
      _include_types+=("file")
    elif [[ -d "$repo_root/$include" ]]; then
      _include_paths+=("$include")
      _include_types+=("directory")
    else
      fail "$command_name" "invalid_include_path" "dotnet-format include path must exist as a file or directory under the repository: $include" "$include"
    fi
  done
}

run_dotnet_format_style_selected() {
  local command_name="$1"
  shift
  local -a include_paths=()
  local -a include_types=()
  classify_dotnet_format_includes "$command_name" include_paths include_types "$@"

  local -a whitespace_argv=(dotnet format spirectl.sln whitespace --verify-no-changes)
  local include
  for include in "${include_paths[@]}"; do
    whitespace_argv+=(--include "$include")
  done
  set +e
  "${whitespace_argv[@]}"
  local whitespace_status=$?
  set -e
  if (( whitespace_status != 0 )); then
    if [[ "$VALIDATE_JSON" == "true" ]]; then
      emit_json_failure "$command_name" "selected_whitespace_validation_failed" "selected whitespace validation failed with exit code $whitespace_status" "${whitespace_argv[@]}"
    else
      printf 'selected whitespace validation failed with exit code %s\n' "$whitespace_status" >&2
    fi
    return "$whitespace_status"
  fi

  local style_repo
  style_repo="$(mktemp -d "${TMPDIR:-/tmp}/spirectl-dotnet-format-style-repo.XXXXXX")"
  git -C "$repo_root" archive HEAD | tar -x -C "$style_repo"
  local include_index
  for include_index in "${!include_paths[@]}"; do
    include="${include_paths[$include_index]}"
    mkdir -p "$style_repo/$(dirname "$include")"
    rm -rf "$style_repo/$include"
    cp -a "$repo_root/$include" "$style_repo/$include"
  done

  local report_dir
  report_dir="$(mktemp -d "${TMPDIR:-/tmp}/spirectl-dotnet-format-style.XXXXXX")"

  local -a argv=(dotnet format spirectl.sln style --verify-no-changes --severity info --report "$report_dir")
  for include in "${include_paths[@]}"; do
    argv+=(--include "$include")
  done

  set +e
  (cd "$style_repo" && "${argv[@]}")
  local status=$?
  set -e

  local report_file
  report_file="$(find_format_report_file "$report_dir")"
  local selected_changes=()
  local include_type
  for include_index in "${!include_paths[@]}"; do
    include="${include_paths[$include_index]}"
    include_type="${include_types[$include_index]}"
    if selected_include_changed "$include" "$include_type" "$style_repo"; then
      selected_changes+=("$include")
      continue
    fi
    if [[ "$report_file" != "" ]]; then
      local absolute="$style_repo/$include"
      if format_report_has_selected_changes "$report_file" "$include" "$absolute" "$include_type"; then
        selected_changes+=("$include")
      fi
    fi
  done

  if (( ${#selected_changes[@]} > 0 )); then
    rm -rf "$style_repo"
    rm -rf "$report_dir"
    if [[ "$VALIDATE_JSON" == "true" ]]; then
      emit_json_failure "$command_name" "selected_style_formatting_changed" "selected style formatting would change ${selected_changes[*]}" "${argv[@]}"
    else
      printf 'selected style formatting would change: %s\n' "${selected_changes[*]}" >&2
    fi
    return 1
  fi

  if (( status != 0 )); then
    rm -rf "$style_repo"
    rm -rf "$report_dir"
    if [[ "$report_file" == "" ]]; then
      if [[ "$VALIDATE_JSON" == "true" ]]; then
        emit_json_failure "$command_name" "selected_style_validation_failed" "selected style validation failed without a dotnet format report" "${argv[@]}"
      else
        printf 'selected style validation failed without a dotnet format report\n' >&2
      fi
      return "$status"
    fi
    if [[ "$VALIDATE_JSON" == "true" ]]; then
      emit_json_success "$command_name" "style validation passed for selected paths; unrelated diagnostics were ignored" "${argv[@]}"
    fi
    return 0
  fi

  if [[ "$VALIDATE_JSON" == "true" ]]; then
    emit_json_success "$command_name" "validation passed" "${argv[@]}"
  fi
  rm -rf "$style_repo"
  rm -rf "$report_dir"
}

selected_include_changed() {
  local include="$1"
  local include_type="$2"
  local style_repo="$3"

  if [[ ! -e "$repo_root/$include" || ! -e "$style_repo/$include" ]]; then
    [[ -e "$repo_root/$include" || -e "$style_repo/$include" ]]
    return $?
  fi

  if [[ "$include_type" == "directory" ]]; then
    ! diff -qr -x bin -x obj "$repo_root/$include" "$style_repo/$include" >/dev/null
    return $?
  fi

  ! cmp -s "$repo_root/$include" "$style_repo/$include"
}

format_report_has_selected_changes() {
  local report_file="$1"
  local rel="$2"
  local abs="$3"
  local include_type="${4:-file}"
  if command -v jq >/dev/null 2>&1; then
    jq -e --arg rel "$rel" --arg abs "$abs" --arg include_type "$include_type" '
      def path_value: .FilePath // .filePath // .DocumentId // .documentId;
      def diagnostics_value: .Diagnostics // .diagnostics // [];
      def changes_value: .FileChanges // .fileChanges // diagnostics_value;
      def diagnostic_id: .DiagnosticId // .diagnosticId // .Id // .id // "";
      def selected_path($path):
        if $include_type == "directory" then
          ($path == $rel or $path == $abs or ($path | startswith($rel + "/")) or ($path | startswith($abs + "/")))
        else
          ($path == $rel or $path == $abs)
        end;
      def actionable_diagnostic:
        diagnostic_id as $id
        | ($id != "IDE0130" and ($id == "" or ($id | startswith("IDE")) or ($id | startswith("SA")) or ($id | startswith("dotnet_"))));
      def style_change:
        ((changes_value | map(select(actionable_diagnostic)) | length > 0)
        or (diagnostics_value | map(select(actionable_diagnostic)) | length > 0));
      [
        .. | objects
        | select((path_value as $path | ($path | type) == "string" and selected_path($path)) and ((changes_value | length) > 0) and style_change)
      ] | length > 0
    ' "$report_file" >/dev/null 2>&1
    return $?
  fi

  awk -v rel="$rel" -v abs="$abs" -v include_type="$include_type" '
    function selected_path(path) {
      if (path == rel || path == abs) {
        return 1
      }
      return include_type == "directory" && (index(path, rel "/") == 1 || index(path, abs "/") == 1)
    }
    function diagnostic_is_style(line) {
      return line ~ /"DiagnosticId"[[:space:]]*:[[:space:]]*"(IDE|SA|dotnet_|")/ || line ~ /"diagnosticId"[[:space:]]*:[[:space:]]*"(IDE|SA|dotnet_|")/ || line ~ /"Id"[[:space:]]*:[[:space:]]*"(IDE|SA|dotnet_|")/ || line ~ /"id"[[:space:]]*:[[:space:]]*"(IDE|SA|dotnet_|")/
    }
    BEGIN { selected = 0; array_depth = 0; hit = 0 }
    /"([Ff]ile[Pp]ath|[Dd]ocument[Ii]d)"[[:space:]]*:[[:space:]]*"/ {
      line = $0
      sub(/^[^"]*"([Ff]ile[Pp]ath|[Dd]ocument[Ii]d)"[[:space:]]*:[[:space:]]*"/, "", line)
      sub(/".*/, "", line)
      selected = selected_path(line)
      array_depth = 0
      next
    }
    selected && /"([Ff]ile[Cc]hanges|[Dd]iagnostics)"[[:space:]]*:[[:space:]]*\[/ {
      line = $0
      sub(/^.*"([Ff]ile[Cc]hanges|[Dd]iagnostics)"[[:space:]]*:[[:space:]]*\[/, "", line)
      if (line !~ /^[[:space:]]*\]/) {
        hit = 1
      }
      array_depth = 1
      next
    }
    selected && array_depth && diagnostic_is_style($0) {
      hit = 1
    }
    END { exit hit ? 0 : 1 }
  ' "$report_file"
}

find_format_report_file() {
  local report_dir="$1"
  if [[ -f "$report_dir/format-report.json" ]]; then
    printf '%s' "$report_dir/format-report.json"
    return 0
  fi
  find "$report_dir" -type f -name '*.json' -print -quit 2>/dev/null || true
}

# Emit the producer-walk perf report: the live scene watcher's OWN self-profiler counters
# (SPIRECTL_SCENE_WATCH_PROFILE=1) folded into the shared cross-repo `perf-report/1` envelope, so an A/B lever
# flip can be DIFFED instead of eyeballed on a phone.
#
# REPORT ONLY. It never applies a wall-clock threshold and never fails because a number moved; a non-zero
# exit means a bad invocation or a broken build. With no `--log`/`--snapshot` input it
# still emits a schema-valid all-zero envelope whose `params.source` is `unavailable` — never fabricated numbers.
run_producer_walk_profile() {
  local command_name="$1"
  local report_path="$2"
  shift 2

  local project="bridge-mod/tools/Spirectl.BridgeMod.PerfReport/Spirectl.BridgeMod.PerfReport.csproj"
  local -a argv=(dotnet run --project "$project" -v q -- --out "$report_path" "$@")
  local output_file
  output_file="$(mktemp "${TMPDIR:-/tmp}/spirectl-producer-walk-profile.XXXXXX")"
  local status=0
  "${argv[@]}" >"$output_file" 2>&1 || status=$?

  if (( status != 0 )); then
    if [[ "$VALIDATE_JSON" == "true" ]]; then
      printf '{"command":"%s","status":"failed","code":"producer_walk_profile_failed","reportPath":null,"argv":[' "$(json_escape "$command_name")"
      local first=1
      for arg in "${argv[@]}"; do
        if (( first )); then first=0; else printf ','; fi
        printf '"%s"' "$(json_escape "$arg")"
      done
      printf '],"output":"%s","message":"producer-walk profile report command failed with exit code %s"}\n' \
        "$(json_escape "$(cat "$output_file")")" "$status"
    else
      cat "$output_file" >&2
      printf 'producer-walk profile report command failed with exit code %s\n' "$status" >&2
    fi
    rm -f "$output_file"
    exit "$status"
  fi

  local windows="null"
  local report_source="null"
  if command -v python3 >/dev/null 2>&1; then
    local summary
    if summary="$(python3 - "$output_file" <<'PY'
import json
import sys

with open(sys.argv[1], "r", encoding="utf-8", errors="replace") as handle:
    text = handle.read()
report = None
for line in reversed(text.splitlines()):
    line = line.strip()
    if not line.startswith("{"):
        continue
    try:
        report = json.loads(line)
        break
    except json.JSONDecodeError:
        continue
if not isinstance(report, dict):
    raise SystemExit(1)
params = report.get("params") or {}
print(json.dumps(params.get("windows")))
print(json.dumps(params.get("source")))
PY
    2>/dev/null)"; then
      windows="$(printf '%s' "$summary" | sed -n '1p')"
      report_source="$(printf '%s' "$summary" | sed -n '2p')"
    fi
  fi

  if [[ "$VALIDATE_JSON" == "true" ]]; then
    printf '{"command":"%s","status":"passed","code":"ok","reportPath":"%s","windows":%s,"source":%s,"argv":[' \
      "$(json_escape "$command_name")" "$(json_escape "$report_path")" "$windows" "$report_source"
    local first=1
    for arg in "${argv[@]}"; do
      if (( first )); then first=0; else printf ','; fi
      printf '"%s"' "$(json_escape "$arg")"
    done
    printf ']'
    emit_json_file_or_text_field "report" "output" "$output_file"
    printf ',"message":"producer-walk profile report emitted (report only; no thresholds)"}\n'
  else
    cat "$output_file"
    printf 'report written to %s\n' "$report_path"
  fi
  rm -f "$output_file"
}

strip_json_flag() {
  local -n _out=$1
  shift
  _out=()
  for arg in "$@"; do
    [[ "$arg" == "--json" ]] && continue
    _out+=("$arg")
  done
}

command="${1:-}"
if [[ -z "$command" || "$command" == "-h" || "$command" == "--help" ]]; then
  usage
  exit 0
fi

VALIDATE_JSON=false
if [[ "$command" == "--json" ]]; then
  VALIDATE_JSON=true
  command="default"
else
  shift
fi

for arg in "$@"; do
  if [[ "$arg" == "--json" ]]; then
    VALIDATE_JSON=true
  fi
done

case "$command" in
  default)
    run_cargo_test_filter "$command" "sts2" "cli_snapshots" "lobby"
    ;;
  dotnet-format)
    args=()
    strip_json_flag args "$@"
    if (( ${#args[@]} == 1 )) && [[ "${args[0]}" == "--help" || "${args[0]}" == "-h" ]]; then
      cat <<'EOF'
Usage: scripts/validate.sh dotnet-format --include <path> [<path>...] [--include <path>...] [--json]

Examples:
  scripts/validate.sh dotnet-format --include bridge-mod/src/Foo.cs --json
  scripts/validate.sh dotnet-format --include bridge-mod/src/Foo.cs bridge-mod/tests/FooTests.cs --json
EOF
      exit 0
    fi
    includes=()
    i=0
    while (( i < ${#args[@]} )); do
      case "${args[$i]}" in
        --include)
          (( i + 1 < ${#args[@]} )) || fail "$command" "missing_argument" "--include requires a path"
          i=$((i + 1))
          while (( i < ${#args[@]} )) && [[ "${args[$i]}" != --* ]]; do
            if ! normalized="$(normalize_repo_path "${args[$i]}")"; then
              fail "$command" "path_outside_repo" "included path is outside the repository"
            fi
            includes+=("$normalized")
            i=$((i + 1))
          done
          ;;
        *)
          fail "$command" "invalid_argument" "unknown dotnet-format argument: ${args[$i]}; use repeated --include values or pass multiple paths after one --include"
          ;;
      esac
    done
    (( ${#includes[@]} > 0 )) || fail "$command" "missing_include" "dotnet-format requires at least one --include path"
    run_dotnet_format_style_selected "$command" "${includes[@]}"
    ;;
  bridge-tests)
    args=()
    strip_json_flag args "$@"
    argv=(dotnet test bridge-mod/tests/Spirectl.BridgeMod.Tests/Spirectl.BridgeMod.Tests.csproj -m:1 -nodeReuse:false -p:UseSharedCompilation=false)
    passthrough=false
    i=0
    while (( i < ${#args[@]} )); do
      if [[ "${args[$i]}" == "--" ]]; then
        passthrough=true
        i=$((i + 1))
        continue
      fi
      if [[ "$passthrough" == "true" ]]; then
        argv+=("${args[$i]}")
      else
        case "${args[$i]}" in
          --filter|--configuration)
            (( i + 1 < ${#args[@]} )) || fail "$command" "missing_argument" "${args[$i]} requires a value"
            argv+=("${args[$i]}" "${args[$((i + 1))]}")
            i=$((i + 1))
            ;;
          *)
            fail "$command" "invalid_argument" "unknown bridge-tests argument: ${args[$i]}"
            ;;
        esac
      fi
      i=$((i + 1))
    done
    run_bridge_command_with_locked_retry "$command" "${argv[@]}"
    ;;
  bridge-build)
    args=()
    strip_json_flag args "$@"
    argv=(dotnet build bridge-mod/src/Spirectl.BridgeMod.Sts2Host/Spirectl.BridgeMod.Sts2Host.csproj -m:1 -nodeReuse:false -p:UseSharedCompilation=false)
    passthrough=false
    i=0
    while (( i < ${#args[@]} )); do
      if [[ "${args[$i]}" == "--" ]]; then
        passthrough=true
        i=$((i + 1))
        continue
      fi
      if [[ "$passthrough" == "true" ]]; then
        argv+=("${args[$i]}")
      else
        case "${args[$i]}" in
          --configuration)
            (( i + 1 < ${#args[@]} )) || fail "$command" "missing_argument" "--configuration requires a value"
            argv+=(--configuration "${args[$((i + 1))]}")
            i=$((i + 1))
            ;;
          *)
            fail "$command" "invalid_argument" "unknown bridge-build argument: ${args[$i]}"
            ;;
        esac
      fi
      i=$((i + 1))
    done
    run_bridge_command_with_locked_retry "$command" "${argv[@]}"
    ;;
  bridge-live-host-tests)
    args=()
    strip_json_flag args "$@"
    argv=(dotnet test bridge-mod/tests/Spirectl.BridgeMod.Tests/Spirectl.BridgeMod.Tests.csproj -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:RunSts2LiveHostTests=true)
    explicit_assemblies_dir=""
    passthrough=false
    i=0
    while (( i < ${#args[@]} )); do
      if [[ "${args[$i]}" == "--" ]]; then
        passthrough=true
        i=$((i + 1))
        continue
      fi
      if [[ "$passthrough" == "true" ]]; then
        argv+=("${args[$i]}")
      else
        case "${args[$i]}" in
          --filter|--configuration)
            (( i + 1 < ${#args[@]} )) || fail "$command" "missing_argument" "${args[$i]} requires a value"
            argv+=("${args[$i]}" "${args[$((i + 1))]}")
            i=$((i + 1))
            ;;
          --assemblies-dir)
            (( i + 1 < ${#args[@]} )) || fail "$command" "missing_argument" "--assemblies-dir requires a value"
            explicit_assemblies_dir="${args[$((i + 1))]}"
            i=$((i + 1))
            ;;
          *)
            fail "$command" "invalid_argument" "unknown bridge-live-host-tests argument: ${args[$i]}"
            ;;
        esac
      fi
      i=$((i + 1))
    done
    assemblies_dir=""
    if ! assemblies_dir="$(resolve_sts2_assemblies_dir "$explicit_assemblies_dir")"; then
      fail "$command" "missing_live_host_assemblies" "bridge-live-host-tests requires sts2.dll and GodotSharp.dll; provide --assemblies-dir, STS2_ASSEMBLIES_DIR, or set assemblies_dir in sts2.local.yaml/sts2.config.yaml"
    fi
    if [[ ! -f "$assemblies_dir/sts2.dll" || ! -f "$assemblies_dir/GodotSharp.dll" ]]; then
      fail "$command" "missing_live_host_assemblies" "bridge-live-host-tests requires sts2.dll and GodotSharp.dll in assemblies directory: $assemblies_dir"
    fi
    argv+=("-p:Sts2AssembliesDir=$assemblies_dir")
    run_bridge_command_with_locked_retry "$command" "${argv[@]}"
    ;;
  npm-wrapper-tests)
    args=()
    strip_json_flag args "$@"
    run_npm_wrapper_tests "$command" "${args[@]}"
    ;;
  cli-tests)
    args=()
    strip_json_flag args "$@"
    run_cli_tests "$command" "${args[@]}"
    ;;
  cargo-package-sts2)
    args=()
    strip_json_flag args "$@"
    run_cargo_package_sts2 "$command" "${args[@]}"
    ;;  cargo-test-filter)
    args=()
    strip_json_flag args "$@"
    package=""
    test_target=""
    filter=""
    i=0
    while (( i < ${#args[@]} )); do
      case "${args[$i]}" in
        --package)
          (( i + 1 < ${#args[@]} )) || fail "$command" "missing_argument" "--package requires a value"
          package="${args[$((i + 1))]}"
          i=$((i + 2))
          ;;
        --test)
          (( i + 1 < ${#args[@]} )) || fail "$command" "missing_argument" "--test requires a value"
          test_target="${args[$((i + 1))]}"
          i=$((i + 2))
          ;;
        --filter)
          (( i + 1 < ${#args[@]} )) || fail "$command" "missing_argument" "--filter requires a value"
          filter="${args[$((i + 1))]}"
          i=$((i + 2))
          ;;
        *)
          fail "$command" "invalid_argument" "unknown cargo-test-filter argument: ${args[$i]}"
          ;;
      esac
    done
    [[ -n "$package" ]] || fail "$command" "missing_argument" "cargo-test-filter requires --package"
    [[ -n "$test_target" ]] || fail "$command" "missing_argument" "cargo-test-filter requires --test"
    [[ -n "$filter" ]] || fail "$command" "missing_argument" "cargo-test-filter requires --filter"
    run_cargo_test_filter "$command" "$package" "$test_target" "$filter"
    ;;
  cargo-unit-test-filter)
    args=()
    strip_json_flag args "$@"
    package=""
    filter=""
    i=0
    while (( i < ${#args[@]} )); do
      case "${args[$i]}" in
        --package)
          (( i + 1 < ${#args[@]} )) || fail "$command" "missing_argument" "--package requires a value"
          package="${args[$((i + 1))]}"
          i=$((i + 2))
          ;;
        --filter)
          (( i + 1 < ${#args[@]} )) || fail "$command" "missing_argument" "--filter requires a value"
          filter="${args[$((i + 1))]}"
          i=$((i + 2))
          ;;
        *)
          fail "$command" "invalid_argument" "unknown cargo-unit-test-filter argument: ${args[$i]}"
          ;;
      esac
    done
    [[ -n "$package" ]] || fail "$command" "missing_argument" "cargo-unit-test-filter requires --package"
    [[ -n "$filter" ]] || fail "$command" "missing_argument" "cargo-unit-test-filter requires --filter"
    run_cargo_unit_test_filter "$command" "$package" "$filter"
    ;;
  rust-proto-selected)
    args=()
    strip_json_flag args "$@"
    selected=()
    keep_temp=false
    i=0
    while (( i < ${#args[@]} )); do
      case "${args[$i]}" in
        --path)
          (( i + 1 < ${#args[@]} )) || fail "$command" "missing_argument" "--path requires a value"
          if ! normalized="$(normalize_repo_path "${args[$((i + 1))]}")"; then
            fail "$command" "path_outside_repo" "selected path is outside the repository"
          fi
          selected+=("$normalized")
          i=$((i + 2))
          ;;
        --keep-temp)
          keep_temp=true
          i=$((i + 1))
          ;;
        *)
          fail "$command" "invalid_argument" "unknown rust-proto-selected argument: ${args[$i]}"
          ;;
      esac
    done
    (( ${#selected[@]} > 0 )) || fail "$command" "missing_path" "rust-proto-selected requires at least one --path"
    tmpdir="$(mktemp -d "${TMPDIR:-/tmp}/spirectl-rust-proto-selected.XXXXXX")"
    if [[ "$keep_temp" != "true" ]]; then
      trap 'rm -rf "$tmpdir"' EXIT
    fi
    git -C "$repo_root" archive HEAD | tar -x -C "$tmpdir"
    for path in "${selected[@]}"; do
      if [[ -e "$repo_root/$path" ]]; then
        mkdir -p "$tmpdir/$(dirname "$path")"
        cp -a "$repo_root/$path" "$tmpdir/$path"
      else
        rm -rf "$tmpdir/$path"
      fi
    done
    while IFS= read -r -d '' dirty_path; do
      if rust_proto_selected_overlay_candidate "$dirty_path"; then
        copy_or_remove_overlay_path "$tmpdir" "$dirty_path"
      fi
    done < <(git_status_dirty_paths_z)
    dirty=()
    while IFS= read -r -d '' dirty_path; do
      skip=false
      for selected_path in "${selected[@]}"; do
        if [[ "$dirty_path" == "$selected_path" || "$dirty_path" == "$selected_path/"* ]]; then
          skip=true
        fi
      done
      [[ "$skip" == "true" ]] || dirty+=("$dirty_path")
    done < <(git_status_dirty_paths_z)
    argv=(cargo test -p sts2 --no-run)
    if (cd "$tmpdir" && "${argv[@]}"); then
      if [[ "$VALIDATE_JSON" == "true" ]]; then
        printf '{"command":"%s","status":"passed","code":"ok","selectedPaths":[' "$(json_escape "$command")"
        for i in "${!selected[@]}"; do
          (( i == 0 )) || printf ','
          printf '"%s"' "$(json_escape "${selected[$i]}")"
        done
        printf '],"ignoredDirtyPaths":['
        for i in "${!dirty[@]}"; do
          (( i == 0 )) || printf ','
          printf '"%s"' "$(json_escape "${dirty[$i]}")"
        done
        printf '],"blockingDirtyPaths":[],"tempDir":'
        if [[ "$keep_temp" == "true" ]]; then
          printf '"%s"' "$(json_escape "$tmpdir")"
        else
          printf 'null'
        fi
        printf ',"argv":["cargo","test","-p","sts2","--no-run"],"message":"validation passed"}\n'
      fi
    else
      status=$?
      if [[ "$VALIDATE_JSON" == "true" ]]; then
        printf '{"command":"%s","status":"failed","code":"rust_proto_selected_failed","selectedPaths":[' "$(json_escape "$command")"
        for i in "${!selected[@]}"; do
          (( i == 0 )) || printf ','
          printf '"%s"' "$(json_escape "${selected[$i]}")"
        done
        printf '],"ignoredDirtyPaths":['
        for i in "${!dirty[@]}"; do
          (( i == 0 )) || printf ','
          printf '"%s"' "$(json_escape "${dirty[$i]}")"
        done
        printf '],"blockingDirtyPaths":[],"tempDir":'
        if [[ "$keep_temp" == "true" ]]; then
          printf '"%s"' "$(json_escape "$tmpdir")"
        else
          printf 'null'
        fi
        printf ',"argv":["cargo","test","-p","sts2","--no-run"],"message":"isolated Rust/protobuf validation failed with exit code %s"}\n' "$status"
      else
        printf 'isolated Rust/protobuf validation failed with exit code %s\n' "$status" >&2
      fi
      exit "$status"
    fi
    ;;
  producer-walk-profile)
    args=()
    strip_json_flag args "$@"
    report_path=".sts2/perf-reports/producer-walk-profile.json"
    tool_args=()
    i=0
    while (( i < ${#args[@]} )); do
      case "${args[$i]}" in
        --out)
          (( i + 1 < ${#args[@]} )) || fail "$command" "missing_argument" "--out requires a path"
          report_path="${args[$((i + 1))]}"
          i=$((i + 2))
          ;;
        --log|--snapshot|--param|--scenario|--env-kind|--env-label)
          (( i + 1 < ${#args[@]} )) || fail "$command" "missing_argument" "${args[$i]} requires a value"
          tool_args+=("${args[$i]}" "${args[$((i + 1))]}")
          i=$((i + 2))
          ;;
        --pretty)
          tool_args+=("--pretty")
          i=$((i + 1))
          ;;
        --help|-h)
          cat <<'EOF'
Usage: scripts/validate.sh producer-walk-profile [--log <path>]... [--snapshot <path>]... [options] [--json]

Folds the live scene watcher's producer-walk self-profiler windows into the shared `perf-report/1` envelope.
Report only: no wall-clock thresholds, and a number that moved never fails the command.

Options:
  --log <path>        Captured game log with [scene-watch][profile-json] lines (repeatable).
  --snapshot <path>   Snapshot JSON or a GetProducerWalkProfile result (repeatable).
  --param <k=v>       Extra params entry, e.g. the A/B lever under test (repeatable).
  --scenario <name>   Report scenario name (default: producer-walk).
  --env-kind <kind>   env.kind (default: ci under $CI, otherwise dev).
  --env-label <text>  env.label (default: OS + architecture + .NET description).
  --out <path>        Where the indented report is written (default: .sts2/perf-reports/producer-walk-profile.json).
  --pretty            Print the report indented instead of single-line.

Capture a log first with:
  SPIRECTL_SCENE_WATCH_PROFILE=1 <launch the live host> ... 2>&1 | tee .sts2/perf-reports/producer-walk.log
EOF
          exit 0
          ;;
        *)
          fail "$command" "invalid_argument" "unknown producer-walk-profile argument: ${args[$i]}"
          ;;
      esac
    done
    run_producer_walk_profile "$command" "$report_path" "${tool_args[@]}"
    ;;
  m78-live-encounter-artifacts)
    args=()
    strip_json_flag args "$@"
    encounter="kaiser_crab_boss"
    print_only=false
    repair_stale_endpoint=false
    launch=false
    attach=false
    i=0
    while (( i < ${#args[@]} )); do
      case "${args[$i]}" in
        --encounter)
          (( i + 1 < ${#args[@]} )) || fail "$command" "missing_argument" "--encounter requires a value"
          encounter="${args[$((i + 1))]}"
          i=$((i + 2))
          ;;
        --print-only)
          print_only=true
          i=$((i + 1))
          ;;
        --repair-stale-endpoint)
          repair_stale_endpoint=true
          i=$((i + 1))
          ;;
        --launch)
          launch=true
          i=$((i + 1))
          ;;
        --attach)
          attach=true
          i=$((i + 1))
          ;;
        *)
          fail "$command" "invalid_argument" "unknown m78-live-encounter-artifacts argument: ${args[$i]}"
          ;;
      esac
    done
    run_m78_live_encounter_artifacts "$command" "$encounter" "$print_only" "$repair_stale_endpoint" "$launch" "$attach"
    ;;
  docs-map-paths)
    args=()
    strip_json_flag args "$@"
    if (( ${#args[@]} > 0 )); then
      case "${args[0]}" in
        --help|-h)
          cat <<'EOF'
Usage: scripts/validate.sh docs-map-paths [--json]

Checks every backticked token in docs/maps/*.md that looks like a repo path (contains a slash, and its first
segment is a directory at the repo root) and fails if it does not exist on disk. The subsystem maps are the
routing table agents read before touching a subsystem, and a path that moved silently sends them nowhere.

Skipped by design: tokens with a glob, tokens containing a colon that is not a `:<line>` suffix (res://
and model:// keys, screen ids), and paths git ignores -- a documented generated output location exists only
after something has produced it. A `path:12` or `path:12-30` reference is checked as `path`.
EOF
          exit 0
          ;;
        *)
          fail "$command" "invalid_argument" "unknown docs-map-paths argument: ${args[0]}"
          ;;
      esac
    fi

    docs_map_checked=0
    docs_map_missing=()
    for docs_map_file in docs/maps/*.md; do
      [[ -f "$docs_map_file" ]] || continue
      while IFS= read -r docs_map_token; do
        docs_map_token="${docs_map_token%/}"
        docs_map_token="$(printf '%s' "$docs_map_token" | sed -E 's/:[0-9]+([-,][0-9]+)*$//')"
        [[ "$docs_map_token" == */* ]] || continue
        [[ "$docs_map_token" == *"*"* || "$docs_map_token" == *" "* || "$docs_map_token" == *:* ]] && continue
        [[ -d "${docs_map_token%%/*}" ]] || continue
        docs_map_checked=$((docs_map_checked + 1))
        if [[ ! -e "$docs_map_token" ]]; then
          # A GENERATED output location documented as such is not a broken reference. Whether it exists on
          # disk depends on whether anyone has run the thing that writes it, and whether its first segment
          # exists at all differs between the main checkout and a fresh worktree -- so without this the leg
          # passes or fails on local leftovers rather than on the docs.
          if git check-ignore -q "$docs_map_token" 2>/dev/null; then
            continue
          fi
          docs_map_missing+=("$docs_map_file -> $docs_map_token")
        fi
      done < <(grep -o '`[^`]*`' "$docs_map_file" | sed 's/^`//; s/`$//')
    done

    if (( docs_map_checked == 0 )); then
      fail "$command" "no_paths_checked" "docs/maps/*.md yielded no repo paths to check" "docs-map-paths"
    fi

    if (( ${#docs_map_missing[@]} > 0 )); then
      fail "$command" "missing_paths" \
        "$(printf '%s missing path(s) in docs/maps: %s' "${#docs_map_missing[@]}" "$(printf '%s; ' "${docs_map_missing[@]}")")" \
        "docs-map-paths"
    fi

    if [[ "${VALIDATE_JSON:-false}" == "true" ]]; then
      emit_json_success "$command" "checked $docs_map_checked repo path(s) in docs/maps/*.md" "docs-map-paths"
    else
      printf 'docs-map-paths: checked %s repo path(s) in docs/maps/*.md\n' "$docs_map_checked"
    fi
    ;;
  *)
    fail "$command" "invalid_validation_command" "unknown validation command: $command"
    ;;
esac
