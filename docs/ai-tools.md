# AI Tool Surface

The AI-facing surface is intentionally thin and CLI-first.

- Authoritative catalog: `sts2 --json inspect ai-tools`
- Optional remote backend: [automation-service](./automation-service.md)
- Thin adapter: `npm --prefix npm-wrapper exec sts2-mcp`
- Thin Node helper: `createSts2Client()` plus `inspectAiTools()`

The CLI remains the source of truth for command behavior, output contracts, and help metadata. The adapters do not implement a second logic stack. `sts2-mcp` defaults to local stdio and supports explicit network MCP configuration, while the optional automation service exposes the same CLI-owned catalog and supported remote flows over HTTP/JSON.

## Result Contract

Tool results preserve the existing CLI JSON envelopes.

- Success: `structuredContent` is the exact CLI JSON payload, and `content[0].text` contains the same payload as formatted JSON text.
- Expected CLI failure: `isError: true` with `structuredContent` equal to `{ exitCode, argv, stderr, payload }` on the local CLI path, or the same structured service error payload when the automation service backend is in use.
- Adapter-only failure: `isError: true` with `structuredContent` equal to `{ code, message, argv, stdout, stderr }`, where `code` is currently `cli_spawn_failed` or `cli_protocol_error`.
- Network MCP uses the same result preservation rule over Streamable HTTP. It must not wrap successful CLI/service JSON in a second business schema, and it must preserve structured service errors for callers that need retry or legality decisions.
- Durable remote `test_run` results preserve runner summaries and add artifact metadata such as `remoteArtifacts[]` with service download URLs. MCP clients should keep that metadata intact instead of rewriting server-local paths.

## Stable And Partial Capabilities

Stable enough for agent use today:

- `game_detect`
- `game_info`
- `toolchain_info`
- `game_launch`
- `game_attach`
- `game_deploy`
- `game_close`
- `project_profile_list`
- `project_profile_show`
- `project_hook_list`
- `project_hook_show`
- `assets_extract`
- `assets_explain`
- `state`
- `inspect_actions`
- `logs`
- `log_health`
- `diagnostics`
- `console`
- `hot_reload_status`
- `hot_reload`
- `load_fixture`
- `skill_install`
- `screenshot`
- `inspect_viewport_presets`
- `screenshot_diff`
- `snapshot_export`
- `snapshot_compare`
- `wait_for`
- `assert`
- `code_locate`
- `code_describe`
- `code_refs`
- `code_derived`
- `code_decompile`
- `code_hooks`
- `code_hook_info`
- `code_scene_search`
- `code_scene_tree`
- `code_scene_node`
- `inspect_reference_topics`
- `test_run`
- `test_stress`
- `act` with combat/lobby verbs plus S86 non-combat intent verbs such as `claim-reward`, `select-card`, `select-bundle`, `buy-card`, `buy-relic`, `buy-potion`, `remove-card`, `leave-shop`, `rest`, `smith`, `open-chest`, `take-relic`, `select-map-node`, `back-from-map`, `select-event-option`, `open-event-shop`, `use-crystal-sphere-control`, and proceed/skip verbs when those actions are currently legal

Still partial:

- `scenario_export` and `scenario_load` are promoted AI tools for shareable sparse repro artifacts. Their payloads include current per-field `restoreSupport`, post-load expected/observed validation summaries, mismatch `supportClass` / `reasonCode` / `suggestedNextStep`, and optional `multiplayerRestore`; lobby multiplayer restore is supported as lobby-only, while active multiplayer artifacts require explicit `allowDegradedLocalMultiplayer` before local-only degraded restore may omit remote clients.
- `project_profile_run`, `project_hook_run`, `http`, `http_wait`, `fetch`, and `websocket` depend on repo-local files or external endpoints even though the adapter surface itself is now stable
- `debug_status`, `debug_session_start`, `debug_session_status`, `debug_session_end`, `debug_events`, `debug_pause`, `debug_resume`, `debug_step`, `debug_wait`, `breakpoint_list`, `breakpoint_add`, and `breakpoint_remove` are promoted AI tools, but they still depend on an attached live host that actually exposes the required debug hooks; unsupported/non-live hosts continue returning structured notices instead of guessed success, mutating debugger operations require the controller lease when ownership is enforced, and observer sessions are read-only
- `console` is promoted for explicit developer-console automation, but it is not a semantic action and should not replace `act`; safe console commands require normal mode by default, while `achievement`, `cloud`, and `unlock` require dangerous mode because they can mutate persisted files
- semantic gameplay coverage is now intent-first for the high-value S86 non-combat groups: reward/card, shop, rest-site, treasure/relic, map, and event-room. Generic `choose` remains fallback-only for generic, modded, or unmodeled visible controls with no modeled `preferredAction`.
- dangerous raw-input fallbacks exist separately in the CLI, but they intentionally stay out of this AI catalog as a non-goal

