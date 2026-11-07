# AGENTS.md

## Purpose

`spirectl` is a developer automation and AI-integration toolchain for Slay the Spire 2. It spans a Rust CLI, a .NET bridge mod, protobuf contracts, helper tooling, fixtures, docs, and an npm wrapper.

Use this file as the always-loaded rule set. For detail, read targeted docs instead of loading broad project history.

## Read First

Start with [docs/agent-quickstart.md](docs/agent-quickstart.md). It is the context-budgeted entrypoint for agent sessions.

Then read only the relevant subsystem map under [docs/maps/](docs/maps/):

| Work area | Start here |
| --- | --- |
| CLI UX, config, command output, completion | `cli/`, `docs/maps/cli.md` |
| Runtime state and semantic actions | `bridge-mod/`, `docs/maps/runtime-state.md`, `docs/maps/runtime-actions.md` |
| Static code/resource inspection | `dotnet-tools/`, `docs/maps/static-inspection.md` |
| Protobuf/API contracts | `proto/` |
| npm wrapper and AI surfaces | `npm-wrapper/`, `docs/maps/ai-surface.md` |
| Tests, fixtures, external runners | `tests/`, `fixtures/`, `docs/maps/test-runner.md` |
| Docs | `docs/` |

Use [docs/capabilities.md](docs/capabilities.md), [docs/known-gaps.md](docs/known-gaps.md), and [docs/architecture.md](docs/architecture.md) only when shipped-capability, limitation, or cross-component architecture context is needed.

## Non-Negotiable Design Rules

- Prefer observable, screen-aware state. Default state should expose what a player can reasonably know plus stable machine IDs; debug internals belong behind explicit dev/debug surfaces.
- Prefer semantic actions over raw input. Raw input must be clearly separated and mode-gated.
- Keep machine IDs stable for a run or screen instance where possible; do not rely on indices when stable IDs exist.
- Treat multiplayer as first-class. Do not add single-player-only abstractions that would later need breaking changes.
- Prefer structured output. Major commands should support `--json`, reliable exit codes, and script-friendly results.
- Keep behavior safe by default. Do not silently mutate original game files or machine-specific installs.
- Treat protobuf as a core API surface: evolve additively, never reuse field numbers, and update CLI/docs/tests when contracts change.
- Keep authored fixtures/tests diff-friendly and YAML-first where applicable.

## Modes And Safety

- **Normal:** safe inspection, semantic actions, non-destructive code inspection.
- **Dev:** fixtures, assertions, logs, traces, screenshots, deploy/restart/verify workflows, and explicit omniscient inspection for validation.
- **Dangerous:** raw input, destructive resets, brittle file mutation helpers. Dangerous behavior must be explicit in naming/help text.

### Live bridge is always available

Always assume a live bridge is running and reachable: prefer validating against the live host (state RPCs, asset extraction, screenshots) over mocks or fixtures alone. Assume you may freely restart the game and redeploy the bridge mod to set up or reset state — `./scripts/build-fixture.sh <fixture>` rebuilds the CLI, installs the bridge, launches the game, and loads a fixture in one step. Do not treat a missing live host as a blocker; bring one up.

Machine-specific STS2 install paths, assemblies, resources, and mods paths belong in ignored local config such as `sts2.local.yaml`, not committed docs or source comments. For static inspection and live host integration, prefer configured authoritative roots over copied repo-local or sibling `libs/` folders.

## Coding Workflow

When making changes:

1. Identify the correct component before editing.
2. Inspect nearby code and existing patterns before changing architecture.
3. Update contracts before or alongside command UX when the API changes.
4. Add or update focused tests for behavioral, contract, or user-facing changes.
5. Update docs/examples/help when user-facing behavior changes.
6. Run focused validation that matches the changed surface before claiming completion. Prefer the smallest meaningful repo-owned check first, then broader checks only when the change warrants them.
7. Report concise validation summaries, including any command failures or environment blockers.
8. Avoid mixing unrelated concerns in one change.
9. Do not revert or overwrite unrelated user changes.
10. Do not use git worktrees unless explicitly requested.

Do not:

- add a command without structured output support;
- add state fields without considering schema stability;
- add multiplayer-blind abstractions;
- silently mutate game files;
- bury important behavior in undocumented magic.

## Commits

Full rules, and the release runbook, in [docs/commit-and-release.md](docs/commit-and-release.md).

- **Commit your finished work** — don't leave it uncommitted or ask whether to. Never `git push`, and
  never create a tag: both are the maintainer's call.
- On `main`, `scripts/githooks/commit-msg` enforces the format. **Feature branches are unchecked**;
  `main` gets one squash commit per change.

  ```
  type(scope)!: imperative subject, <=72 chars, no trailing period

  Body: what changed and why, wrapped at <=100. Optional.

  Changelog: one sentence an integrator would read — feat/fix/perf only, or `none`
  ```

- Types: `feat fix perf refactor docs test build ci chore`. Only the first three need a `Changelog:`
  trailer, and internal work is almost always one of the other six. Retiring a flag or an experiment
  is `refactor`, not `cleanup`. A break to the CLI, the npm wrapper or `Spirectl.Sts2` needs `!` and
  a `BREAKING CHANGE:` footer.
