# Architecture

`docs/initial-spec.md` remains the consolidated design brief. This document describes the current shape: a typed runtime contract, a live-capable standalone local transport path over Unix sockets, Windows named pipes, or loopback TCP, practical semantic actions, bounded log retrieval, CLI-local wait/assert helpers, a CLI-local YAML/JSON scenario runner, repo-owned toolchain/recovery outputs plus lifecycle profiles, a neutral bridge core, a loader-facing STS2 host adapter, a repo-owned live bridge deploy/verify script, and a real static inspection path for both managed code metadata and Godot text scenes/resources.

## Current Shape

- `cli/`: Rust CLI with declarative command definitions, layered config/provenance reporting, optional local command usage tracking, managed toolchain/project ownership, live-bridge path reporting, inspect metadata, generated protobuf types, a typed bridge client, CLI-local path query/assert/diagnostics/snapshot helpers, explicit dev-debug/breakpoint JSON shaping, runner/stress orchestration, and a shell-out integration to the .NET helper tool.
- `bridge-mod/`: three .NET 9 ownership layers. `Spirectl.Sts2` is the reduced embeddable runtime (state,
  semantic actions, assets/models/reference data, scene stream, animation/combat hints, Spine/geoclip, and
  main-thread dispatch); `Spirectl.BridgeMod` owns bridge-only contracts, composition, transport, development,
  diagnostics, and `perf-report/1`; `Spirectl.BridgeMod.Sts2Host` owns STS2/Godot-backed bridge-only adapters.
- `dotnet-tools/`: .NET 9 helper tool for metadata-based `locate`, `describe`, `refs`, `derived`, explicit `decompile`, helper-owned decompile corpus export, and static Godot scene/resource inspection.
- `proto/`: additive protobuf contracts under `spirectl/v0` for handshake, state, actions, logs, screenshots, live-capable debug control, and structured bridge errors.
- `scripts/live-bridge.sh`: reproducible Linux-first build/publish/deploy/verify workflow for the live STS2 bridge path.
- `fixtures/` and `tests/`: authored YAML fixtures plus support for executable `.sts2.yaml` and `.sts2.json` scenarios in the M7 runner.
- `npm-wrapper/`: thin local binary wrapper, thin ESM Node helper with direct fixture/screenshot/visual-diff/snapshot/stress helpers, a thin Playwright fixture module, and a thin stdio MCP adapter that maps AI-tool calls back onto the CLI.

## Runtime Contract Model

Runtime commands now cross a real typed contract:

1. `sts2 state`, `sts2 act`, `sts2 dev logs`, `sts2 dev debug`, `sts2 dev breakpoint`, `sts2 dev screenshot`, `sts2 dev screenshot-diff`, `sts2 game info`, and `sts2 inspect actions` call the CLI's typed `RuntimeBridgeClient` directly or reuse the same CLI-local screenshot/diff helpers over that client.
2. The default executable transport is `transport.kind=ipc`, so ordinary `sts2` commands target the live local bridge without local config edits.
3. The mock transport is still available through explicit config and talks to a stub bridge service implementation built on generated protobuf messages and gRPC service definitions, but it now exercises successful `play-card` / `use-potion` / `choose` / `confirm-selection` / `cancel-selection` / `select-map-node` / `end-turn` / `ready` / `unready` / `select-character` paths plus structured failures.
4. `transport.kind=ipc` supports a real standalone IPC path that keeps the existing protobuf request/result envelopes without relying on ASP.NET Core hosting: Unix-domain sockets on Unix-like hosts and named pipes on Windows.
5. The live host side lives in `bridge-mod/src/Spirectl.BridgeMod.Sts2Host` and only compiles the STS2/Godot-backed adapter when `STS2_ASSEMBLIES_DIR` or `Sts2AssembliesDir` points at real game assemblies.
6. `scripts/live-bridge.sh deploy` is the supported in-repo packaging path for the loader, host publish output, and generated manifest.
7. `scripts/live-bridge.sh verify` uses IPC handshake/state success as the bring-up gate instead of generic game-log health scans.
8. `transport.kind=tcp` now uses the same standalone framed-protobuf host over loopback TCP with explicit local-only validation.
9. `sts2 dev wait-for` and `sts2 dev assert` intentionally stay CLI-local in M4: they poll typed `state` responses and evaluate simple dotted paths, numeric indexes, and additive by-id object paths instead of introducing new wait/assert RPCs.
10. `sts2 test run` is also CLI-local in M7: it loads YAML or JSON scenarios from files, directories, or inline input, normalizes the supported step subset, executes those steps through the same Rust helper functions used by direct CLI commands, and persists run artifacts from the CLI layer rather than inventing a second protocol.
11. `sts2 dev snapshot export`, `sts2 dev snapshot compare`, and `sts2 test stress` are likewise CLI-local in M34: they reuse typed `state` / `inspect actions` / `dev log-health` / `dev diagnostics` / screenshot helpers plus nested `test run` reports instead of adding snapshot restore RPCs or a second orchestration service.
12. The M50-M59 bounded restore roadmap is separate from fixtures and snapshot regression: M50 shipped lifecycle close/kill foundations, M51 defined scenario/checkpoint contracts and planned stubs, M52-M55 proved the limits of ignored local checkpoint capture/resume, M53 added sparse shareable scenario export/load, M54 hardened restore fidelity with per-field reports and validation summaries, M55 handles multiplayer restore limits, and M59 corrects the checkpoint surface by replacing arbitrary local checkpoint resume with recorded screen-entry fixtures.

