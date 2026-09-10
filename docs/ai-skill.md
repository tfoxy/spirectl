# AI Skill Overview

`spirectl` now exposes a practical AI-facing surface without replacing the CLI as the primary interface.

- The authoritative machine-facing catalog is `sts2 --json inspect ai-tools`.
- The repo-local adapter is `sts2-mcp`, implemented in `npm-wrapper/` as a stdio MCP server.
- The Node helper in `npm-wrapper/` exposes the same surface through `createSts2Client()` and `inspectAiTools()`.
- The optional remote backend is `sts2 service serve`, documented in [automation-service](./automation-service.md).

This spec keeps the design deliberately small:

- the CLI remains the source of truth for behavior and output contracts
- the MCP adapter loads the CLI catalog once at startup instead of maintaining a separate hand-written tool list
- tool calls flow back through the existing wrapper and CLI commands, whether the backend is the local CLI spawn path or the CLI-owned automation service
- successful tool results preserve the CLI JSON payload unchanged
- expected CLI failures preserve the original structured CLI payload instead of being translated into a new protocol

## Repo-Native Skill Install

`spirectl` ships a repo-native skill pack for ecosystems that install `SKILL.md` files directly from GitHub. The `spirectl` skill is a self-contained router with lazy-loaded references under `skills/spirectl/references/` for runtime, library integration, render/assets, testing/debugging, and static modding work.

Install the checked-in router from this repo with:

```bash
npx skills add <owner>/<repo> --skill spirectl
npx skills add https://github.com/<owner>/<repo>/tree/main/skills/spirectl
```

After install, the skill should prefer a local `sts2` binary when it already exists on `PATH`, then a repo-local Cargo workspace, then `npx @spirectl/sts2`. The skill remains CLI-first and treats `sts2-mcp` as optional secondary guidance rather than the primary entrypoint.

If you want local MCP setup after installing the skill, use:

```bash
sts2-mcp
# or, when only npx is available
npx -p @spirectl/sts2 sts2-mcp
```

Local stdio MCP is the default safe startup. For a network MCP endpoint, make the bind/auth posture explicit and keep service backend settings in environment or local config:

```bash
sts2 --json service serve \
  --listen 127.0.0.1:4317 \
  --auth-token local-dev-token \
  --job-store durable \
  --mcp-mode network \
  --mcp-listen 127.0.0.1:4318

STS2_SERVICE_URL=http://127.0.0.1:4317 \
STS2_SERVICE_TOKEN=local-dev-token \
npx -p @spirectl/sts2 sts2-mcp --transport http --listen 127.0.0.1:4318
```

Non-loopback binds require bearer auth and acknowledgement:

```bash
STS2_MCP_AUTH_TOKEN='replace-with-a-secret' \
npx -p @spirectl/sts2 sts2-mcp \
  --transport http \
  --listen 0.0.0.0:4318 \
  --auth-token "$STS2_MCP_AUTH_TOKEN" \
  --acknowledge-network-risk
```

The CLI now also exposes a first-party installer:

```bash
sts2 skill install
sts2 --json skill install
sts2 --json skill install --path ./vendor/skills
```

`sts2 skill install` writes the checked-in self-contained `spirectl` skill directory into the current project's `./.agents/skills/` directory by default, or under an explicit root passed with `--path`.

## What Exists Now

- `--json` is a first-class global flag across the command surface.
- `inspect commands`, `inspect examples`, `inspect actions`, `inspect reference-topics`, `inspect viewport-presets`, and `inspect ai-tools` expose self-description from the CLI, including explicit visual preset catalogs and offline screenshot diff inputs.
- runtime state is structured, schema-versioned, perspective-aware, and includes additive presentation-grade combat fields for cards, piles, potions, relics, status effects, enemies, intents, generic asset keys, screen source metadata, and run breadcrumbs.
- bridge-backed commands use a shared protobuf contract and structured bridge errors.
- static inspection emits machine-readable envelopes plus stable symbol IDs for follow-up calls.
- `npm-wrapper/` now ships both a thin Node client and a thin stdio MCP adapter.
- `skills/spirectl/SKILL.md` now exposes a repo-native router with lazy-loaded references that can be installed directly from GitHub with `npx skills add` or copied together with `sts2 skill install`.

## Practical Usage

For agent-driven workflows, start here:

