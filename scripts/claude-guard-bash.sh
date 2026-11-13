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
#    The hazard is in how flock was invoked, not in what it wraps, so wrapping validate.sh
#    does not make it safe.
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

# --- private companion guard (begin) ----------------------------------------------------------
# BLOCK: force-adding the private companion. private/ (a worktree's `private` link) is gitignored
# because this repository is public; `git add -f` is the one way it gets committed anyway.
# `git` in command position, global options, `add`, a -f/--force flag (alone or in a short-flag
# cluster such as -fA) and a pathspec that is private or under it. `git -C private add -f …`
# works inside the private repository itself and is not matched: its pathspec is not `private`.
# ---------------------------------------------------------------------------------------------
git_opt_pc='((-C|-c|--git-dir|--work-tree|--namespace|--exec-path)[[:space:]]+[^[:space:];&|()]+|--[A-Za-z-]+(=[^[:space:];&|()]+)?|-[pP])[[:space:]]+'
git_add_re="(^|[[:space:];&|(])git[[:space:]]+(${git_opt_pc})*add([[:space:]][^;&|)]*)?"
while IFS= read -r add_args; do
  add_args=" ${add_args#*add}"
  if printf '%s\n' "$add_args" | grep -Eq '[[:space:]](--force|-[A-Za-z]*f[A-Za-z]*)([[:space:]]|$)' \
     && printf '%s\n' "$add_args" | grep -Eq "[[:space:]]['\"]?(\./|:/|:\(top\))?private(/[^[:space:]]*)?['\"]?([[:space:]]|\$)"; then
    deny "Refusing \`git add --force\` of private/: it is the gitignored private companion (or a worktree's link to it), and this repository is public. Commit private material inside private/ itself (\`git -C private add …\`), never here."
  fi
done < <(printf '%s' "$cmd" | grep -Eo "$git_add_re")
# --- private companion guard (end) ------------------------------------------------------------