Embedded downstream callers use `ISpirectlRuntime.GetCurrentState(CurrentStateRequest)` for the same direct current semantic observation without launching the CLI, service, IPC, TCP, MCP, screenshot, browser, or asset paths. There is no cache-backed variant: S118 specified a `GetLatestState(EmbeddableLatestStateRequest)` beside it and it was never built, so a caller that wants a last-known snapshot keeps one itself off `SubscribeCurrentState`.

This keeps the core neutral while making the first live in-game bridge path and validation loop real.

## Command Catalog

The CLI keeps a single internal command catalog for:

- `inspect commands`
- `inspect examples`
- `inspect ai-tools`
- shared example strings used by docs and tests

That avoids drifting documentation between help text, inspect metadata, and example outputs.

M28 through M33 extend that same catalog with repo-owned `toolchain info`, `project profile *`, `project hook *`, diagnostics/probe surfaces, visual-validation helpers, and staged discovery helpers instead of inventing a second project-orchestration or MCP-specific layer outside the CLI.

`inspect ai-tools` builds on that same command catalog plus a small explicit AI mapping layer. The output is a machine-facing catalog shaped for adapters and docs:

- `source`
- `adapter`
- `tools[]` with `name`, `summary`, `status`, `readOnly`, `mapsTo`, `inputSchema`, `outputShape`, `limitations`, and `recommendedUsage`

`inspect actions` is separate from that local catalog and is sourced from the bridge handshake plus the current state's `availableActions`.

That split matters more in M4: handshake-level `supportedActions` can describe bridge-known kinds, while state-level `availableActions` only advertises actions that are currently executable on the live or mock screen. `inspect actions` now also carries the current `screen`, `resolvedPerspective`, and `notices`, and handshake descriptors include explicit `status` plus `parameters` metadata so callers can distinguish implementation maturity from screen-specific executability.

## AI Tool Adapter Path

The new AI-facing layer stays intentionally thin and lives in `npm-wrapper/` rather than adding a second Rust or .NET service.

- `sts2 --json inspect ai-tools` is the authoritative adapter catalog.
- `sts2-mcp` loads that catalog once at startup and registers the same tool names and input schemas over stdio MCP.
- actual tool execution flows back through the Node wrapper and into the existing CLI commands.
- successful MCP tool results preserve the underlying CLI JSON payload unchanged as `structuredContent`.
- expected CLI failures preserve `{ exitCode, argv, stderr, payload }` so agents can inspect the original CLI error body.
- adapter-only failures use small synthetic codes such as `cli_spawn_failed` and `cli_protocol_error`.

This keeps the CLI as the source of truth for behavior while still giving agent systems a narrower, explicit surface area.

## Test Runner Path

M7 adds a first formal `sts2 test run` runner without extending the bridge contract.

