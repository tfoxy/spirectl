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
| Docs and roadmap context | `docs/` |

Use [docs/current-status.md](docs/current-status.md), [docs/known-gaps.md](docs/known-gaps.md), and [docs/architecture.md](docs/architecture.md) only when roadmap, shipped-capability, or cross-component architecture context is needed.

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

## Downstream Consumers

This repo sits in a three-way split. Know which side of it you are on before adding anything.

| Repo | Owns |
| --- | --- |
| `spirectl` (here) | reusable STS2 tooling: runtime state, semantic actions, scene-state producers, assets, fixtures/scenarios, snapshots, the `sts2` CLI |
| `../godot-scene-web` | the generic Godot-scene→web renderer (parser, layout, DOM/CSS, canvas, WebGL/WebGPU). No STS2 or product concepts belong in it |
| `../sts2-couch-coop` | the co-op product: browser DTOs, URL layout, frontend UX, co-op behavior. It may not reimplement anything the other two own |

Two consequences that are easy to miss:

- **Couch-coop aliases this repo's TypeScript source, not a built package.** Its `frontend/vite.config.ts` maps
  `@spirectl/presentation/render` straight at `presentation/web/src/render/` (and `@godot-scene-web/*` at that
  repo's `packages/*/src`). There is no npm link, no workspace and no build step, so an edit here is live in its
  dev server immediately — and this checkout left on a branch silently changes what every agent working there is
  testing against. **Leave this checkout on clean `main` when you finish.**
- **`./render` + `./spine` are the whole TypeScript contract.** `@spirectl/presentation` exports exactly
  two subpaths. `./render` is the render vocabulary — six symbols: `applyAnimationBinding`,
  `ensureAnimationStyles`, `PresentationAnimationBinding`, `PresentationAnimationOptions`,
  `DEFAULT_BBCODE_TAGS`, `playZoneThreshold`. `./spine` is DOM-free Spine/geoclip clip parsing and
  frame sampling (`parseRasterSpineClip`/`sampleRasterSpineClip`, `parseGeoclip`/`sampleGeoclip`/
  `applyGeoclipVerts`, and their types) for a host that owns its own canvas, WebGL, and resource
  loading. Anything a mirror host needs beyond those it builds itself; do not grow this package back
  into a renderer.
- **A bridge change is two deploys, not one.** `sts2 game install-bridge` updates the bridge/CLI-facing copy;
  couch-coop's `scripts/build-local-mod.sh` updates the embedded copy the running game and browser mirror
  actually serve. They are separate assemblies, and `install-bridge` alone does not update what the mirror sees.

Shared contracts, co-owned and not unilaterally changeable: the scene-state DTOs in
`bridge-mod/src/Spirectl.Sts2/Live/Sts2GodotSceneStateModels.cs` mirror `../godot-scene-web/packages/core`
exactly; the `perf-report/1` envelope is specified in `../godot-scene-web/docs/perf-report-contract.md`; the
`./render` subpath above is the only TypeScript surface couch-coop imports from here.

Committed agent config lives at [`agents/`](agents/) and [`skills/`](skills/) (`/.claude`, `/.agents` and
`/.codex` are gitignored). Run `scripts/install-agent-config.sh` after cloning and in every new worktree: it
links them in for Claude Code, generates the Codex config under `.codex/`, and registers the PreToolUse guard
for both CLIs — `scripts/claude-guard-bash.sh`, self-tested by `scripts/test-claude-guard.sh`, which blocks the
zero-match `cargo test` filter and un-serialized dotnet builds. Add `--user` to mirror home-level config into
`~/.codex/`.

- Project memory is `.agents/memory/MEMORY.md` (in a worktree, a symlink to the main checkout's store). Read
  that index before non-trivial work; record what you learn with the `project-memory` skill.

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