# ---------------------------------------------------------------------------------------------
# 1. A filtered `cargo test` that matches ZERO tests exits 0. That is the headline no-op trap here,
#    and the reason validate.sh's cargo-test-filter / cargo-unit-test-filter exist: they fail with
#    "cargo test filter executed zero tests" instead.
#
#    Detect a *positional* argument to `cargo test` (i.e. a name filter), ignoring flags and the
#    values of flags that take one. No positional => it runs the whole target => not the trap.
# ---------------------------------------------------------------------------------------------
# Inspect each command independently; quoted arguments and heredoc data are not commands.
IFS=$'\t' read -r rule filter < <(python3 - "$cmd" <<'PYTHON'
import re
import shlex
import sys

word = r"(?:\\[\s\S]|'[^']*'|\"(?:\\[\s\S]|[^\"\\])*\"|\$(?!\()|[^\s;&|()'\"\\<>$])+"
token = re.compile(r"#[^\n]*|<<-?|\$\(|&&|\|\||[;|&()\n]|<[^<>\s]+>|" + word + r"|[<>]")

def unquote(raw):
    return "".join(shlex.split(raw))

def substitution_end(source, pos):
    depth, quote = 1, None
    while pos < len(source):
        char = source[pos]
        if quote == "'":
            if char == "'":
                quote = None
        elif char == "\\":
            pos += 2
            continue
        elif source.startswith("$(", pos):
            pos = substitution_end(source, pos + 2)
            continue
        elif char == '"':
            quote = None if quote == '"' else '"'
        elif char == "'" and quote is None:
            quote = "'"
        elif quote is None and char in "()":
            depth += 1 if char == "(" else -1
            if depth == 0:
                return pos + 1
        pos += 1
    return pos

def substitution_bodies(raw):
    pos, double_quoted = 0, False
    while pos < len(raw):
        if raw[pos] == "\\":
            pos += 2
            continue
        if raw[pos] == "'" and not double_quoted:
            pos = raw.find("'", pos + 1) + 1
        elif raw.startswith("$(", pos):
            end = substitution_end(raw, pos + 2)
            yield raw[pos + 2:end - 1]
            pos = end
        else:
            if raw[pos] == '"':
                double_quoted = not double_quoted
            pos += 1

def commands(source):
    args, pending = [], []
    delimiter = None
    pos = 0
    while match := token.search(source, pos):
        raw = match.group()
        pos = match.end()
        if raw.startswith("#"):
            continue
        if delimiter is not None:
            pending.append((unquote(raw), delimiter == "<<-"))
            delimiter = None
        elif raw in ("<<", "<<-"):
            delimiter = raw
        elif raw == "$(":
            end = substitution_end(source, pos)
            yield from commands(source[pos:end - 1])
            args.append("$()")
            pos = end
        elif raw in (";", "&", "&&", "||", "|", "(", ")", "\n"):
            if args:
                yield args
                args = []
            if raw == "\n":
                for end, tabs in pending:
                    while pos < len(source):
                        stop = source.find("\n", pos)
                        stop = len(source) if stop < 0 else stop
                        line, pos = source[pos:stop], stop + 1
                        if (line.lstrip("\t") if tabs else line) == end:
                            break
                pending = []
        else:
            for body in substitution_bodies(raw):
                yield from commands(body)
            args.append(unquote(raw))
    if args:
        yield args

def expanded(source):
    for args in commands(source):
        while args and re.match(r"^[A-Za-z_]\w*=", args[0]):
            args = args[1:]
        if not args:
            continue
        yield args
        if args[0] in ("bash", "sh"):
            for i, arg in enumerate(args[1:], 1):
                if re.fullmatch(r"-[a-zA-Z]*c[a-zA-Z]*", arg) and i + 1 < len(args):
                    yield from expanded(args[i + 1])
                    break

value_flags = set("-p --package --test --bench --example --bin --features --manifest-path "
                  "--target --target-dir --profile --color -j --jobs".split())
for args in expanded(sys.argv[1]):
    if args[0] == "dotnet" and len(args) > 1 and args[1] in ("build", "test", "publish", "pack"):
        if "-m:1" not in args[2:]:
            print("dotnet")
            break
    if args[0] != "cargo":
        continue
    rest = args[1:]
    if rest and rest[0].startswith("+"):
        rest = rest[1:]
    if not rest or rest[0] != "test":
        continue
    skip_next = False
    for arg in rest[1:]:
        if skip_next:
            skip_next = False
        elif arg == "--":
            break
        elif arg in value_flags:
            skip_next = True
        elif not arg.startswith("-"):
            print("cargo\t" + arg)
            sys.exit(0)
PYTHON
)
if [ "$rule" = cargo ]; then
  deny "A filtered \`cargo test\` EXITS 0 WHEN THE FILTER MATCHES NOTHING — a typo in \"$filter\" reads exactly like a pass.

Use the wrappers that fail on zero matches instead:
  scripts/validate.sh cargo-test-filter --package sts2 --test <target> --filter $filter
  scripts/validate.sh cargo-unit-test-filter --package sts2 --filter $filter

Note the crate's unit tests live under src/lib.rs, not an integration target, so \`--test\` matches nothing for them — that is the unit-test-filter form.
Running a whole target unfiltered (\`cargo test -p sts2 --test cli_snapshots\`) is fine and not blocked."
fi

# ---------------------------------------------------------------------------------------------
# 2. Parallel MSBuild is unsafe in this repo (MSB3030). Every dotnet invocation is forced to -m:1.
# ---------------------------------------------------------------------------------------------
if [ "$rule" = dotnet ]; then
  deny "Every dotnet invocation in this repo runs serial: parallel MSBuild races here (MSB3030).

Add -m:1, or better, go through the validator:
  scripts/validate.sh bridge-build
  scripts/validate.sh bridge-tests      # run this leg ALONE, never alongside other legs"
fi

exit 0
