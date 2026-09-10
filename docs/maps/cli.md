# CLI Map

Use this map for Rust CLI UX, config, output, dispatch, and command implementation work.

## Entry Points

- `cli/src/lib.rs`: crate facade, module declarations, and public re-exports such as `Cli`, `run_cli`, `run_cli_streaming`, `AppError`, and `RenderedCommand`.
- `cli/src/main.rs`: binary entry point; parses `Cli`, chooses streaming vs one-shot execution, renders errors, and exits with the command status.

## Core CLI Modules

- `cli/src/cli_args.rs`: Clap command tree, command argument structs/enums, and CLI-mode argument helpers.
- `cli/src/config.rs`: `AppConfig`, YAML loading/merging, config provenance, and local discovered-game-path cache updates.
- `cli/src/config_resolve.rs`: `config resolve` — resolves config into absolute install paths with per-key provenance, reusing the `install_paths` resolvers.
- `cli/src/context.rs`: command runtime context, owned context snapshots, RPC timeout overrides, bridge-client construction, and the optional active `--instance` handle.
- `cli/src/instance.rs`: multi-instance support — `--instance <name>` resolution (deterministic socket/user-dir/mirror derivation, name validation, `auto` allocation), config overlay, isolated-build game mirror, and the `<instances.dir>/<name>/instance.json` registry. See the Multi-Instance section in `docs/cli.md`.
- `cli/src/error.rs`: `AppError` and structured CLI error payload constructors.
- `cli/src/output.rs`: `RenderedCommand`, structured rendering, success render helpers, and output write errors.
- `cli/src/commands.rs`: top-level dispatch, command handlers, usage tracking, dev/game/code/service wiring, and shared runtime helper JSON.
- `cli/src/inspect.rs`: command catalog, examples, schema helpers, and AI tool metadata/argument adaptation.
- `cli/src/state.rs`: state command execution, watch stream projection, current state JSON, and state schema JSON.
- `cli/src/models_ext.rs`: model-catalog loading (`load_presentation_models`) used by the state-action surface.
- `cli/src/tests.rs`: CLI-root unit tests that cover argument parsing, config loading, usage tracking, rendering helpers, and command adapters.

## Neighbor Modules

- `cli/src/assets.rs`, `cli/src/assets_render.rs`: asset catalog/resolve/extract commands and asset hydration.
- `cli/src/automation_service.rs`: long-running HTTP/JSON automation service used by `service serve`.
- `cli/src/completion.rs`: shell completion command generation and installation.
- `cli/src/lifecycle.rs`: game launch/attach/close/kill/deploy implementation used by game commands. Process matching consults the instance registry (`cli/src/instance.rs`) before scanning `/proc` for the configured executable, so `close`/`kill` still find a game launched from a different checkout or mirror; the executable scan remains the fallback.
- `cli/src/progress.rs`: process-global `--progress` sink. NDJSON phase lines on stderr for the long lifecycle commands; stdout stays the structured result.
- `cli/src/test_runner.rs`: YAML/JSON scenario test runner used by `test run` and `test stress`.

## Change Guidance

- Preserve `lib.rs` as a facade; add command behavior in the focused module that owns the subsystem.
- Keep command definitions declarative in `cli_args.rs` so Clap help and completions remain authoritative.
- For user-facing command changes, update inspect metadata in `inspect.rs`, docs/examples when needed, and targeted tests in `tests.rs` or subsystem tests.
- For behavior checks, start with `cargo test -p sts2 --lib`; use `scripts/validate.sh cli-tests --json` for broader CLI coverage.
