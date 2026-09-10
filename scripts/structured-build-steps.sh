#!/usr/bin/env bash

structured_build_artifact_dir() {
  local workflow="$1"
  local root="${SPIRECTL_BUILD_ARTIFACT_ROOT:-$repo_root/.sts2/artifacts/$workflow}"
  local stamp
  stamp="$(date -u '+%Y%m%dT%H%M%SZ')-$$"
  printf '%s/%s\n' "$root" "$stamp"
}

structured_build_positive_int() {
  local name="$1"
  local value="$2"
  if [[ ! "$value" =~ ^[1-9][0-9]*$ ]]; then
    printf '%s must be a positive integer, got %q\n' "$name" "$value" >&2
    exit 2
  fi
  printf '%s\n' "$value"
}

structured_build_env_positive_int() {
  local name="$1"
  local default_value="$2"
  local value="${!name:-$default_value}"
  structured_build_positive_int "$name" "$value"
}

structured_build_write_report_impl() {
  local promote="$1"
  shift
  local artifact_dir="$1"
  local step="$2"
  local status="$3"
  local exit_code="$4"
  local stdout_file="$5"
  local stderr_file="$6"
  shift 6

  mkdir -p "$artifact_dir"
  python3 - "$artifact_dir/report.json" "$artifact_dir/$step.report.json" "$promote" "$artifact_dir" "$step" "$status" "$exit_code" "$stdout_file" "$stderr_file" "$@" <<'PY'
import json
import sys
from pathlib import Path

report_path = Path(sys.argv[1])
step_report_path = Path(sys.argv[2])
promote = sys.argv[3] == "true"
artifact_dir = sys.argv[4]
step = sys.argv[5]
status = sys.argv[6]
exit_code = int(sys.argv[7])
stdout_path = Path(sys.argv[8])
stderr_path = Path(sys.argv[9])
argv = sys.argv[10:]

def last_json_line(text):
    for line in reversed(text.splitlines()):
        stripped = line.strip()
        if not stripped.startswith("{"):
            continue
        try:
            return json.loads(stripped)
        except json.JSONDecodeError:
            continue
    return None

stdout = stdout_path.read_text(encoding="utf-8", errors="replace") if stdout_path.exists() else ""
stderr = stderr_path.read_text(encoding="utf-8", errors="replace") if stderr_path.exists() else ""
parsed_stdout = last_json_line(stdout)
parsed_stderr = last_json_line(stderr)
parsed = parsed_stdout if parsed_stdout is not None else parsed_stderr
error_code = None
if isinstance(parsed, dict):
    error = parsed.get("error")
    if isinstance(error, dict) and isinstance(error.get("code"), str):
        error_code = error["code"]

report = {
    "schemaVersion": "spirectl.dev.structured-build/v0",
    "status": status,
    "artifactDir": artifact_dir,
    "step": step,
    "exitCode": exit_code,
    "argv": argv,
    "stdoutPath": str(stdout_path),
    "stderrPath": str(stderr_path),
    "stdout": stdout,
    "stderr": stderr,
}
if status == "failed":
    report["failedStep"] = step
if parsed_stdout is not None:
    report["parsedStdout"] = parsed_stdout
if parsed_stderr is not None:
    report["parsedStderr"] = parsed_stderr
if error_code is not None:
    report["errorCode"] = error_code
if status == "failed" and isinstance(parsed, dict):
    source = parsed.get("error")
    if not isinstance(source, dict):
        source = parsed
    diagnostics = {}
    for key in (
        "code",
        "message",
        "process",
        "recentLogs",
        "latestLog",
        "logPath",
        "launchDiagnostics",
        "diagnostics",
        "endpoint",
        "socketPath",
        "pipeName",
        "tcpAddress",
        "lastError",
        "wrapperLogs",
        "safeNextCommands",
        "nextCommands",
    ):
        if key in source:
            diagnostics[key] = source[key]
    if diagnostics:
        report["failureDiagnostics"] = diagnostics
report["nextCommands"] = [
    "sts2 --json game bridge-health",
    "sts2 game attach",
    "sts2 game launch",
]
encoded = json.dumps(report, separators=(",", ":")) + "\n"
step_report_path.write_text(encoded, encoding="utf-8")
if promote:
    report_path.write_text(encoded, encoding="utf-8")
PY
}

structured_build_write_report() {
  structured_build_write_report_impl true "$@"
}

structured_build_write_step_report() {
  structured_build_write_report_impl false "$@"
}