Deliberately excluded from the standalone AI catalog:

- `game install-bridge`: operators and scripted workflows should prefer `game_deploy` for the end-to-end repair/deploy path
- `game kill`: explicit force termination stays an operator-controlled CLI/wrapper command rather than a promoted AI tool
- `completion` and `completion install`: completion remains CLI/operator setup rather than AI-task orchestration
- `inspect commands` and `inspect examples`: keep `inspect ai-tools` as the single authoritative machine-facing catalog
- `dev scene tree`, `dev scene node`, `dev scene children`, and `dev scene set-visible`: live runtime scene inspection remains a separate dev-only surface
- `dev fixture record|resume|status|clear`: recorded fixtures are ignored local operator state under `.sts2/fixtures/current-screen/`, so they stay out of `inspect ai-tools`
- `dev checkpoint capture|resume|list|delete`: removed from the active CLI surface by M59; historical checkpoint RPC compatibility remains unsupported rather than promoted
- dangerous raw input such as `sts2 --mode dangerous act mouse click ...`

## Recommended Usage Patterns

- Prefer local stdio `sts2-mcp` for one-machine AI workflows. Choose network MCP only when a team or remote agent needs a reachable service endpoint, and configure bind address, bearer auth, TLS/reverse proxy, and threat-model acknowledgement explicitly.
- Filter discovery through `inspect ai-tools` / `GET /v0/ai-tools`; do not expose `inspect commands` wholesale as an MCP catalog. Dangerous raw input, force kill, completion setup, and dev scene internals remain excluded unless a future spec explicitly promotes them.
- Call `game_detect` before `game_info` when install roots or local bridge layout are uncertain.
- Call `game_info` first to discover transport, mock/live mode, and environment notices.
- Use `game_launch` when the game is not already running, `game_attach` when it is, `game_deploy` when you need the full local bridge+mod deploy flow, and `game_close` when an automation workflow needs a graceful bounded shutdown before a restart.
- Use `toolchain_info`, `project_profile_*`, and `project_hook_*` for repo-local lifecycle ownership instead of maintaining a second orchestration or hook protocol.
- Use `assets_extract` for repo-local artifact export from resources and supported virtual live assets; do not treat it as runtime scene inspection. Use `assets_explain` before changing composed background or encounter package extraction behavior when a visual looks incomplete, transparent-heavy, or misframed.
- Read `state` and `inspect_actions` before `act` so the agent uses current action IDs and legal action kinds instead of stale guesses.
- Use `log_health`, `diagnostics`, `inspect_viewport_presets`, `screenshot`, `screenshot_diff`, `snapshot_export`, and `snapshot_compare` for evidence-oriented automation rather than treating screenshots as the primary runtime API. `inspect_viewport_presets` can load explicit custom catalogs, and `screenshot_diff` supports both live capture and offline `actual` PNG comparison.
- Use `debug_status` before starting a debugger workflow so the agent can confirm whether the attached host actually exposes live debug hooks, whether another lease owner already exists, and what breakpoint registry already exists.
- Use `debug_session_start` with `role: "controller"` before `debug_pause`, `debug_resume`, `debug_step`, `debug_wait`, or any mutating `breakpoint_*` call when the workflow needs explicit ownership for mutating debugger operations.
- Use `debug_session_start` with `role: "observer"` before `debug_events` when a passive tool needs a replayable transcript without taking the controller lease.
- Use `debug_events` with `fromSequence` / `limit` for bounded replay and with `follow: true` plus `timeoutMs` for a finite live-ish poll. Preserve `retention`, `expired`, `overflow`, `nextSequence`, and `notices`; these fields are how event loss, retention overflow, unsupported hosts, disconnects, and dropped clients are made visible.
- Use `debug_wait` only after `debug_session_start` plus one or more relevant breakpoints; it is a debugger-oriented resume-until-hit helper, not a replacement for `wait_for` or `test_run`.
- Use `http`, `http_wait`, `fetch`, and `websocket` when repo-local validation should stay under the same CLI/test-runner surface as the rest of the workflow.
- Use `load_fixture` only with authored recipe files for deterministic setup; it maps to `sts2 dev fixture load`.
- Use `scenario_export` to create reviewable sparse local repro artifacts, and inspect `restoreSupport.fields[]` before assuming which state was exact, partial, inferred, omitted, unsupported, or degraded for local multiplayer. Use `scenario_load` to anchor a workflow from an exported `spirectl.scenario/v0` file, then check `validation.expectedSummary`, `validation.observedSummary`, mismatch `supportClass` / `reasonCode` / `suggestedNextStep`, and `multiplayerRestore` before continuing. Treat exact sidecars, sparse recipes, native save-backed continuation, and recorded fixture record/resume/status/clear as separate workflows; recorded fixtures are local operator CLI work, not AI tools.
- Use `hot_reload_status` and `hot_reload` only for M57/M60 shell-supported projects with explicit local project paths. These are developer reload controls, not deploy/restart/source-edit tools; failed reload payloads should be inspected for whether the previous generation remains active and whether a restart is required.
- Use `skill_install` only when another local repo needs the checked-in `spirectl` skill copied into its `.agents/skills/` tree.
- Prefer `inspect_reference_topics`, `code_hooks`, and `code_hook_info` for the staged modding-research loop, then continue with `code_locate`, `code_describe`, `code_refs`, `code_derived`, or `code_decompile` only when needed.
- Treat `code_scene_search`, `code_scene_tree`, and `code_scene_node` as static inspection over authored resources, not runtime scene-tree inspection.
- Use `test_run` with a checked-in `.sts2.yaml` or `.sts2.json` path for reusable workflows or `inline` for one-off generated YAML/JSON bodies. On network MCP/service backends, treat remote `test_run` as a durable job: submit, poll, then retrieve `remoteArtifacts[]` or the artifact index/download URLs.
- Use `test_stress` when a stable scenario should be replayed repeatedly under bounded failure tolerance instead of scripting a second runner loop outside the CLI.

