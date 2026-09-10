# MCP Migration

This repo now treats `sts2-mcp` as the only supported repo-local MCP layer.

Use `sts2 --json inspect ai-tools` as the authoritative machine-facing catalog, use `sts2-mcp` only as a thin stdio adapter over that catalog, and use direct CLI JSON, the thin Node helper, or the CLI-owned [automation service](./automation-service.md) when MCP is unnecessary.

## Setup And Lifecycle

Recommended replacements:

- `game_detect`
- `game_info`
- `game_launch`
- `game_attach`
- `game_deploy`
- `toolchain_info`
- `project_profile_list`
- `project_profile_show`
- `project_profile_run`

Notes:

- `project_profile_*` is repo-authored lifecycle composition over existing CLI commands, not a second orchestration protocol
- `game install-bridge` stays outside the AI catalog on purpose; automation should prefer `game_deploy`

Verification commands:

```bash
sts2 --json inspect ai-tools
sts2 --json toolchain info
sts2 --json project profile list
sts2 --json game detect
```

## Runtime State, Actions, And Assertions

Recommended replacements:

- `state`
- `inspect_actions`
- `act`
- `logs`
- `log_health`
- `diagnostics`
- `wait_for`
- `assert`
- `load_fixture`
- `screenshot`
- `screenshot_diff`
- `snapshot_export`
- `snapshot_compare`
- `inspect_viewport_presets`
- `test_run`
- `test_stress`

Notes:

- Local stdio MCP is the default safe migration path. Run `sts2-mcp` locally when the AI client and game/toolchain are on the same machine; it loads the same `inspect ai-tools` catalog and preserves CLI JSON results without exposing a network listener.
- Network MCP is the explicit remote/team option. Use `sts2 service serve --mcp-mode network` or the wrapper HTTP transport only when operators need a reachable Streamable HTTP MCP endpoint, and keep the bind/auth/TLS threat model in service configuration rather than downstream adapter code.
- Both MCP postures use the same catalog and service contracts. Do not fork action names, state schemas, test-run semantics, or artifact metadata during migration.
- success payloads remain the unchanged CLI JSON envelopes
- expected CLI failures remain structured CLI failures; adapter-only failures stay adapter-only
- `state` now preserves the CLI's current agent snapshot directly. Prefer `actions[]` for executable semantic actions, `scene.items[]` / `scene.controls[]` for visible non-combat objects, and `actions[].stateRefs` / `scene.*[].actionIds` for joins. `choices[]`, `availableActions[]`, and per-screen top-level sections are intentionally absent from default state; use `state full` only for migration or bridge extraction debugging. Script and adapter consumers can use `state screen` for only screen classification, `state actions` for the action/ref subset, and `state full --section eventRoom` for the bounded legacy event-room text surface.
- S85/S86 current broke old choose-heavy automation intentionally; state makes that the default CLI shape. MCP clients should execute the advertised `actions[]` intent action for reward/card, shop, rest-site, treasure/relic, map, and event-room controls, and use fallback choose only for generic, modded, or unmodeled visible choices exposed through `fallbackChoices[]`.
- `inspect_actions` also preserves CLI-owned `stateContract` metadata naming the typed sections, compatibility fields, structured notice fields, and the rule that raw runtime scene-tree internals belong to `dev scene tree` / `dev scene node` / `dev scene children`, not default `state`. Use `sts2 --json inspect actions --offline` or `sts2 --config tests/sts2.mock.yaml --json inspect actions` when validating static metadata JSON without a live host.
- structured `notices[]` are also passed through directly, including `path`, `severity`, `source`, `stability`, and `perspective`; MCP clients should not depend on an adapter-specific notice schema
- structured action failures are passed through directly, including `actionFailure.reasonCode` values such as `wrong-screen`, `wrong-player`, `not-visible`, `not-enabled`, `missing-hook`, `ambiguous-hook`, `stale-id`, `unsupported-perspective`, and `dangerous-mode-required`, plus `screen`, `playerId`, `perspective`, and checked hook paths when relevant
- multiplayer ownership metadata is also passthrough. MCP clients should read `ownerPlayerId`, requested/resolved `playerId`, `isLocal`, `isHost`, `isRemote`, `hostPlayerId`, `localPlayerId`, selected `perspective`, and remote-orchestration capability fields before calling `act`. A local-only or unsupported capability is not a remote-control path; preserve wrong-player and unsupported-perspective failures for the caller instead of retrying through a fake remote client.
- when `STS2_SERVICE_URL` / `STS2_SERVICE_TOKEN` are set, stdio `sts2-mcp` can use the automation service as a backend without changing its local process model
- durable remote `test_run` jobs still use the CLI-owned runner behind the automation service; network MCP reuses that durable job flow and artifact metadata instead of adding a second runner protocol
- `diagnostics`, `screenshot_diff`, `snapshot_export`, and `snapshot_compare` are evidence surfaces, not primary state APIs
- dangerous raw input remains excluded from the AI catalog

Verification commands:

```bash
sts2 --json state
sts2 --json state screen
sts2 --json state actions
sts2 --json state full --section eventRoom
sts2 --json inspect actions
sts2 --json inspect actions --offline
sts2 --config tests/sts2.mock.yaml --json inspect actions
sts2 --json inspect viewport-presets
sts2 --json test run tests/scenarios/visual-main-menu.sts2.yaml
sts2 --json dev snapshot compare --spec tests/snapshots/main-menu.sts2.snapshot.yaml --baseline tests/snapshots/baselines/main-menu
sts2 --json test stress tests/scenarios/regression-main-menu.sts2.yaml --iterations 2
```

## External Probes And Repo Hooks

Recommended replacements:

- `http`
- `http_wait`
- `fetch`
- `websocket`
- `project_hook_list`
- `project_hook_show`
- `project_hook_run`

Notes:

- probe success is intentionally environment-dependent and stays marked `partial`
- `project_hook_*` executes only checked-in repo-authored hooks; it is not a plugin registry

Verification commands:

```bash
sts2 --json project hook list
sts2 --json project hook show repo-check
scripts/validate.sh npm-wrapper-tests --json
```

## Modding Discovery And Reference Workflows

Recommended replacements:

- `code_hooks`
- `code_hook_info`
- `inspect_reference_topics`
- plus the existing staged static-inspection commands such as `code_locate`, `code_describe`, `code_refs`, and `code_decompile`

Notes:

- `inspect_reference_topics` is a checked-in workflow catalog, not a search engine
- `code_hook_*` replaces the common legacy hook/signature lookup loop without claiming IDE-grade navigation

Verification commands:

```bash
sts2 --json inspect reference-topics
sts2 --json code hooks OnDeckChanged --assemblies-dir ./assemblies --resources-dir ./game-project
```

## Safe Delete Criteria

A downstream repo can safely delete its vendored legacy MCP tree when all of the following are true:

1. local MCP clients can use `sts2-mcp` or direct CLI JSON without any repo-local MCP logic fork
2. every required repo-local workflow maps to shipped `inspect ai-tools` entries or direct CLI commands
3. setup and docs no longer refer to the legacy MCP tree
4. CI or local smoke checks cover the chosen replacement path
5. the repo no longer depends on adapter-only schemas or hand-curated duplicate tool lists