structured_build_append_diagnostic_report() {
  local artifact_dir="$1"
  local step="$2"

  python3 - "$artifact_dir/report.json" "$artifact_dir/$step.report.json" <<'PY'
import json
import sys
from pathlib import Path

report_path = Path(sys.argv[1])
diagnostic_report_path = Path(sys.argv[2])
if not report_path.exists() or not diagnostic_report_path.exists():
    raise SystemExit(0)

report = json.loads(report_path.read_text(encoding="utf-8"))
diagnostic = json.loads(diagnostic_report_path.read_text(encoding="utf-8"))
entry = {
    "step": diagnostic.get("step"),
    "status": diagnostic.get("status"),
    "exitCode": diagnostic.get("exitCode"),
    "reportPath": str(diagnostic_report_path),
    "stdoutPath": diagnostic.get("stdoutPath"),
    "stderrPath": diagnostic.get("stderrPath"),
}
for key in ("errorCode", "failedStep"):
    if key in diagnostic:
        entry[key] = diagnostic[key]
report.setdefault("diagnosticReports", []).append(entry)
report_path.write_text(json.dumps(report, separators=(",", ":")) + "\n", encoding="utf-8")
PY
}

structured_build_attach_attempts() {
  local artifact_dir="$1"
  local step="$2"

  python3 - "$artifact_dir/report.json" "$artifact_dir/$step.report.json" "$artifact_dir" "$step" <<'PY'
import json
import re
import sys
from pathlib import Path

report_path = Path(sys.argv[1])
step_report_path = Path(sys.argv[2])
artifact_dir = Path(sys.argv[3])
step = sys.argv[4]
if not report_path.exists() and not step_report_path.exists():
    raise SystemExit(0)

pattern = re.compile(rf"^{re.escape(step)}-attempt-(\d+)\.report\.json$")
attempt_reports = []
for path in artifact_dir.glob(f"{step}-attempt-*.report.json"):
    match = pattern.match(path.name)
    if match:
        attempt_reports.append((int(match.group(1)), path))
attempt_reports.sort(key=lambda item: item[0])
if not attempt_reports:
    raise SystemExit(0)

attempts = []
for attempt, path in attempt_reports:
    attempt_report = json.loads(path.read_text(encoding="utf-8"))
    entry = {
        "attempt": attempt,
        "step": attempt_report.get("step"),
        "status": attempt_report.get("status"),
        "exitCode": attempt_report.get("exitCode"),
        "reportPath": str(path),
        "stdoutPath": attempt_report.get("stdoutPath"),
        "stderrPath": attempt_report.get("stderrPath"),
    }
    for key in ("errorCode", "failedStep"):
        if key in attempt_report:
            entry[key] = attempt_report[key]
    attempts.append(entry)

for path in (report_path, step_report_path):
    if not path.exists():
        continue
    report = json.loads(path.read_text(encoding="utf-8"))
    report["attempts"] = attempts
    path.write_text(json.dumps(report, separators=(",", ":")) + "\n", encoding="utf-8")
PY
}

structured_build_stdout_has_error() {
  local stdout_file="$1"
  python3 - "$stdout_file" <<'PY'
import json
import sys
from pathlib import Path

text = Path(sys.argv[1]).read_text(encoding="utf-8", errors="replace")
for line in reversed(text.splitlines()):
    line = line.strip()
    if not line.startswith("{"):
        continue
    try:
        value = json.loads(line)
    except json.JSONDecodeError:
        continue
    raise SystemExit(0 if isinstance(value, dict) and "error" in value else 1)
raise SystemExit(1)
PY
}

structured_build_stdout_error_code() {
  local stdout_file="$1"
  python3 - "$stdout_file" <<'PY'
import json
import sys
from pathlib import Path

text = Path(sys.argv[1]).read_text(encoding="utf-8", errors="replace")
for line in reversed(text.splitlines()):
    line = line.strip()
    if not line.startswith("{"):
        continue
    try:
        value = json.loads(line)
    except json.JSONDecodeError:
        continue
    if isinstance(value, dict):
        error = value.get("error")
        if isinstance(error, dict) and isinstance(error.get("code"), str):
            print(error["code"])
            raise SystemExit(0)
raise SystemExit(1)
PY
}

structured_build_error_code_in_list() {
  local error_code="$1"
  local allowed_error_codes="$2"
  [[ ",$allowed_error_codes," == *,"$error_code",* ]]
}

structured_build_run_step() {
  local artifact_dir="$1"
  local step="$2"
  shift 2

  mkdir -p "$artifact_dir"
  local stdout_file="$artifact_dir/$step.stdout"
  local stderr_file="$artifact_dir/$step.stderr"
  set +e
  "$@" >"$stdout_file" 2>"$stderr_file"
  local exit_code=$?
  set -e

  local structured_error=false
  if structured_build_stdout_has_error "$stdout_file"; then
    structured_error=true
  fi

  if (( exit_code == 0 )) && [[ "$structured_error" != "true" ]]; then
    structured_build_write_report "$artifact_dir" "$step" "passed" "$exit_code" "$stdout_file" "$stderr_file" "$@"
    return 0
  fi

  structured_build_write_report "$artifact_dir" "$step" "failed" "$exit_code" "$stdout_file" "$stderr_file" "$@"
  printf '%s failed at step %s; see %s/report.json\n' "$(basename "$0")" "$step" "$artifact_dir" >&2
  if (( exit_code == 0 )); then
    return 1
  fi
  return "$exit_code"
}