Migration guidance for downstream repos that still vendor legacy MCP trees lives in [mcp-migration](./mcp-migration.md).

For remote access without inventing a second schema, use [automation-service](./automation-service.md). It keeps `GET /v0/ai-tools` aligned with `inspect ai-tools`, routes supported tool calls through the same CLI-owned helper logic, and adds async remote `test run` jobs with additive `remoteArtifacts[]` download metadata.

For the operator-facing leased-session debugger workflow behind the debug tools, see [debugging](./debugging.md).

## Debugger Sessions

The debugger surface is now explicitly lease-oriented rather than implicit best-effort mutation.

- `debug_status` reports current ownership through `sessionOwnership` plus `activeSession` metadata when a lease exists.
- `debug_session_start` returns a stable session id plus lease expiry metadata. Only one controller lease is supported at a time, while multiple observer sessions may attach for read-only monitoring.
- `debug_pause`, `debug_resume`, `debug_step`, `debug_wait`, `breakpoint_list`, `breakpoint_add`, and `breakpoint_remove` all accept an optional or required session id, depending on whether the underlying operation must enforce ownership. Observer sessions must not be used for mutation; preserve structured observer-mutation notices instead of retrying as raw input.
- `debug_events` returns a bounded replay envelope with `events`, `fromSequence`, `nextSequence`, `oldestRetainedSequence`, `newestSequence`, `retention.oldestSequence`, `retention.newestSequence`, `retention.limit`, `expired`, `overflow`, `follow`, `timedOut`, `timeoutMs`, and `notices`.
- `breakpoint_add` supports `kind: "match"` with the shared predicate grammar and `kind: "change"` for observed-value transitions. Breakpoints also report hit counts, auto-remove behavior, and the most recent observed JSON value when relevant.

## Tool Reference

### `game_info`

Purpose: discover repository/build metadata, transport state, and mock/live notices before deeper automation.

Maps to: `sts2 --json game info`

Main inputs: none

Output expectations: unchanged CLI JSON envelope from `game info`. Expect environment, transport, and version metadata rather than live gameplay state.

### `game_detect`

Purpose: probe configured-vs-detected install roots and the derived live-bridge layout before deeper setup or lifecycle calls.

Maps to: `sts2 --json game detect`

Main inputs: none

Output expectations: unchanged CLI `game detect` envelope. Expect `status`, `configuredPath`, `detectedPath`, `liveBridge`, and setup-oriented `notes`.

### `state`

Purpose: read the current observable game state plus stable IDs for follow-up inspection and actions.

Maps to: `sts2 --json state`

Main inputs: `perspective?`, `playerId?`