1. call `game_detect` first when install roots or live-bridge layout are uncertain
2. call `game_info`
3. call `game_launch`, `game_attach`, or `game_deploy` when you need lifecycle control rather than inspection only
4. call `state` and `inspect_actions`
5. call `act` only with currently executable IDs or kinds
6. use `logs`, `wait_for`, and `assert` for automation control flow
7. use `log_health`, `diagnostics`, `inspect_viewport_presets`, and `screenshot_diff` for evidence-oriented automation, including explicit preset catalogs and offline image-to-image comparisons when needed
8. use `toolchain_info`, `project_profile_*`, and `project_hook_*` for repo-local ownership and lifecycle composition
9. use `http`, `http_wait`, `fetch`, and `websocket` for repo-local probe workflows without inventing a second runner stack
10. use `load_fixture`, `screenshot`, `assets_extract`, `assets_explain`, and `assets_extract_batch` when you need deterministic setup, filesystem artifacts, composed combat-background diagnostics, or catalog-backed encounter render targets
11. use `scenario_export` / `scenario_load` for local repro anchors, reading `restoreSupport`, validation summaries, and any `multiplayerRestore` before continuing from restored state
12. use `hot_reload_status` / `hot_reload` only for explicit M57/M60 shell-supported dev projects; they do not deploy/restart shells, edit source, or reload arbitrary native mods
13. use `inspect_reference_topics`, `code_hooks`, and `code_hook_info` for the staged modding-discovery loop
14. use `skill_install` when another local repo needs the checked-in skill pack copied into `.agents/skills/`
15. use `test_run` with existing `.sts2.yaml` or `.sts2.json` files, scenario directories, or `inline` YAML/JSON bodies; on a network MCP/service backend, submit it as a durable remote job and preserve `remoteArtifacts[]` download metadata

UI-facing consumers should adapt from the generic `state` contract into their own DTOs. Asset references are opaque keys rather than URLs, and partial presentation data should be handled through structured `notices[]` fields such as `path`, `severity`, and `source`.

For S90 representative encounter reliability, start with `assets_explain` on `composed://encounters/kaiser_crab_boss/scene-package` to read catalog-backed render targets, selector diagnostics, bounds diagnostics, and unsupported notices. Feed the returned background, overlay, and part queries to `assets_extract_batch`, or run `scripts/validate.sh m78-live-encounter-artifacts --encounter kaiser_crab_boss --json` from the repo for the opt-in live artifact check. That helper writes only repo-local `.sts2/artifacts/...` outputs, does not mutate installed game files, and treats `environment_blocked` / `unavailable` as live-host infrastructure outcomes rather than validated images. No official STS2 assets should be committed in docs, examples, snapshots, or skill packaging.

The staged static-inspection loop is still the intended path:

1. `sts2 --json inspect reference-topics`
2. `sts2 --json code locate ...` when you still need a starting symbol family
3. `sts2 --json code hooks ...`
4. choose a returned stable method `id`
5. `sts2 --json code hook-info <id>`
6. `sts2 --json code describe method <id>`
7. `sts2 --json code refs method <id>` only when you need call-site context
8. `sts2 --json code decompile method <id>` only when metadata is not enough
9. add `--full` only when the exact-match metadata view is still insufficient

The static scene loop also remains staged and static-only:

1. `sts2 --json code scene-search ...`
2. choose a returned `sceneId` or exact node match
3. `sts2 --json code scene-tree <scene>` or `sts2 --json code scene-node <scene> <node-path>`
4. if present, feed `attachedScriptTypeId` into `sts2 --json code describe type ...` or pivot straight to `sts2 --json code hooks ...`
5. continue with `code hook-info`, `code refs`, `code derived`, or `code decompile` only when scene structure alone is not enough

Live runtime scene-tree inspection is a separate dev-only bridge surface through `sts2 --json dev scene tree [node-path]`, `sts2 --json dev scene node <node-path>`, and `sts2 --json dev scene children <node-path>`. It stays outside the AI-tool catalog in the current spec set.

## Current Limits

