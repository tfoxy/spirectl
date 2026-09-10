#!/usr/bin/env bash
# Self-test for scripts/claude-guard-bash.sh. Two-sided: a guard that blocks a documented workflow
# is worse than no guard, so the must-allow half replays the command lines in docs/.
#
# Every case runs twice, once per HARNESS: the Claude Code envelope, and the Codex CLI envelope
# (extra fields, no $CLAUDE_PROJECT_DIR, and a cwd one level down inside the repo, because Codex
# hooks run in the session cwd rather than at the repo root). The same guard file is registered with
# both CLIs, so a verdict that differs between them is a bug.

set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
REPO="$PWD"
GUARD="$REPO/scripts/claude-guard-bash.sh"

pass=0; fail=0

# Echoes deny | warn | allow for a command, from a given cwd, under a given harness.
verdict() { # verdict <cmd> [cwd] [claude|codex]
  local cmd="$1" cwd="${2:-$REPO}" harness="${3:-claude}" out
  if [ "$harness" = codex ]; then
    # Codex runs the hook in the session cwd, which is routinely a subdirectory.
    if [ -d "$cwd/cli" ]; then cwd="$cwd/cli"; fi
    out="$(jq -n --arg c "$cmd" --arg d "$cwd" \
            '{hook_event_name:"PreToolUse",tool_name:"Bash",cwd:$d,
              session_id:"0195f0de-0000-7000-8000-000000000000",
              turn_id:"turn_1",transcript_path:($d+"/.codex/transcript.jsonl"),
              permission_mode:"default",tool_use_id:"call_1",
              tool_input:{command:$c}}' \
          | env -u CLAUDE_PROJECT_DIR bash "$GUARD")"
  else
    out="$(jq -n --arg c "$cmd" --arg d "$cwd" \
            '{hook_event_name:"PreToolUse",tool_name:"Bash",cwd:$d,tool_input:{command:$c}}' \
          | bash "$GUARD")"
  fi
  if   printf '%s' "$out" | grep -q '"permissionDecision":[[:space:]]*"deny"'; then echo deny
  elif printf '%s' "$out" | grep -q 'additionalContext'; then echo warn
  else echo allow; fi
}

expect() { # expect <want> <cmd> [cwd]  — asserted under BOTH harnesses
  local want="$1" cmd="$2" cwd="${3:-$REPO}" got harness
  for harness in claude codex; do
    got="$(verdict "$cmd" "$cwd" "$harness")"
    if [ "$got" = "$want" ]; then pass=$((pass+1)); else
      fail=$((fail+1)); printf 'FAIL  [%s] want=%-5s got=%-5s  %s\n' "$harness" "$want" "$got" "$cmd" >&2
    fi
  done
}

echo "== must block =="
expect deny 'cargo test -p sts2 state_actions'
expect deny 'cargo test -p sts2 --test dev_workflows some_case'
expect deny 'cargo test --test cli_snapshots snapshot_help'
expect deny 'dotnet build bridge-mod/src/Spirectl.BridgeMod.Sts2Host/Spirectl.BridgeMod.Sts2Host.csproj'
expect deny 'dotnet test bridge-mod/tests/Spirectl.BridgeMod.Tests/Spirectl.BridgeMod.Tests.csproj'
expect deny "flock /tmp/sts2-dotnet-build.lock -c 'dotnet build spirectl.sln -m:1'"
expect deny "flock -w 600 /tmp/sts2-dotnet-build.lock -c 'scripts/validate.sh bridge-tests'"

echo "== must allow =="
expect allow 'cargo build -p sts2'
expect allow 'cargo test -p sts2'
expect allow 'cargo test -p sts2 --test cli_snapshots'
expect allow 'cargo fmt --check'
expect allow 'mise exec -- cargo build -p sts2'
expect allow 'scripts/validate.sh cargo-test-filter --package sts2 --test dev_workflows --filter some_case'
expect allow 'scripts/validate.sh cargo-unit-test-filter --package sts2 --filter state_actions'
expect allow 'scripts/validate.sh bridge-tests'
expect allow 'scripts/verify_parallel.sh --json'
expect allow 'dotnet build spirectl.sln -m:1'
expect allow 'dotnet test bridge-mod/tests/Spirectl.BridgeMod.Tests/Spirectl.BridgeMod.Tests.csproj -m:1'
expect allow 'cargo run -p sts2 -- --json game bridge-health'
expect allow "flock -o /tmp/sts2-dotnet-build.lock -c 'dotnet build spirectl.sln -m:1'"
expect allow "flock --close /tmp/sts2-dotnet-build.lock -c 'scripts/validate.sh bridge-tests'"
expect allow "flock -no /tmp/sts2-dotnet-build.lock -c 'scripts/validate.sh bridge-tests'"
expect allow 'flock /tmp/sts2-dotnet-build.lock echo no-command-flag'
expect allow 'npm --prefix npm-wrapper test'

echo "== must allow: command lines in docs/ =="
expected_deny_re='(cargo test .*--test [a-z_]+ [a-z_]+|dotnet (build|test)( |$))'
harvested=0; flagged=0
while IFS= read -r line; do
  harvested=$((harvested+1))
  for harness in claude codex; do
    if [ "$(verdict "$line" "$REPO" "$harness")" = deny ] \
       && ! printf '%s' "$line" | grep -Eq "$expected_deny_re"; then
      flagged=$((flagged+1))
      printf 'FAIL  [%s] guard denies a documented command: %s\n' "$harness" "$line" >&2
    fi
  done
done < <(
  awk '/^```/{inblock=!inblock; next} inblock' docs/*.md docs/maps/*.md 2>/dev/null \
  | sed 's/^[[:space:]]*//' \
  | grep -E '^(cargo|dotnet|sts2|scripts/|npm|npx|mise|node)' \
  | sort -u
)
fail=$((fail+flagged))
echo "  replayed $harvested documented command lines under 2 harnesses, $flagged false positives"

echo
if [ "$fail" -eq 0 ]; then echo "guard self-test: $pass checks passed, 0 failures"
else echo "guard self-test: $pass passed, $fail FAILED" >&2; exit 1; fi