- scenario discovery is local to the CLI: a file path runs one scenario, and a directory path discovers `*.sts2.yaml` and `*.sts2.json` recursively in sorted relative-path order
- wrapper-facing reuse stays thin too: `npm-wrapper` maps `testRun({ path | inline | scenario })` back onto the same CLI command, serializing JS scenario objects through `--inline` instead of adding a second runner implementation
- parsing and validation are local to the CLI: M7 accepts canonical step IDs such as `dev.assert` plus small compatibility aliases such as `assert.query`
- execution is still contract-backed where appropriate: `game.info`, CLI-local `game.deploy`, `dev.load-fixture` / `dev.fixture.load`, `dev.logs`, `dev.assert`, `dev.wait-for`, `dev.screenshot`, `dev.screenshot-diff`, `act.play-card`, `act.use-potion`, `act.choose`, `act.confirm-selection`, `act.cancel-selection`, `act.select-map-node`, `act.end-turn`, `act.ready`, `act.unready`, and `act.select-character` all reuse the existing runtime client, lifecycle helper, or CLI-local assertion logic
- later additive runner/debugging work stays on the same path too: `dev.snapshot-compare` reuses the snapshot-regression helper locally, and `test stress` loops nested `test run` reports rather than spawning a second runner implementation
- artifact persistence is local to the CLI: `summary.json`, per-scenario `result.json`, scenario-local `steps/` artifacts for successful `game.deploy` / `dev.load-fixture` / `dev.hot-reload` / `dev.log-health` / `dev.diagnostics` outputs, default screenshot paths, and visual diff `comparison.json` bundles, plus best-effort failure capture through one diagnostics-owned `failure/evidence/diagnostics.json` bundle and sibling evidence files, all under `config.artifacts.dir` or `--artifacts-dir` without any new bridge RPCs
- additive runner mutation steps now stay on the same CLI-local path too: `game.deploy` reuses the lifecycle helper directly and `dev.load-fixture` / `dev.fixture.load` reuse the fixture-loading path without introducing a separate runner transport

## Bridge Core Boundaries

The shared runtime does not assume a concrete loader. It keeps only reusable embedder seams separate:

- state extraction: `IGameStateExtractor`
- runtime observation: `IRuntimeObservationProvider`
- state mapping: `RuntimeStateMapper`
- live Godot scene state: `Sts2GodotSceneStateProducer` / `Sts2RuntimeSceneWatcher`, streamed to downstream renderers as typed scene/transform deltas
- semantic actions: `IActionHandler`
- log streaming: `ILogStream`
- multiplayer perspective: `IPerspectiveProvider`

`Spirectl.BridgeMod` owns `BridgeRuntime`, its bootstrap/options/service aggregates, handshake/transport,
fixtures/scenarios/debug controls, lifecycle, console, screenshots, runtime-scene inspection, combat/map preview,
restore diagnostics, and producer/state-watch profiling. `Spirectl.BridgeMod.Sts2Host` supplies their live
STS2/Godot implementations and composes them through `Sts2BridgeRuntimeFactory`. The shared
`Sts2EmbeddableRuntimeFactory` builds reusable providers only; it does not create bridge development, lifecycle,
transport, screenshot, probe, fixture, or scenario services.

`BridgeRuntimeBootstrap.CreateScaffold()` now runs through a scaffold observation provider plus mapper instead of a direct placeholder extractor. `Sts2BridgeRuntimeFactory` supplies the live screen locator, main-thread dispatcher, runtime observation provider, and standalone multi-endpoint host without widening the shared runtime.

M75 adds `Spirectl.Sts2.Embedding.ISpirectlRuntime` as the small in-process runtime facade for downstream mods. It
uses the focused reusable provider boundary and does not start the CLI, automation service, bridge transport,
fixture/scenario/debug services, screenshots, probes, IPC host, or TCP host.

The direct embedded semantic read is `GetCurrentState(CurrentStateRequest)` — the only synchronous one. It observes the current STS2 screen through the shared runtime observation and mapping core and returns product-neutral state plus structured error/health information. Reactive delivery is `SubscribeCurrentState` / `WatchCurrentStateAsync`. The S118 latest-state cache (`GetLatestState`, with freshness metadata) was specified but never implemented and is not on the interface.

M76 expands the same runtime state contract with generic presentation fields instead of a product-specific phone/controller DTO. The protobuf/schema additions are additive: UI consumers can read card text and asset keys, player gold/relics/potions/piles/status effects, enemy status/intent/visual metadata, run breadcrumbs, and structured partial notices from `state` or the embedded facade, while existing automation clients can ignore those fields. Asset references remain opaque keys (`model://cards/<model-id>/image`, `enemy:<model-id>:visual`, etc.) and intentionally do not define URLs, bundle layout, or UI layout policy.

A downstream renderer layers over the existing runtime state, semantic action, model, localization, ResourceLoader inspection, live scene-state, and asset contracts: current state remains the semantic source of truth, assets provide bytes behind opaque keys, and `ExecuteAction` remains the legality-checked mutation path. This repo does not add layout to default `GetCurrentState`, and it does not define downstream HTTP routes, browser DTOs, cache policy, sessions, auth, WebSocket policy, or product UI behavior. Where geometry is reported at all it comes from live Godot control rects or explicit live scene nodes; screenshot-guided constants, mock/offline inferred rects, and guessed layouts are unsupported.