- The shipped semantic action set now includes S86 intent-first non-combat verbs for reward/card, shop, rest-site, treasure/relic, map, event-room, and run top-bar flows, alongside combat/lobby verbs such as `play-card`, `use-potion`, `end-turn`, `ready`, `unready`, and `select-character`. Examples include `claim-reward`, `skip-rewards`, `select-card`, `select-bundle`, `buy-card`, `buy-relic`, `buy-potion`, `remove-card`, `leave-shop`, `close-shop-inventory`, `rest`, `smith`, `use-rest-site-option`, `open-chest`, `take-relic`, `select-map-node`, `back-from-map`, `select-event-option`, `open-event-shop`, `use-crystal-sphere-control`, `toggle-map`, `toggle-deck`, `toggle-settings`, combat card-pile viewers `view-draw-pile` / `view-discard-pile` / `view-exhaust-pile` (open the in-game `NCardPileScreen`; the open pile is reported by `run.view.capstone.cardPileView`), and proceed verbs.
- For non-combat screens, read compact state first: use `actions[]` for executable intent actions, `scene.items[]` / `scene.controls[]` for visible objects, and `actions[].stateRefs` / `scene.*[].actionIds` for joins. Use `state full` only when a migration/debug workflow needs the old typed sections or compatibility `choices[]` / `availableActions[]`.
- Old choose-heavy examples are fallback guidance only. In compact state, call current entries from `actions[]` with their advertised kind and args; use fallback choose only for generic, modded, or unmodeled visible choices with no modeled first-party action.
- Preserve structured action failures across CLI, wrapper, service, and MCP flows. Read `actionFailure.reasonCode` values such as `wrong-screen`, `wrong-player`, `not-visible`, `not-enabled`, `missing-hook`, `ambiguous-hook`, `stale-id`, `unsupported-perspective`, and `dangerous-mode-required` plus `screen`, `playerId`, `perspective`, and checked hook paths; do not classify failures by prose text.
- For multiplayer workflows, read ownership and perspective metadata before acting: `playerId`, `ownerPlayerId`, `isLocal`, `isHost`, `isRemote`, `hostPlayerId`, `localPlayerId`, `localPlayerRole`, selected `perspective`, and any remote-orchestration capability block. Local-only degraded state is inspectable evidence, not proof that the local bridge can execute as every player; remote-owned actions require an explicit configured client or host-mediated capability.
- `inspect actions` includes `stateContract` guidance for these same sections and metadata fields. Preserve that guidance in wrapper/MCP passthroughs instead of inventing adapter-local state schemas.
- `load_fixture` stays recipe-based and intentionally bounded: the shipped executable fixture subset currently covers `screen: main-menu`, `combat`, `map`, `rewards`, `rest-site`, `event-room`, `treasure-room`, `relic-selection`, `shop`, `card-selection`, `simple-card-selection`, `deck-card-selection`, `bundle-selection`, `card-overlay`, `passive-card-overlay`, `screen: Screens.CharacterSelect.NCharacterSelectScreen` for start-run setup, and `screen: Screens.CharacterSelect.NMultiplayerLoadGameScreen` for authored load-run setup, including explicit `eventRoom.options`, opened treasure-room proceed flow, shop inventory/card-removal overrides, overlay entry state, ownership examples, and local-only degraded multiplayer reporting. Read `recipeReport.recipeName`, `appliedFields`, `inferredFields`, `omittedFields`, `unsupportedFields`, `degradedMultiplayerFields`, and `bridgeValidation` before deciding whether follow-up state/action assertions are meaningful.
- Scenario export/load is available through `scenario_export` and `scenario_load` for shareable sparse local repro artifacts. Payloads report current per-field restore support, expected/observed validation summaries, mismatch `supportClass` / `reasonCode` / `suggestedNextStep`, and optional `multiplayerRestore`; active multiplayer artifacts require explicit degraded-local opt-in and must report omitted remote clients. Exact sidecars, sparse recipes, recorded screen-entry fixtures, and native save-backed continuation are separate workflows; checkpoint commands are historical and not active AI tools.
- the AI catalog is still intentionally selective after M62: `game install-bridge`, shell deploy/restart, source-editing helpers, arbitrary native mod hot reload, `completion`, `completion install`, `inspect commands`, `inspect examples`, `dev scene tree`, `dev scene node`, `dev scene children`, `dev scene set-visible`, and other lower-level or dangerous helpers stay outside the standalone tool set.
- `sts2-mcp` is now the only supported repo-local MCP layer, but it stays optional secondary guidance over the CLI rather than a parallel product.
- dangerous raw input exists in the CLI as `sts2 --mode dangerous act mouse click ...`, but it stays outside the AI/MCP catalog.
- `sts2-mcp` stdio remains the local default; network MCP is explicit opt-in over Streamable HTTP with loopback defaults, auth/acknowledgement for public binds, and operator-owned TLS/reverse-proxy policy.

## Where To Go Next

- Tool-by-tool contract and usage notes: [AI Tool Surface](./ai-tools.md)
- CLI and output-shape background: [CLI](./cli.md)
- Adapter and Node wrapper usage: [npm-wrapper README](../npm-wrapper/README.md)
- Architectural rationale: [Architecture](./architecture.md)
- Downstream replacement path and safe-delete checklist: [MCP Migration](./mcp-migration.md)