structured_build_run_retryable_step() {
  local artifact_dir="$1"
  local step="$2"
  local attempts="$3"
  local retry_error_codes="$4"
  shift 4

  structured_build_positive_int "${step} attempts" "$attempts" >/dev/null
  mkdir -p "$artifact_dir"

  local attempt=1
  while (( attempt <= attempts )); do
    local attempt_step="$step-attempt-$attempt"
    local stdout_file="$artifact_dir/$attempt_step.stdout"
    local stderr_file="$artifact_dir/$attempt_step.stderr"
    set +e
    "$@" >"$stdout_file" 2>"$stderr_file"
    local exit_code=$?
    set -e

    local structured_error=false
    if structured_build_stdout_has_error "$stdout_file"; then
      structured_error=true
    fi

    if (( exit_code == 0 )) && [[ "$structured_error" != "true" ]]; then
      structured_build_write_step_report "$artifact_dir" "$attempt_step" "passed" "$exit_code" "$stdout_file" "$stderr_file" "$@"
      structured_build_write_report "$artifact_dir" "$step" "passed" "$exit_code" "$stdout_file" "$stderr_file" "$@"
      structured_build_attach_attempts "$artifact_dir" "$step"
      return 0
    fi

    local error_code=""
    error_code="$(structured_build_stdout_error_code "$stdout_file" || true)"
    structured_build_write_step_report "$artifact_dir" "$attempt_step" "failed" "$exit_code" "$stdout_file" "$stderr_file" "$@"

    if (( attempt < attempts )) && structured_build_error_code_in_list "$error_code" "$retry_error_codes"; then
      attempt=$((attempt + 1))
      continue
    fi

    structured_build_write_report "$artifact_dir" "$step" "failed" "$exit_code" "$stdout_file" "$stderr_file" "$@"
    structured_build_attach_attempts "$artifact_dir" "$step"
    printf '%s failed at step %s; see %s/report.json\n' "$(basename "$0")" "$step" "$artifact_dir" >&2
    if (( exit_code == 0 )); then
      return 1
    fi
    return "$exit_code"
  done
}

structured_build_run_optional_step() {
  local artifact_dir="$1"
  local step="$2"
  local allowed_error_codes="$3"
  shift 3

  mkdir -p "$artifact_dir"
  local stdout_file="$artifact_dir/$step.stdout"
  local stderr_file="$artifact_dir/$step.stderr"
  set +e
  "$@" >"$stdout_file" 2>"$stderr_file"
  local exit_code=$?
  set -e

  local structured_error=false
  if structured_build_stdout_has_error "$stdout_file"; then
    structured_error=true
  fi

  if (( exit_code == 0 )) && [[ "$structured_error" != "true" ]]; then
    structured_build_write_report "$artifact_dir" "$step" "passed" "$exit_code" "$stdout_file" "$stderr_file" "$@"
    return 0
  fi

  local error_code=""
  error_code="$(structured_build_stdout_error_code "$stdout_file" || true)"
  if [[ ",$allowed_error_codes," == *,$error_code,* ]]; then
    structured_build_write_report "$artifact_dir" "$step" "nonfatal" "$exit_code" "$stdout_file" "$stderr_file" "$@"
    return 0
  fi

  structured_build_write_report "$artifact_dir" "$step" "failed" "$exit_code" "$stdout_file" "$stderr_file" "$@"
  printf '%s failed at step %s; see %s/report.json\n' "$(basename "$0")" "$step" "$artifact_dir" >&2
  if (( exit_code == 0 )); then
    return 1
  fi
  return "$exit_code"
}

structured_build_run_cleanup_step() {
  local artifact_dir="$1"
  local step="$2"
  shift 2

  mkdir -p "$artifact_dir"
  local stdout_file="$artifact_dir/$step.stdout"
  local stderr_file="$artifact_dir/$step.stderr"
  set +e
  "$@" >"$stdout_file" 2>"$stderr_file"
  local exit_code=$?
  set -e

  local status="passed"
  local structured_error=false
  if structured_build_stdout_has_error "$stdout_file"; then
    structured_error=true
  fi
  if (( exit_code != 0 )) || [[ "$structured_error" == "true" ]]; then
    status="failed"
  fi
  structured_build_write_step_report "$artifact_dir" "$step" "$status" "$exit_code" "$stdout_file" "$stderr_file" "$@"
  return 0
}