Output expectations: the canonical runtime state envelope — `schemaVersion: spirectl.state/v0`, `language`, `rootScene`, `characterSelect`, `run`. `rootScene` classifies the screen (`run` whenever a run exists, else `screens/main_menu` / `screens/character_select_screen` / `screens/multiplayer_load_game_screen`); everything in a run hangs off `run` (`run.players[]` with `creature`, `deck`, `relics`, `potions`, `overlays[]`, `combat`; `run.currentRoom` with the room's `combat`/`event`/`shop`/`restSite`/`treasure` block; `run.map`, `run.view`, `run.notices`). The pre-cutover top-level `screen`, `scene`, `combat`, `actions[]`, `fallbackChoices[]`, `choices[]`, `availableActions[]`, per-screen sections and the `view: full|screen|actions` selectors are GONE (current was deleted; schema is v0 again). State carries runtime facts, not presentation text — card/relic/intent display strings come from `models`. For the executable surface call `sts2 --json state actions`: `actions[].kind` + `actions[].args`, with `actions[].sourcePath` joining each action back to the state node it came from. In multiplayer, read `ownerPlayerId` on actions and `run.players[].isLocal` / `isHost` / `isRemote` before acting; ownership is otherwise structural (a card/potion/relic sits under its owning player).

### `game_launch`

Purpose: start the configured local game process and wait until the live bridge is attached.

Maps to: `sts2 --json game launch`

Main inputs: `timeoutMs?`, `intervalMs?`, `noDetachSession?`, `disableBackgroundThrottle?`, `verbose?`, `launchArgs?`

Output expectations: compact CLI `game launch` success envelope. Expect top-level `status`, compact `launch` process/endpoint metadata, `attachment.screen`, compact bridge version/build identity, `stability`, and `modLoadout`. Pass `--verbose` when diagnostics need the full launch command, resolved config, full `gameInfo`, supported action catalog, and recent logs. Failure envelopes remain diagnostic-rich. Temporary allowlist launches report immediate `modLoadout.restore` and, when settings changed, `modLoadout.postExitRestore`. If `game.modLoadout.enabled` omits `spirectlbridge`, attachment reports `status: "skipped"`.

### `game_attach`

Purpose: wait for a running game/bridge process to become reachable without launching it.

Maps to: `sts2 --json game attach`

Main inputs: `timeoutMs?`, `intervalMs?`

Output expectations: unchanged CLI `game attach` envelope. Expect endpoint metadata, attempts/elapsed timing, `gameInfo`, and the current `screen`.

### `game_deploy`

Purpose: deploy the bridge plus one authored mod project, optionally building, restarting, and verifying the result.

Maps to: `sts2 --json game deploy`

Main inputs: `path`, plus optional `build?`, `restart?`, `verify?`, `timeoutMs?`, and `intervalMs?`

Output expectations: unchanged CLI `game deploy` envelope. Expect `bridge`, `mod`, optional `build`, `restart`, and `verify` sections.

### `game_close`

Purpose: request a graceful, bounded local game shutdown without capturing or restoring runtime state.

Maps to: `sts2 --json game close`

Main inputs: `timeoutMs?`, `intervalMs?`

Output expectations: unchanged CLI `game close` envelope. Expect `command`, `strategy`, `matchedPids`, `stoppedPids`, `remainingPids`, optional `endpoint`, `elapsedMs`, `timeoutMs`, `success`, `bridgeEndpointDisappeared`, and `notices`. The live bridge close RPC defaults to 10 seconds so graceful shutdown has enough time before falling back.

`game kill` is intentionally not promoted. Force termination remains available through the CLI and Node helper for explicit operator workflows.

### `assets_extract`

Purpose: export matching repo-local game assets and supported virtual live assets to disk through the existing extraction flow.

Maps to: `sts2 --json assets extract`

Main inputs: `query`, plus optional `execution?`, `format?`, `gamePath?`, `resourcesDir?`, `modsDir?`, and `includeMods?`. `query` accepts filesystem/packed asset paths, direct `res://` resources, or substrings, plus typed virtual keys such as `model://characters/ironclad/visuals`, `model://characters/ironclad/iconOutline`, `model://characters/ironclad/characterSelectBg`, `model://relics/burning-blood/bigIcon`, and encounter render targets such as `composed://encounters/kaiser_crab_boss/background/image`, `composed://encounters/kaiser_crab_boss/visual-state/rocket-charge-up/overlay/image`, and `composed://encounters/kaiser_crab_boss/visual-part/rocket/state/rocket-charge-up/image`. Character-select backgrounds may use `model://characters/<id>/characterSelectBg` or a direct scene path such as `res://scenes/screens/char_select/char_select_bg_defect.tscn`.

Output expectations: unchanged CLI `assets extract` envelope. Expect `query`, `executionMode`, `outputDir`, `exportCount`, and per-match `exports[]` metadata with output paths and provenance. Font files export as `artifactKind: "font"` with raw bytes and `font/*` content types.

### `assets_explain`

Purpose: explain live composed combat-background and encounter scene-package extraction plans without writing artifacts.

Maps to: `sts2 --json assets explain`

Main inputs: `query`, plus optional `execution?`, `gamePath?`, `resourcesDir?`, `modsDir?`, and `includeMods?`. Supported query forms are `composed://combat-background/<id>/image` and `composed://encounters/<id>/scene-package` with `execution: "live"`.

Output expectations: CLI `assets explain` envelope. Combat-background explanations include root scene metadata, placeholders, discovered layer groups, selected layer paths, texture refs, bounds, render notes, warnings, and best-effort active-scene observation under `explanation`. Encounter scene-package explanations use `explanationKind: "encounter-scene-package"` and include viewport, runtime-resolved camera scale/offset, background metadata, logical actors, visual parts, states, transitions, render target queries, selector diagnostics, bounds diagnostics, and notices. Failed live encounter renders may also carry optional `renderDiagnostics` or `error.details.diagnostic` evidence with request/render target ids, root node path/type, resolved selectors, hidden/kept part ids, viewport size, frame position/scale, `_Ready` and Spine preview hook status, capture timing notes, alpha evidence, and RGB evidence including whether nonzero RGB existed before alpha normalization. These fields are provisional where marked and base-game catalog backed in current.

### `assets_extract_batch`

Purpose: run manifest-driven asset extraction while preserving per-request metadata, partial failures, and bridge diagnostics.

Maps to: `sts2 --json assets extract-batch`

Main inputs: `manifest`, plus optional `output?`, `execution?`, `format?`, `gamePath?`, `resourcesDir?`, `modsDir?`, `includeMods?`, `dryRun?`, and `failFast?`.

Output expectations: CLI `assets extract-batch` envelope. Expect aggregate `status`, `outputDir`, `results[]`, per-request `exports[]`, `artifactChecks`, `notices`, and structured `error` payloads. For failed live encounter background, overlay, or part requests, bridge-provided render evidence is additive and may appear as `exports[].renderDiagnostics[]` or `results[].error.details.diagnostic`; AI-tool consumers should preserve those objects intact so selector, frame, hook, timing, alpha, and RGB evidence survives automation-service, npm, MCP, and validator layers. Unsupported encounters remain structured notices or failures; do not fabricate scene packages or collapse them into generic no-match results.

### `inspect_actions`

Purpose: learn bridge-known action kinds and the actions that are currently executable on the active screen.

Maps to: `sts2 --json inspect actions`

Main inputs: none

Output expectations: unchanged CLI `inspect actions` envelope plus a small `stateContract` guidance block. Expect `screen`, `resolvedPerspective`, `notices`, `supportedActions`, and `availableActions`. Current `availableActions` is a passthrough executable-action list with current metadata when the bridge knows it; it is not a replacement schema for the typed state sections. `stateContract.typedNonCombatSections` names the preferred S83 read sections, `stateContract.typedOverlaySections` names `cardOverlay`, `stateContract.typedOverlaySectionDetails.cardOverlay` describes the typed partial detail/breadcrumb/overlay-policy/fallback-control payload, `stateContract.overlayPolicy` explains blocking/passive/unsupported overlay precedence, `stateContract.fallbackChoosePolicy` keeps generic `choose` limited to currently executable compatibility or unmodeled visible controls, `stateContract.compatibilityMetadata` names the compatibility fields (`choiceKind`, `intentKind`, `ownerPlayerId`, `perspective`, `preferredAction`), and `stateContract.noticeFields` names the structured notice fields callers should preserve.

S85/S86 current is a breaking action/choice contract. AI callers should treat `preferredAction` as the primary bridge from typed state to intent-first verbs. If a visible typed choice has `preferredAction`, execute that action kind with its arguments; use fallback choose only for generic, modded, or unmodeled visible choices without a modeled first-party action.

S87 adds a multiplayer ownership guarantee on top of that contract. If `act` is called with a requested `playerId` that does not own the control or cannot be mediated by the current bridge/client, preserve the structured `actionFailure` payload and handle `wrong-player` or `unsupported-perspective` directly. Do not treat a local bridge as a proxy for independent remote clients unless the capability metadata says an explicit configured client owns that player.

### `act`

Purpose: perform a semantic gameplay or lobby action without falling back to raw input.

Maps to: `sts2 --json act ...` for semantic action kinds, including S86 non-combat verbs and fallback `choose`.

Main inputs: `kind`, plus action-specific IDs such as `choiceId?`, `mapNodeId?`, `cardId?`, `potionId?`, `targetId?`, `characterId?`, `rewardId?`, `shopItemId?`, `restOptionId?`, `relicId?`, `eventOptionId?`, and `controlId?`.

Supported kinds include `play-card`, `use-potion`, `select-map-node`, `back-from-map`, `claim-reward`, `skip-rewards`, `select-card`, `skip-card-selection`, `select-bundle`, shop buy/remove/leave/close verbs, rest-site verbs, treasure/relic verbs, event-room verbs, lobby verbs, and fallback `choose`.

For overlay flows, call `state` first and prefer typed `cardOverlay` data plus any `preferredAction` metadata. Passive overlays retain the underlying active screen, blocking overlays take precedence, unsupported overlays emit notices, and raw scene-tree internals remain dev-only.

Output expectations: unchanged CLI action result envelope. Expect structured acceptance or failure details from the runtime. Failure payloads preserve `actionFailure`, stable `reasonCode` values such as `wrong-screen`, `wrong-player`, `not-visible`, `not-enabled`, `missing-hook`, `ambiguous-hook`, `stale-id`, `unsupported-perspective`, and `dangerous-mode-required`, plus `screen`, `playerId`, `perspective`, and checked hook paths when relevant. Do not infer legality from prose messages.

Dangerous raw-input fallbacks such as `sts2 --mode dangerous act mouse click ...` are intentionally excluded from this tool surface.

### `console`

Purpose: execute one explicit STS2 in-game developer console command through the bridge.

Maps to: `sts2 --json dev console <command> [args...]` or `sts2 --mode dangerous --json dev console achievement|cloud|unlock ...`

Main inputs: `command`, optional `args`, optional `mode` (`dangerous` only for `achievement`, `cloud`, or `unlock`).

Output expectations: unchanged CLI console envelope with `requestId`, `command`, `args`, canonical `line`, `accepted`, `success`, `output`, `outputLines`, `source`, `provisional`, and `notices`. Console-level rejection exits nonzero with `console_command_rejected` while preserving the console response payload.

This is developer automation, not a semantic action surface and not default state. Prefer `act` for player-visible semantic actions; use `console` for explicit setup/debug commands such as `help`, `help draw`, `draw 3`, or `die`.

### `logs`

Purpose: fetch recent bridge/runtime logs for automation and debugging.

Maps to: `sts2 --json dev logs`

Main inputs: `limit?`, `level?`, `target?`

Output expectations: unchanged CLI log envelope with bounded recent entries and filter metadata.

### `load_fixture`

Purpose: apply one authored fixture recipe file through the live bridge for deterministic setup.

Maps to: `sts2 --json dev fixture load`

Main inputs: `path`

Output expectations: unchanged CLI load-fixture envelope. Expect canonicalized fixture metadata plus the bridge-backed load result.

Current shipped subset: executable fixture recipes currently cover `screen: main-menu`, `combat`, `map`, `rewards`, `rest-site`, `event-room`, `treasure-room`, `relic-selection`, `shop`, `card-selection`, `simple-card-selection`, `deck-card-selection`, `bundle-selection`, `screen: Screens.CharacterSelect.NCharacterSelectScreen` for start-run setup, and `screen: Screens.CharacterSelect.NMultiplayerLoadGameScreen` for authored load-run setup, including explicit `eventRoom.options`, opened treasure-room proceed flow, and shop inventory/card-removal overrides. Inline fixture bodies remain out of scope for fixture loading. Local checkpoints shipped as M52-M55 experiments, shareable scenario export/load shipped in M53, and M59 replaces the active checkpoint command surface with recorded screen-entry fixtures. Recorded fixture record/resume/status/clear stay out of the AI tool catalog unless explicitly promoted later. Unstructured arbitrary runtime memory editing remains out of scope.

### `hot_reload_status`

Purpose: inspect the running shell status for one M57/M60 shell-supported hot-reload project.

Maps to: `sts2 --json dev mod-reload status --project <dir>`

Main inputs: `project`

Output expectations: unchanged CLI hot-reload status envelope. Expect project metadata, shell support, active generation, last reload report, restart-required state, and bridge notices when available.

This tool is developer-only and requires a project that advertises the supported shell protocol through `sts2.hot-reload.yaml`. It does not deploy, restart, or edit source files.

### `hot_reload`

Purpose: optionally build reloadable logic and request a live shell reload for one M57/M60 shell-supported project.

Maps to: `sts2 --json dev mod-reload --project <dir> [--build] [--wait]`

Main inputs: `project`, plus optional `build?`, `wait?`, `timeoutMs?`, and `intervalMs?`

Output expectations: unchanged CLI reload envelope. Expect request metadata, optional build output, shell generation/report details, and structured failure payloads that distinguish whether the previous generation is still active and whether restart is required.

This tool remains bounded to the supported shell architecture. It does not expose arbitrary native mod hot reload, shell deploy/restart, source editing, or game-file mutation.

### `scenario_export`

Purpose: export the attached local state as a sparse, reviewable `spirectl.scenario/v0` YAML artifact.

Maps to: `sts2 --json dev scenario export`

Main inputs: `output`, plus optional `includeExact`

Output expectations: unchanged CLI scenario-export envelope. Expect `path`, optional opaque `exactBundlePath`, optional `exactBundleKind` (`save-backed` or `fixture-backed`), `restoreQuality`, field-level `restoreSupport`, captured `screen`, source metadata, and notices.

### `scenario_load`

Purpose: restore one exported `spirectl.scenario/v0` artifact through the bridge before continuing an automation or repro workflow.

Maps to: `sts2 --json dev scenario load`

Main inputs: `path`, plus optional `restart`, `timeoutMs`, `intervalMs`, and `allowDegradedLocalMultiplayer`

Output expectations: unchanged CLI scenario-load envelope. Expect `restoreQuality`, `exactBundleUsed`, `sparseFallbackUsed`, `screen`, `validation.expectedSummary`, `validation.observedSummary`, `validation.mismatches[]` with expected/observed summaries plus `supportClass`, `reasonCode`, and `suggestedNextStep`, optional `multiplayerRestore`, optional `bridgeVerification`, restart metadata when requested, and notices.

Scenario restore is local and explicit by design. Exact sidecars are optional opaque data, validated by hash and size on load, and do not turn scenarios into generic runtime memory edits. Sparse scenario artifacts remain reviewable recipes, recorded screen-entry fixtures remain ignored local operator state, and native save-backed exact continuation is bounded to supported save/run data. Multiplayer lobby restore is reported as `lobby-only`; active multiplayer restore requires `allowDegradedLocalMultiplayer` to omit remote clients intentionally and report those omissions.

### `skill_install`

Purpose: copy the checked-in self-contained `spirectl` skill directory into another local repo tree without introducing a separate installer workflow.

Maps to: `sts2 --json skill install`

Main inputs: `path?`

Output expectations: unchanged primary CLI `skill install` envelope plus `installedFiles[]`. Expect `name`, `path`, `pathSource`, `skillDir`, `sourcePath`, and per-file `sourcePath`, `relativePath`, `path`, and `kind`.

### `screenshot`

Purpose: capture one runtime PNG artifact to disk for debugging or reporting.

Maps to: `sts2 --json dev screenshot`

Main inputs: `output?`, `rpcTimeoutMs?`

Output expectations: unchanged CLI screenshot envelope. Expect the written `path`, image dimensions/format, and captured `screen` metadata. Live capture is bounded by `rpcTimeoutMs` when supplied and otherwise uses the CLI bridge RPC default.

### `snapshot_export`

Purpose: capture a bounded authored regression baseline bundle after deterministic setup through existing fixtures or scenarios.

Maps to: `sts2 --json dev snapshot export`

Main inputs: `spec`, `output`

Output expectations: unchanged CLI snapshot-export envelope. Expect the normalized manifest plus the written `snapshot.json` root and any authored `state.json`, `inspect-actions.json`, `log-health.json`, `diagnostics.json`, or `runtime.png` artifacts.

### `snapshot_compare`

Purpose: re-capture the authored bounded regression surfaces and compare them with a checked-in baseline bundle.

Maps to: `sts2 --json dev snapshot compare`

Main inputs: `spec`, `baseline`, `bundleDir?`

Output expectations: unchanged CLI snapshot-compare envelope. Expect per-section comparison results, mismatch details when the baseline differs, and optional persisted bundle paths.

### `wait_for`

Purpose: poll `state` until a single predicate matches or a timeout occurs.

Maps to: `sts2 --json dev wait-for`

Main inputs: `path`, exactly one predicate from `equals`, `contains`, `gt`, `gte`, `lt`, `lte`, `exists`, or `notExists`, plus optional `perspective?`, `playerId?`, `timeoutMs?`, and `intervalMs?`

Output expectations: unchanged CLI wait envelope on success. Timeouts remain structured CLI failures with the last observed value in the error payload.

### `assert`

Purpose: check one state predicate immediately with reliable automation-oriented exit codes.

Maps to: `sts2 --json dev assert`

Main inputs: `path`, exactly one predicate from `equals`, `contains`, `gt`, `gte`, `lt`, `lte`, `exists`, or `notExists`, plus optional `perspective?` and `playerId?`

Output expectations: unchanged CLI assertion envelope on success. Assertion mismatches remain structured CLI failures with `payload.error`.

### Static Inspection Search Roots

All static inspection tools accept the existing CLI search-root inputs in camelCase:

- `gamePath?`
- `assembliesDir?`
- `resourcesDir?`
- `modsDir?`
- `includeMods?`
- `includeDependencies?`

Managed code tools inspect game/product assemblies by default and skip common framework or third-party dependency DLLs. Set `includeDependencies: true` only when the runner explicitly needs dependency symbols. This flag is independent from `includeMods`; set both when the desired search space is mod assemblies plus dependency DLLs.

### `code_locate`

Purpose: discover candidate symbols before deeper metadata or decompile work.

Maps to: `sts2 --json code locate`

Main inputs: `subject`, `query`, `limit?`, plus the shared search-root inputs including `includeDependencies?`

Output expectations: unchanged helper envelope with compact matches and stable IDs for follow-up commands.

### `code_describe`

Purpose: inspect metadata for a known symbol ID or query without decompiling full bodies.

Maps to: `sts2 --json code describe`

Main inputs: `subject`, `query`, plus the shared search-root inputs including `includeDependencies?`

Output expectations: unchanged helper envelope with symbol metadata, member summaries, and related IDs.

### `code_refs`

Purpose: find metadata or IL-based references to a known symbol.

Maps to: `sts2 --json code refs`

Main inputs: `subject`, `query`, `limit?`, plus the shared search-root inputs including `includeDependencies?`

Output expectations: unchanged helper envelope with compact reference results and stable IDs.

### `code_derived`

Purpose: find derived types or members from a known base type.

Maps to: `sts2 --json code derived`

Main inputs: `subject`, `query`, `limit?`, plus the shared search-root inputs including `includeDependencies?`

Output expectations: unchanged helper envelope with compact inheritance/navigation results and stable IDs.

### `code_decompile`

Purpose: explicitly request deeper decompile output when metadata is not enough.

Maps to: `sts2 --json code decompile`

Main inputs: `subject`, `query`, `full?`, plus the shared search-root inputs including `includeDependencies?`

Output expectations: helper envelope with `backend`, `text`, and `memberSummaries`. Treat this as the last step in the staged static-inspection flow; default output stays metadata-first, while `full: true` opts into ILSpy for exact matches.

### `code_scene_search`

Purpose: search static Godot text, binary, and packed scenes/resources for candidate scenes, nodes, scripts, and resource matches.

Maps to: `sts2 --json code scene-search`

Main inputs: `query`, `limit?`, plus the shared search-root inputs

Output expectations: unchanged helper envelope with scene or resource matches, scene IDs, provenance such as `storageKind` and `containerPath`, and notes for static-only limitations.

### `code_scene_tree`

Purpose: inspect the static node tree for one supported text, binary, or packed scene resource.

Maps to: `sts2 --json code scene-tree`

Main inputs: `scene`, plus the shared search-root inputs

Output expectations: unchanged helper envelope with normalized `scenePath`, provenance such as `storageKind` and `containerPath`, node hierarchy, and notes about what was or was not statically resolvable.

### `code_scene_node`

Purpose: inspect one specific static scene node in detail from one supported text, binary, or packed scene resource.

Maps to: `sts2 --json code scene-node`

Main inputs: `scene`, `nodePath`, plus the shared search-root inputs

Output expectations: unchanged helper envelope with exact node metadata, resource-reference provenance, and `attachedScriptTypeId` when resolvable.

### `test_run`

Purpose: execute YAML or JSON scenarios through the same CLI and runtime helpers used by direct commands.

Maps to: `sts2 --json test run`

Main inputs: `path?`, `inline?`, `artifactsDir?`, `failureArtifacts?`

Output expectations: unchanged CLI runner envelope. Expect persisted artifact metadata, per-scenario results, and existing exit-code semantics.

### `test_stress`

Purpose: repeat one checked-in scenario file or directory under bounded iteration or duration controls while preserving aggregate artifact references.

Maps to: `sts2 --json test stress`

Main inputs: `path`, exactly one of `iterations` or `durationMs`, plus optional `maxFailures?`, `cooldownMs?`, and `artifactsDir?`

Output expectations: unchanged CLI stress-run envelope. Expect aggregate status, completed iteration counts, per-iteration nested run references, and one persisted stress-summary path.
