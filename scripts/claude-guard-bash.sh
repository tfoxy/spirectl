#!/usr/bin/env bash
# PreToolUse(Bash) guard for spirectl.
#
# Two rules, both about commands that report success without having done the work. They are the
# reason scripts/validate.sh wraps cargo and dotnet at all; this stops an agent from reaching past
# the wrapper and getting a green result that means nothing.
#
# Contract (https://code.claude.com/docs/en/hooks.md):
#   stdin = hook JSON, bash command at .tool_input.command
#   deny  = exit 0 + {"hookSpecificOutput":{"hookEventName":"PreToolUse",
#                     "permissionDecision":"deny","permissionDecisionReason":"..."}}
#   warn  = exit 0 + {"hookSpecificOutput":{"hookEventName":"PreToolUse","additionalContext":"..."}}
# PreToolUse hooks fire in ALL permission modes, including bypassPermissions.
#
# Self-test: scripts/test-claude-guard.sh

set -uo pipefail

payload="$(cat)"
cmd="$(printf '%s' "$payload" | jq -r '.tool_input.command // ""')"
[ -n "$cmd" ] || exit 0

deny() {
  jq -n --arg r "$1" \
    '{hookSpecificOutput:{hookEventName:"PreToolUse",permissionDecision:"deny",permissionDecisionReason:$r}}'
  exit 0
}
has() { printf '%s' "$cmd" | grep -Eq "$1"; }

# ---------------------------------------------------------------------------------------------
# 0. `flock <file> -c '<cmd>'` holds the lock in an OPEN FILE DESCRIPTOR and hands that descriptor
#    to the command it runs — and to every process that command leaves behind. An orphaned
#    VBCSCompiler or MSBuild node therefore keeps holding the lock after the build has exited, and
#    the next `flock` on that file waits forever on work that already finished. `-o` / `--close`
#    closes the descriptor before exec'ing the command, which is what makes the lock last exactly
#    as long as the command does.
#
#    This runs BEFORE the validator exemption below on purpose: the hazard is in how flock was
#    invoked, not in what it wraps, so wrapping validate.sh does not make it safe.
# ---------------------------------------------------------------------------------------------
if has '(^|[[:space:];&|(])flock[[:space:]]' \
   && has '(^|[[:space:]])(-c|--command)([[:space:]]|$)'; then
  # Only the region between `flock` and its -c/--command token can carry flock's own options.
  flock_opts="$(printf '%s' "$cmd" | sed -E 's/.*flock[[:space:]]+//; s/[[:space:]]+(-c|--command)([[:space:]].*)?$//; s/[[:space:]]+(-c|--command)[[:space:]].*//')"
  if ! printf '%s' "$flock_opts" | grep -Eq '(^|[[:space:]])(-[a-zA-Z]*o[a-zA-Z]*|--close)([[:space:]]|$)'; then
    deny "\`flock <file> -c ...\` without -o LEAKS THE LOCK: the open lock descriptor is inherited by the command and by everything it leaves running.

A build spawns background compiler servers (VBCSCompiler, MSBuild nodes) that outlive it. They inherit the descriptor, so the lock is still held after the build exits, and the next flock on that file blocks forever waiting on work that already finished.

Use the close-on-exec form:
  flock -o <file> -c '<command>'      # or --close

(This repo's own dotnet legs also pass -nodeReuse:false -p:UseSharedCompilation=false so those servers are never spawned in the first place: scripts/validate.sh bridge-tests / bridge-build.)"
  fi
fi

# Anything routed through the repo's own validators is already correct by construction.
has 'validate\.sh|verify_parallel\.sh|build-fixture\.sh' && exit 0

# ---------------------------------------------------------------------------------------------
# 1. A filtered `cargo test` that matches ZERO tests exits 0. That is the headline no-op trap here,
#    and the reason validate.sh's cargo-test-filter / cargo-unit-test-filter exist: they fail with
#    "cargo test filter executed zero tests" instead.
#
#    Detect a *positional* argument to `cargo test` (i.e. a name filter), ignoring flags and the
#    values of flags that take one. No positional => it runs the whole target => not the trap.
# ---------------------------------------------------------------------------------------------
if has '(^|[[:space:];&|])cargo[[:space:]]+(\+[^[:space:]]+[[:space:]]+)?test([[:space:]]|$)'; then
  segment="$(printf '%s' "$cmd" | sed -E 's/.*cargo[[:space:]]+(\+[^[:space:]]+[[:space:]]+)?test//; s/[;&|].*//')"
  filter=""
  skip_next=0
  for tok in $segment; do
    if [ "$skip_next" = 1 ]; then skip_next=0; continue; fi
    case "$tok" in
      --)                 break ;;                       # everything after -- goes to the harness
      -p|--package|--test|--bench|--example|--bin|--features|--manifest-path|--target|--target-dir|--profile|--color|-j|--jobs)
                          skip_next=1 ;;
      -*)                 ;;                             # a flag, or --flag=value
      *)                  filter="$tok"; break ;;
    esac
  done
  if [ -n "$filter" ]; then
    deny "A filtered \`cargo test\` EXITS 0 WHEN THE FILTER MATCHES NOTHING — a typo in \"$filter\" reads exactly like a pass.

Use the wrappers that fail on zero matches instead:
  scripts/validate.sh cargo-test-filter --package sts2 --test <target> --filter $filter
  scripts/validate.sh cargo-unit-test-filter --package sts2 --filter $filter

Note the crate's unit tests live under src/lib.rs, not an integration target, so \`--test\` matches nothing for them — that is the unit-test-filter form.
Running a whole target unfiltered (\`cargo test -p sts2 --test cli_snapshots\`) is fine and not blocked."
  fi
fi

# ---------------------------------------------------------------------------------------------
# 2. Parallel MSBuild is unsafe in this repo (MSB3030). Every dotnet invocation is forced to -m:1.
# ---------------------------------------------------------------------------------------------
if has '(^|[[:space:];&|])dotnet[[:space:]]+(build|test|publish|pack)' && ! has '\-m:1'; then
  deny "Every dotnet invocation in this repo runs serial: parallel MSBuild races here (MSB3030).

Add -m:1, or better, go through the validator:
  scripts/validate.sh bridge-build
  scripts/validate.sh bridge-tests      # run this leg ALONE, never alongside other legs"
fi

exit 0
