#!/usr/bin/env bash
# Parse captured summaries and exercise terminal JSON paths without invoking Cargo or dotnet.
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
mkdir -p "$repo_root/.local"
scratch="$(mktemp -d "$repo_root/.local/test-validate-counts.XXXXXX")"
completed=false
trap 'rm -rf "$scratch"; if [[ "$completed" != true ]]; then
  echo "validate-counts self-test did not complete" >&2; exit 1
fi' EXIT
export TMPDIR="$scratch"
source "$repo_root/scripts/validate.sh"
VALIDATE_JSON=true
pass=0

cat > "$scratch/cargo-pass" <<'EOF'
running 5 tests
test result: ok. 3 passed; 0 failed; 2 ignored; 0 measured; 999 filtered out; finished in 0.01s
test result: ok. 4 passed; 0 failed; 1 ignored; 0 measured; 15 filtered out; finished in 0.02s
EOF
cat > "$scratch/cargo-fail" <<'EOF'
test result: ok. 2 passed; 0 failed; 0 ignored; 0 measured; 1 filtered out; finished in 0.01s
test result: FAILED. 1 passed; 2 failed; 1 ignored; 0 measured; 5 filtered out; finished in 0.02s
EOF
printf 'test result: ok. 0 passed; 0 failed; 0 ignored; 0 measured; 55 filtered out; finished in 0.00s\n' > "$scratch/cargo-zero"
cat > "$scratch/dotnet-pass" <<'EOF'
Passed!  - Failed:     0, Passed:    12, Skipped:     2, Total:    14, Duration: 1 s - First.dll (net9.0)
Passed!  - Failed:     0, Passed:     5, Skipped:     1, Total:     6, Duration: 10 ms - Second.dll (net9.0)
EOF
cat > "$scratch/dotnet-fail" <<'EOF'
Passed!  - Failed:     0, Passed:     2, Skipped:     1, Total:     3, Duration: 1 s - First.dll (net9.0)
Failed!  - Failed:     2, Passed:     3, Skipped:     0, Total:     5, Duration: 5 ms - Second.dll (net9.0)
EOF
printf 'No test matches the given testcase filter `typo`\n' > "$scratch/dotnet-zero"
printf 'System.Net.Sockets.SocketException (13): Permission denied\n' > "$scratch/blocked"
printf 'error MSB3021: cannot copy bridge-mod/src/Example/obj/Debug/ref/Example.dll\n' > "$scratch/locked"
printf 'build failed before any tests ran\n' > "$scratch/no-summary"

check_parser() {
  local parser="$1" sample="$2" expected="$3"
  "$parser" "$scratch/$sample" | jq -e ". == $expected" > /dev/null
  pass=$((pass + 1))
}

cargo_pass='{"passed":7,"failed":0,"skipped":3,"total":10}'
cargo_fail='{"passed":3,"failed":2,"skipped":1,"total":6}'
dotnet_pass='{"passed":17,"failed":0,"skipped":3,"total":20}'
dotnet_fail='{"passed":5,"failed":2,"skipped":1,"total":8}'
zero='{"passed":0,"failed":0,"skipped":0,"total":0}'
check_parser parse_cargo_test_counts cargo-pass "$cargo_pass"
check_parser parse_cargo_test_counts cargo-fail "$cargo_fail"
check_parser parse_cargo_test_counts cargo-zero "$zero"
check_parser parse_cargo_test_counts no-summary "$zero"
check_parser parse_dotnet_test_counts dotnet-pass "$dotnet_pass"
check_parser parse_dotnet_test_counts dotnet-fail "$dotnet_fail"
check_parser parse_dotnet_test_counts dotnet-zero "$zero"
check_parser parse_dotnet_test_counts no-summary "$zero"

# Supply captured output directly; retry cases use the lock error only on the first attempt.
run_command_capture() {
  if [[ "$retry" == true && "$attempt" == 0 ]]; then
    cp "$scratch/locked" "$1"
    attempt=1
    return 1
  fi
  cp "$scratch/$sample" "$1"
  return "$sample_status"
}
cleanup_bridge_ref_outputs() {
  local -n attempted_ref=$1 removed_ref=$2
  attempted_ref=()
  removed_ref=()
}

check_result() {
  local want_status="$1" want_code="$2" want_counts="$3"
  shift 3
  local result status=0
  result="$("$@")" || status=$?
  [[ "$status" == "$want_status" ]]
  printf '%s\n' "$result" | jq -e --arg code "$want_code" --argjson counts "$want_counts" \
    '.code == $code and .tests == $counts' > /dev/null
  pass=$((pass + 1))
}

retry=false
for wrapper in run_cargo_test_filter run_cargo_unit_test_filter; do
  args=(cargo-unit-test-filter sts2 filter)
  if [[ "$wrapper" == run_cargo_test_filter ]]; then
    args=(cargo-test-filter sts2 target filter)
  fi
  sample=cargo-pass; sample_status=0
  check_result 0 ok "$cargo_pass" "$wrapper" "${args[@]}"
  sample=cargo-fail; sample_status=101
  check_result 101 validation_failed "$cargo_fail" "$wrapper" "${args[@]}"
  sample=cargo-zero; sample_status=0
  check_result 1 zero_tests_matched "$zero" "$wrapper" "${args[@]}"
  sample=no-summary; sample_status=101
  check_result 101 validation_failed "$zero" "$wrapper" "${args[@]}"
done

for retry in false true; do
  attempt=0
  sample=dotnet-pass; sample_status=0
  check_result 0 ok "$dotnet_pass" run_bridge_command_with_locked_retry bridge-tests dotnet test
  sample=dotnet-fail; sample_status=1
  check_result 1 validation_failed "$dotnet_fail" run_bridge_command_with_locked_retry bridge-tests dotnet test
  sample=dotnet-zero; sample_status=0
  check_result 1 zero_tests_matched "$zero" run_bridge_command_with_locked_retry bridge-tests dotnet test
  sample=blocked; sample_status=1
  check_result 125 environment_blocked "$zero" run_bridge_command_with_locked_retry bridge-tests dotnet test
done

retry=false
sample=no-summary; sample_status=0
run_bridge_command_with_locked_retry bridge-build dotnet build | jq -e 'has("tests") | not' > /dev/null
pass=$((pass + 1))
completed=true
echo "validate-counts self-test: $pass checks passed, 0 failures"