- `git log priv` is a **frozen pre-publication archive** with an unrelated history and an older mixed
  style. Never copy its conventions, and never commit to it.

## Tooling Feedback Loop

If a missing or weak tool blocks, slows, or weakens validation, record it with:

```bash
scripts/tool-improvement.sh add ...
```

This is required before claiming completion when local tooling cannot set up state, keep a process alive, expose enough diagnostics, provide structured output, or automate a required check. Mention the entry in the final answer.

## Documentation Expectations

New user-facing capabilities should usually include:

- CLI help text or `inspect` exposure where relevant;
- docs or test examples;
- JSON output shape documentation when nontrivial.

Use [docs/cli.md](docs/cli.md), [docs/testing.md](docs/testing.md), and [docs/ai-skill.md](docs/ai-skill.md) only when command, verification, or AI-surface detail is not already available through `inspect`.

## Game Internals In Committed Files

This repo is published. Keep the game's own code out of git history.

Do not commit **game code**: decompiled or transcribed STS2 source, quoted method bodies, private
field/method names, or captured payloads carrying them. Do not commit a *narrative* reconstruction of how a
game class works internally either — a comment that walks through its logic is the same disclosure as
pasting it. Write that up in `.sts2/research/` (git-ignored via `.gitignore:19`, per checkout — start at its
`INDEX.md`) and leave only the engineering lesson in the committed file.

**Resource paths and node names ARE allowed where the shipped code needs them to work.** Addressing the
game's content is unavoidable; the precedent is this repo's own committed
`bridge-mod/src/Spirectl.Sts2/Data/base-game-encounter-visual-packages.json`, which carries the `res://`
scene paths, node selectors, and root type names the encounter-visual packages must address, and zero game
code. So a producer fold keyed by scene path, an asset key, or a node name a hook must find is fine — the
decompiled class that taught you the name is not. Keep it to what the code actually uses, prefer one table
over paths scattered through prose, and keep the *explanation* on the engineering side: what the node does
on screen and why this code treats it that way, not what the game's source does inside it.

`../sts2-couch-coop` carries the same rule and its own `.sts2/research/`; a note written in one repo's
folder is not visible from the other, and neither is visible from a `git worktree` (which gets its own empty
`.sts2/`). Always read and write at the real checkout path.

## Agent Config

Committed agent config lives at [`agents/`](agents/) and [`skills/`](skills/) (`/.claude`, `/.agents`, `/.codex`
and `/.dsh` are gitignored). Run `scripts/install-agent-config.sh` after cloning and in every new worktree: it
links them in for Claude Code, generates the Codex config under `.codex/`, generates the DeepSeek Harness overlay
under `.dsh/` (launch with `scripts/dsh`), and registers the PreToolUse guard for Claude and Codex —
`scripts/claude-guard-bash.sh`, self-tested by `scripts/test-claude-guard.sh`, which blocks the zero-match
`cargo test` filter and un-serialized dotnet builds. Add `--user` to mirror home-level config into `~/.codex/` and
the shared `~/.agents` root.

## Public and private material

This repository is public. Internal R&D lives in the private companion `tfoxy/spirectl-internal`, checked out at
`private/` (gitignored; `scripts/install-agent-config.sh` links every worktree's `private` to the main checkout's).
Maintainers with access: `git clone git@github.com:tfoxy/spirectl-internal.git private`.

- **Public:** code, tests, fixtures, wire and file-format specs, user, integration and contributor docs, and
  benchmarks with instructions to run them.
- **Private** (at `private/<original path>`): research reports and results, experiment write-ups, performance
  investigations and ledgers, handoffs, plans and roadmaps, design explorations, agent definitions and skills that
  encode project expertise, device and rig specifics.
- Source comments state the contract and the reason, not the measurement log: no device numbers, round or gate tags,
  or citations of private documents.
- Public commits, PR bodies, release notes and CI logs never quote or cite `private/` content.
- `scripts/check-public-tree.py` enforces a markdown allowlist and a denylist (validate, the pre-push
  hook, CI). A new public `.md` file must be added to `scripts/public-tree.conf`.

@private/AGENTS.md

## Context Hygiene

- Prefer focused `rg` searches and targeted file reads over broad scans.
- Use `jq` summaries for large JSON and diagnostics.
- Redirect verbose logs, screenshots, traces, and render artifacts to files.
- Report concise validation summaries unless the user asks for full logs.
- Start a fresh chat after large commits when prior exploration is no longer needed.

## Quick Routing Reminder

- Runtime state/action behavior -> `bridge-mod/`
- Command UX/output/completion/config -> `cli/`
- Static inspection/index/decompile -> `dotnet-tools/`
- Transport/schema definitions -> `proto/`
- JS convenience wrapper -> `npm-wrapper/`
- Example scenarios/tests -> `fixtures/`, `tests/`, `docs/`

If a change spans multiple areas, start from the contract and work outward.