M4 extends those seams rather than redesigning them:

- `IActionHandler` now returns structured success or structured failure details instead of a boolean implemented/not-implemented split.
- `ILogStream` now supports bounded recent-log retrieval with stable filtering and ordering semantics.
- the STS2 host uses a shared action catalog so observation-time `availableActions` and execution-time legality checks stay aligned for currently supported actions.
- the STS2 host now uses shared live-introspection resolvers so screen classification, lobby extraction, combat state, and semantic action execution resolve the same runtime objects instead of drifting reflection paths.

Bridge version metadata is now shared across the bridge assemblies through `bridge-mod/Directory.Build.props`, exposed in code through `BridgeBuildInfo`, and mirrored into the Rust stub bridge at build time. Handshake and state therefore report the same `bridgeVersion` string shape: `spirectl-bridge/<semver>`.

## Multiplayer Lobby Path

The multiplayer lobby path is still bridge-first and additive rather than a new subsystem.

- `Sts2ScreenLocator` preserves the live game screen ids for both `Screens.CharacterSelect.NCharacterSelectScreen` and `Screens.CharacterSelect.NMultiplayerLoadGameScreen` while routing both through the shared lobby observation path.
- `Sts2RuntimeObservationProvider` branches lobby extraction into start-run and load-run paths.
- start-run lobbies pull players from `StartRunLobby.Players`, local-player identity from `StartRunLobby.LocalPlayer` or `INetGameService.NetId`, ready state from `LobbyPlayer.isReady`, selected characters from the live lobby player record, and available characters from visible lobby buttons first.
- load-run lobbies pull players from `LoadRunLobby.Run.Players`, preserve `run.seed` / `run.floor` / `run.act` / `run.playersById` from the loaded save, derive ready state from `LoadRunLobby.IsPlayerReady`, and surface visible character availability from discovered descendant character-select buttons when the screen exposes them.
- full-state by-id maps such as `lobby.playersById`, `lobby.availableCharactersById`, `run.playersById`, and `combat.playersById` are additive indexes over the canonical ordered arrays; they exist to make scripts and AI callers stable without changing the query language. Compact `state` omits duplicate lobby by-id maps and keeps `lobby.players[]` / `lobby.availableCharacters[]` as the lobby roster surfaces.

The bridge stays explicit about partial support:

- player names remain null when not exposed by the current runtime hook
- `hostPlayerId` is only populated when it can be derived directly
- lobby, run, and combat player payloads only expose additive `isLocal` / `isHost` / `isRemote` flags where the current screen/runtime path makes that role observable
- player-scoped visible `choices[]` only carry additive `ownerPlayerId` where the bridge already knows which player owns the current visible option or target
- `availableCharacters` can still be empty in load-run lobbies when the runtime screen does not expose discoverable character buttons, but when visible buttons are present the same stable character ids now back executable `select-character` actions on both lobby variants
- fallback character catalogs derived from `ModelDb.AllCharacters` are marked by notices instead of being presented as fully live button state

## Static Inspection Path

`sts2 code locate`, `sts2 code describe`, `sts2 code refs`, `sts2 code derived`, `sts2 code hooks`, `sts2 code hook-info`, `sts2 code decompile`, `sts2 code scene-search`, `sts2 code scene-tree`, and `sts2 code scene-node` shell out to the helper tool project. The CLI resolves search roots from command flags plus config, then passes explicit assembly, resource, and mod roots to the helper. `sts2 --json inspect reference-topics` is the adjacent CLI-owned catalog for the staged modding workflows built on top of those commands.

The helper tool now:

- scans managed assemblies from the resolved game root
- optionally scans mod assemblies when `--include-mods` is set
- supports two managed metadata loading modes:
  - declarations-only: declared types, members, signatures, base types, interfaces, nested types, and inheritance relationships for `locate`, `describe`, and `derived`
  - with-references: declarations plus IL body reference scanning for `refs`, metadata-summary `decompile`, `hooks`, and `hook-info`
