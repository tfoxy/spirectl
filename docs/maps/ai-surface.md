# AI Surface Map

See also: [current status](../current-status.md), [known gaps](../known-gaps.md), [AI skill](../ai-skill.md), [AI tool surface](../ai-tools.md), [CLI](../cli.md).

## Primary files

- `cli/src/lib.rs`
- `cli/src/automation_service.rs`
- `cli/src/skill.rs`
- `skills/spirectl/SKILL.md` plus lazy-loaded references under `skills/spirectl/references/`
- `npm-wrapper/src/index.js`
- `npm-wrapper/src/index.d.ts`
- `npm-wrapper/src/service-client.js`
- `npm-wrapper/src/mcp-server.js`
- `npm-wrapper/src/mcp-tool-executor.js`
- `npm-wrapper/bin/sts2-mcp.js`
- `npm-wrapper/README.md`

## Secondary files

- `npm-wrapper/bin/sts2.js`
- `npm-wrapper/src/resolve-binary.js`
- `docs/ai-skill.md`
- `cli/tests/snapshots/inspect-ai-tools.json`
- `npm-wrapper/test/client.test.js`
- `npm-wrapper/test/service-client.test.js`
- `npm-wrapper/test/mcp.test.js`
- `npm-wrapper/test/skill-packaging.test.js`

## Supported now

- `sts2 --json inspect ai-tools` is the authoritative catalog.
- `skills/spirectl/SKILL.md` is the repo-native router entrypoint for `npx skills add ...`; lazy-loaded reference files keep task-specific guidance context-light while remaining self-contained under one skill directory.
- `sts2 skill install` writes the checked-in `spirectl` skill directory into a project-local or explicit root without generating a second instruction set.
- The Node helper is thin and shells back to the CLI with `--json`.
- `sts2 service serve` now exposes the same CLI-owned catalog and supported automation flows over HTTP/JSON without inventing a second tool schema.
- The Node helper can now prefer that service through `serviceUrl` / `serviceToken` or `STS2_SERVICE_URL` / `STS2_SERVICE_TOKEN`, while keeping the same method names.
- `sts2-mcp` is still a thin stdio MCP adapter by default; explicit Streamable HTTP network MCP is supported with loopback defaults, bearer auth for non-loopback binds, and threat-model acknowledgement.
- Durable remote `test_run` jobs remain automation-service API calls over the CLI-owned Rust runner. The wrapper and MCP adapter can point at that service, and network MCP reuses the same durable job/artifact contract without introducing a JS runner.
- Success and expected CLI failures preserve the underlying CLI JSON payloads instead of inventing a second business schema.
- Runtime `state` payloads are passed through as the CLI emits them. Default `state` is now the current agent snapshot: downstream AI/MCP callers should prefer `actions[]` for executable semantic actions, `scene.items[]` / `scene.controls[]` for visible screen objects, `decision` for phase/action summary, and `actions[].stateRefs` / `scene.*[].actionIds` for joins. Use `state full` only for the expanded bridge-native compatibility payload.
- S87 multiplayer metadata is adapter-passthrough data, not adapter logic. AI/MCP callers should read `playerId`, `ownerPlayerId`, `isLocal`, `isHost`, `isRemote`, `hostPlayerId`, `localPlayerId`, `localPlayerRole`, `perspective`, and remote-orchestration capability fields from the CLI payload before acting. Remote-owned controls are actionable only when the current perspective and capability metadata report a real configured-client or host-mediated path.
- S86 non-combat groups are represented as typed action refs rather than choose-first recipes: reward/card, shop, rest-site, treasure/relic, map, and event-room workflows should call `client.act(...)`, MCP `act`, or CLI `sts2 act ...` with the advertised intent kind and arguments.
- CLI, wrapper, service, and MCP action failures preserve the same structured payloads, including `actionFailure.reasonCode` values such as `wrong-screen`, `wrong-player`, `not-visible`, `not-enabled`, `missing-hook`, `ambiguous-hook`, `stale-id`, `unsupported-perspective`, and `dangerous-mode-required`.
- `inspect_actions` also passes through `stateContract`, the CLI-owned machine guidance for typed section names, card overlay precedence, fallback `choose` limits, compatibility metadata fields, structured notice fields, and the rule that raw runtime scene-tree internals stay in dev `scene tree` / `scene node` / `scene children`.
- The current tool set covers repo-local setup/export helpers (`game_detect`, `assets_extract`, `assets_explain`, `assets_extract_batch`, `skill_install`), M28-M55 repo-local ownership/evidence/regression/debug/lifecycle/scenario tools (`toolchain_info`, `project_profile_*`, `game_close`, `log_health`, `diagnostics`, `http`, `http_wait`, `fetch`, `websocket`, `project_hook_*`, `inspect_viewport_presets`, `screenshot_diff`, `snapshot_export`, `snapshot_compare`, `code_hooks`, `code_hook_info`, `inspect_reference_topics`, `test_stress`, `debug_status`, `debug_session_start`, `debug_session_status`, `debug_session_end`, `debug_pause`, `debug_resume`, `debug_step`, `debug_wait`, `breakpoint_list`, `breakpoint_add`, `breakpoint_remove`, `scenario_export`, `scenario_load`), M62 developer hot-reload tools (`hot_reload_status`, `hot_reload`) for M57/M60 shell-supported projects, runtime state/actions/logs/assert/wait, the promoted lifecycle/dev subset, static inspection, and `test_run`.
- Checkpoint commands stay outside `inspect ai-tools` and are no longer active CLI commands after M59. Scenario export/load is included because it works with shareable `spirectl.scenario/v0` files, and current payloads preserve restore-support, validation, and optional multiplayer-restore JSON directly from the CLI. Authored `load_fixture` maps to `dev fixture load`; recorded fixture record/resume/status/clear remain CLI/operator-oriented local state unless their safety and semantics are explicitly promoted into the AI catalog later.

