# Agent Quickstart

Tiny orientation for repo-local agent sessions. Read this before larger docs.

## Context Budget

- Prefer `rg`, focused file reads, and CLI self-description over broad document reads.
- Do not read all of `docs/current-status.md`, `docs/cli.md`, or spec docs unless the task needs roadmap/current-spec context.
- For command shape, start with `cargo run -p sts2 -- --json inspect commands` or the installed `sts2 --json inspect commands`.
- For AI/tool surfaces, use `inspect ai-tools`; for actions, use `inspect actions`; for examples, use `inspect examples`.
- Read only the relevant map under `docs/maps/`.

## First Choice By Task

- CLI UX, config, output, lifecycle: `cli/`, `docs/maps/cli.md`, and `docs/maps/test-runner.md` when runner-related.
- Runtime state/actions, bridge behavior: `bridge-mod/`, `docs/maps/runtime-state.md`, `docs/maps/runtime-actions.md`.
- Scene-state producers and the browser-mirror wire: `bridge-mod/src/Spirectl.Sts2/Live/Sts2GodotSceneStateProducer.cs`, `Sts2GodotSceneStateModels.cs`, `Sts2RuntimeSceneWatcher.cs`, and `docs/maps/runtime-state.md`. For a live host (needed by `dev screenshot` and any live producer measurement), `scripts/build-fixture.sh <fixture>` rebuilds the cli, installs the bridge, launches the game, and loads the fixture in one command. For Godot layout/rendering behavior, prefer local sibling references first: `../godot-4.5.1-stable` and `../godot-docs-4.5`.
- Static code/scene inspection: `dotnet-tools/`, `docs/maps/static-inspection.md`.
- Protobuf/API surface: `proto/`.
- npm wrapper or MCP: `npm-wrapper/`, `docs/maps/ai-surface.md`.
- Tests/fixtures/docs: `tests/`, `fixtures/`, `docs/testing.md` only when needed.

- Installing the CLI: `scripts/install-cli.sh` builds the release binary and links it as `sts2` in `${PREFIX:-$HOME/.local/bin}` (`--debug`, `--print-path`, `--force`); use the release build for anything that polls the CLI in a loop.
- Config is found by walking up from the working directory (stopping at a `.git` boundary), so `sts2` works from a subdirectory; `SPIRECTL_CONFIG_DIR` overrides it and `sts2 --json config resolve` prints the resolved absolute install paths.

## Local Toolchain Outputs

Use `sts2 --json toolchain info` to confirm the managed toolchain root, and `sts2 project recover --kind decompile|recovered-project|all` to create or refresh repo-local outputs. Prefer `sts2 code ...` and `sts2 assets ...` for structured lookup first; after they narrow the target, `.sts2/toolchain/decompile/sts2` is useful for browsing decompiled game sources, and `.sts2/toolchain/recovered-project` is useful for browsing recovered game resources, including scenes.

## When To Read Larger Docs

- `docs/current-status.md`: use for current spec/roadmap or shipped capability questions.
- `docs/known-gaps.md`: use when deciding whether missing behavior is intentional.
- `docs/architecture.md`: use for cross-component architecture changes.
- `docs/cli.md`: use for user-facing command docs after inspecting the live command metadata.

## Verification

Prefer targeted checks first, then broader baselines when the touched surface warrants it:

```bash
scripts/validate.sh cargo-test-filter --package sts2 --test <integration-test> --filter <test-name> --json
scripts/validate.sh cargo-unit-test-filter --package sts2 --filter <unit-test-filter> --json
scripts/validate.sh cli-tests --json
cargo test -p sts2 --test <relevant-test>
cargo test -p sts2
scripts/validate.sh bridge-tests --json
scripts/verify_parallel.sh --json
scripts/validate.sh npm-wrapper-tests --json
scripts/validate.sh bridge-live-host-tests --filter FullyQualifiedName~MapScreenInspector --json
```

Use `scripts/validate.sh cargo-test-filter --package sts2 --test <integration-test> --filter <test-name> --json` for focused Rust integration-test filters so zero matched tests fail explicitly; use `scripts/validate.sh cargo-unit-test-filter --package sts2 --filter <unit-test-filter> --json` for package/unit tests that live under `src/lib.rs` rather than an integration target. Raw `cargo test -p sts2 --test <relevant-test>` remains the broad integration-test command. Use `scripts/validate.sh dotnet-format --include <bridge path> --json` for selected C# formatting checks, and `scripts/validate.sh rust-proto-selected --path <proto-or-rust path> --json` when unrelated dirty Rust/npm edits would make a normal workspace build noisy. Use `scripts/validate.sh bridge-live-host-tests --filter FullyQualifiedName~MapScreenInspector --json` only when you explicitly need live-host-gated .NET coverage (`RunSts2LiveHostTests=true` / `#if ENABLE_STS2_LIVE_HOST`). For a live bridge readiness check, run `cargo run -p sts2 -- --json game bridge-health`.

Report only high-signal test output in conversation unless the user asks for full logs.
