#!/usr/bin/env bash
# One command to get a live host running with a fixture loaded, for presentation
# and runtime work (e.g. Node presentation capture / `dev screenshot`, which all
# require a live game). Runs sequentially:
#   1. cargo build                       (rebuild the cli)
#   2. sts2 game install-bridge          (install the bridge mod)
#   3. sts2 game close                   (optional: close any running game)
#   4. sts2 game launch                  (launch STS2; cleans up on failure)
#   5. sts2 game bridge-health           (confirm the bridge is reachable)
#   6. sts2 dev fixture load --path ...  (load the fixture into a live screen)
#
# Usage:
#   scripts/build-fixture.sh [--launch-timeout-ms <ms>] [--launch-attempts <n>] [--repair-stale-endpoint] fixtures/<name>.sts2.fixture.yaml
#
# Environment:
#   BUILD_FIXTURE_LAUNCH_TIMEOUT_MS  Launch/attach timeout in milliseconds (default: 30000).
#   BUILD_FIXTURE_LAUNCH_ATTEMPTS    Number of game launch attempts (default: 1).
#   BUILD_FIXTURE_QUIESCENT_MS       Post-attach settle budget in milliseconds (default: 8000).
#                                    A reachable bridge is not a settled screen: the boot flow
#                                    keeps pushing overlays for seconds afterwards, which is what
#                                    used to make the fixture load land on the wrong screen. 0
#                                    disables the wait; running out of budget is a notice, not a
#                                    failure.
#   BUILD_FIXTURE_CARGO_PROFILE      cargo profile for step 1: debug (default) or release.
#                                    Only changes which binary is built here; the `sts2` on PATH
#                                    decides what the later steps run.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

# shellcheck source=scripts/structured-build-steps.sh
source "$repo_root/scripts/structured-build-steps.sh"

usage() {
  printf 'usage: scripts/build-fixture.sh [--launch-timeout-ms <ms>] [--launch-attempts <n>] [--repair-stale-endpoint] <fixture-path>\n' >&2
}

launch_timeout_ms="$(structured_build_env_positive_int BUILD_FIXTURE_LAUNCH_TIMEOUT_MS 30000)"
quiescent_ms="${BUILD_FIXTURE_QUIESCENT_MS:-8000}"
launch_attempts="$(structured_build_env_positive_int BUILD_FIXTURE_LAUNCH_ATTEMPTS 1)"
cargo_profile="${BUILD_FIXTURE_CARGO_PROFILE:-debug}"
case "$cargo_profile" in
  debug) cargo_build_args=() ;;
  release) cargo_build_args=(--release) ;;
  *)
    printf 'BUILD_FIXTURE_CARGO_PROFILE must be "debug" or "release", got: %s\n' "$cargo_profile" >&2
    exit 2
    ;;
esac
repair_stale_endpoint=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --launch-timeout-ms)
      if [[ $# -lt 2 ]]; then
        usage
        exit 2
      fi
      launch_timeout_ms="$(structured_build_positive_int "--launch-timeout-ms" "$2")"
      shift 2
      ;;
    --launch-attempts)
      if [[ $# -lt 2 ]]; then
        usage
        exit 2
      fi
      launch_attempts="$(structured_build_positive_int "--launch-attempts" "$2")"
      shift 2
      ;;
    --repair-stale-endpoint)
      repair_stale_endpoint=true
      shift
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    --)
      shift
      break
      ;;
    -*)
      printf 'unknown option: %s\n' "$1" >&2
      usage
      exit 2
      ;;
    *)
      break
      ;;
  esac
done

if [[ $# -lt 1 ]]; then
  usage
  exit 2
fi

artifact_dir="$(structured_build_artifact_dir build-fixture)"

run_launch_failure_diagnostics() {
  structured_build_run_cleanup_step "$artifact_dir" failure-bridge-health sts2 --json game bridge-health
  structured_build_append_diagnostic_report "$artifact_dir" failure-bridge-health
  if [[ "$repair_stale_endpoint" == "true" ]]; then
    structured_build_run_cleanup_step "$artifact_dir" repair-stale-endpoint sts2 --json game bridge-health --repair-stale-endpoint
    structured_build_append_diagnostic_report "$artifact_dir" repair-stale-endpoint
  fi
  structured_build_run_cleanup_step "$artifact_dir" launch-cleanup sts2 game close --timeout-ms 30000 --interval-ms 250
  structured_build_append_diagnostic_report "$artifact_dir" launch-cleanup
}

structured_build_run_step "$artifact_dir" cargo-build cargo build "${cargo_build_args[@]}"
structured_build_run_step "$artifact_dir" install-bridge sts2 game install-bridge
structured_build_run_optional_step \
  "$artifact_dir" \
  pre-launch-close \
  "no_process_found,endpoint_missing,ipc_socket_missing" \
  sts2 game close --timeout-ms 30000 --interval-ms 250
if structured_build_run_retryable_step \
  "$artifact_dir" \
  game-launch \
  "$launch_attempts" \
  "launch_exited_before_ipc,launch_timeout" \
  sts2 game launch --timeout-ms "$launch_timeout_ms" --wait-quiescent-ms "$quiescent_ms"; then
  :
else
  launch_status=$?
  run_launch_failure_diagnostics
  exit "$launch_status"
fi
structured_build_run_step "$artifact_dir" bridge-health sts2 --json game bridge-health
structured_build_run_step "$artifact_dir" load-fixture sts2 dev fixture load --path "$@"

printf '{"command":"build-fixture","status":"passed","artifactDir":"%s","message":"fixture build and load completed"}\n' "$artifact_dir"