## Current limits

- The wrapper remains thin and CLI-first, but it no longer stays CLI-spawn-only: supported calls can target the automation service when configured, while preserving the same public API.
- The current replacement-ready catalog is intentionally selective, but it is now broad enough to support the documented thin MCP migration path.
- The automation service remains intentionally bounded: loopback-safe by default, bearer-gated plus acknowledgement-gated for non-loopback network MCP binds, no in-process TLS, and only remote `test_run` has first-class artifact download support. S92 durable job state and S93 network-native MCP both reuse the same CLI-owned catalog and service contracts.
- Lower-level or intentionally narrow surfaces such as `game install-bridge`, explicit forceful `game kill`, shell deploy/restart, source-editing helpers, `dev scene tree`, `dev scene node`, `dev scene children`, `dev scene set-visible`, and dangerous raw input still stay out of the AI catalog.
- Broader semantic gameplay coverage is still partial for combat redesign, multiplayer ownership hardening, and unmodeled/modded UI, but high-value non-combat verbs are now part of the shipped AI/MCP action surface. AI/MCP callers must follow compact `actions[]` entries instead of teaching `choose` for modeled first-party flows.
- S87 hardens ownership, but broad remote-client orchestration remains outside the shipped AI/MCP surface. Treat `wrong-player` and `unsupported-perspective` as actionable structured results, not transport failures; do not fall back to raw input to control a remote-owned player unless a separate dangerous-mode operator workflow explicitly asks for viewport input.
- For overlays, AI/MCP callers should read typed `cardOverlay` first. Passive card overlays keep the underlying typed screen authoritative, blocking overlays take precedence, unsupported overlays surface structured notices, and generic `choose` remains only a compatibility fallback for currently executable or unmodeled visible controls.
- The MCP adapter must not define its own state schema. If a typed state field, notice metadata field, or perspective field is missing from the MCP result, fix CLI passthrough or wrapper typing/tests rather than adding adapter-side remapping.

## If you change the AI surface, also update

- Command catalog and AI-tool catalog generation in `cli/src/lib.rs`
- Wrapper method mappings and argument translation in `npm-wrapper/src/index.js`
- Wrapper public types in `npm-wrapper/src/index.d.ts`
- MCP registration and catalog loading in `npm-wrapper/src/mcp-server.js`
- MCP execution/error preservation in `npm-wrapper/src/mcp-tool-executor.js`
- Skill install behavior in `cli/src/skill.rs`
- Skill packaging instructions in `skills/spirectl/SKILL.md` and `skills/spirectl/references/`
- Binary resolution and wrapper docs in `npm-wrapper/src/resolve-binary.js`, `npm-wrapper/bin/`, and `npm-wrapper/README.md`
- AI-surface docs in `docs/ai-tools.md`, `docs/ai-skill.md`, `docs/cli.md`, `docs/current-status.md`, `docs/known-gaps.md`, and `docs/mcp-migration.md`
- Snapshots/tests in `cli/tests/cli_snapshots.rs`, `npm-wrapper/test/client.test.js`, `npm-wrapper/test/mcp-tool-executor.test.js`, `npm-wrapper/test/mcp.test.js`, and `npm-wrapper/test/skill-packaging.test.js`