- separately indexes supported static Godot text, binary, and packed resources from the resolved resource root and optional mod roots
- returns stable IDs for follow-up `describe`, `refs`, `derived`, and `decompile` steps
- adds a dedicated hook-intelligence layer over the reference-backed metadata graph so `hooks` stays discovery-oriented while `hook-info` stays exact
- keeps `locate`, `describe`, `refs`, `derived`, and `decompile` separate so metadata lookup stays useful even when deeper inspection is unnecessary
- resolves member navigation from declared metadata first, then augments method summaries and references with IL-token scans where available
- keeps the default `decompile` output metadata-derived, with explicit embedded-ILSpy escalation only when callers pass `--full`
- parses supported `.tscn`, `.escn`, `.tres`, `.scn`, `.res`, and unencrypted `.pck` entries for scene headers, nodes, script attachments, `NodePath(...)` references, and simple external/subresource links
- enriches scene/resource results with follow-up type IDs when `Godot.ScriptPathAttribute` or `Godot.GlobalClassAttribute` metadata is available in scanned assemblies
- keeps scene output honest through `notes[]` when the supported binary or packed parser slice cannot recover deeper structure

The helper uses metadata inspection and text-resource parsing rather than runtime bridge transport. That keeps static inspection additive and decoupled from the live bridge.

This split is intentional:

- `code scene-*` is static inspection for authored/extracted text resources plus the supported binary and packed-resource slice
- runtime scene-tree observation remains a separate bridge-backed dev surface through `dev scene tree`, `dev scene node`, `dev scene children`, and `dev scene set-visible`

The intended follow-up flow is explicit: `scene-search` discovers candidate scenes, nodes, or resources; `scene-tree` and `scene-node` provide exact structure; attached script type IDs then bridge back into `hooks`, `hook-info`, `describe`, `refs`, `derived`, and `decompile`. The checked-in `inspect reference-topics` / `docs/modding-reference.md` pair documents those staged loops without adding a second search stack or dynamic doc indexer.

M28 adds one more helper-only static-inspection path on top of that foundation: `project recover --kind decompile` shells into a helper-side `decompile-export` command so the CLI can own a stable repo-local ILSpy corpus under `.sts2/toolchain/decompile/` without turning the default `code decompile` UX into a broad project-reconstruction surface.

## What Is Explicitly Not Implemented Yet

- broader live runtime coverage beyond the currently shipped `main-menu` / `combat` / `map` / `event-room` / `treasure-room` / `relic-selection` / `rest-site` / `shop` / `rewards` / `card-selection` / `simple-card-selection` / `deck-card-selection` / `bundle-selection` / `Screens.CharacterSelect.NCharacterSelectScreen` / `Screens.CharacterSelect.NMultiplayerLoadGameScreen` slice is planned in S83-S84, and richer semantic actions beyond the current legality-backed `choose` / `play-card` / `use-potion` / `confirm-selection` / `cancel-selection` / `select-map-node` / `end-turn` / `ready` / `unready` / `select-character` surface are planned in S85-S86. S85 intentionally allows breaking API cleanup toward typed choices and intent-first actions, with generic `choose` kept only as an explicit fallback for generic, modded, or unmodeled visible choices. M76 makes combat presentation-grade and adds generic breadcrumbs/notice metadata, but the remaining `card-overlay` family still returns an explicit partial screen-identity-only notice rather than full overlay presentation state.
- richer deterministic setup beyond the current authored `main-menu`, `combat`, `map`, `rewards`, `rest-site`, `event-room`, `treasure-room`, `relic-selection`, `shop`, `card-selection`, `simple-card-selection`, `deck-card-selection`, `bundle-selection`, `Screens.CharacterSelect.NCharacterSelectScreen`, and `Screens.CharacterSelect.NMultiplayerLoadGameScreen` recipe slices is planned in S89 after S88 re-audits restore support for the current state/action contract; the shipped slice now includes authored load-run lobby setup plus explicit event options, opened treasure-room proceed flow, and shop inventory/card-removal overrides. Local checkpoints shipped as M52-M55 experiments, shareable sparse scenarios shipped in M53, M54 restore hardening reports bounded fidelity explicitly, and M55 adds multiplayer lobby/degraded-local restore limits. M59 replaces the active checkpoint command model with recorded screen-entry fixtures because arbitrary mid-screen hidden-state restoration remains intentionally out of scope.
- deeper debugging workflows beyond the shipped explicit `dev debug` / `dev breakpoint` pause/resume/frame-step/action-step and query-backed breakpoint surface are planned in S91
- durable remote jobs and hosted/network-native MCP service models are planned in S92-S93

Those gaps stay explicit through structured bridge errors, live-state notices, and explicit lifecycle configuration instead of hidden no-op behavior.
