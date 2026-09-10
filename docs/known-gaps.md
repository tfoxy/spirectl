# Known Gaps

Short gap list for planning. For file-level pointers, use the subsystem maps under [docs/maps/](./maps/).

Classification:

- `planned in S##`: sequenced future roadmap work
- `parking-lot`: intentionally deferred, not yet sequenced into a spec
- `non-goal`: intentionally outside the current product shape

## CLI UX

- Shell-profile mutation or default writes into shell-owned completion directories — non-goal — completion stays safe-by-default and app-managed rather than editing user startup files automatically.

## Runtime coverage

- Remaining unmodeled/modded non-combat controls beyond the shipped S86 reward/card, shop, rest-site, treasure/relic, map, and event-room intent verbs — planned by future focused specs as needed — generic `choose` should stay only an explicit fallback for generic, modded, or unmodeled visible choices with no modeled `preferredAction`.
- Runtime scene-node property and transform inspection — supported by M72 — `dev scene node` keeps compact metadata by default and exposes bounded layout/render properties plus computed transforms only behind `--properties` and `--computed-transform`.
- Encounter visual packages beyond the checked-in base-game current catalog — bounded future catalog work — M78 ships the generic package/extract/runtime-state contract and the representative live preflight/helper path (`dev visual-preflight`, `m78-live-encounter-artifacts`), S90 has Kaiser Crab plus Ovicopter, and S107 adds broader catalog-facing coverage such as Knowledge Demon Boss metadata, expanded enemy visual keys, special combat backgrounds, and common VFX keys. Exact animation/VFX timing, pixel-perfect framebuffer parity, and arbitrary uncataloged encounter package inference remain gaps; Kaiser-style render targets must preserve selector/bounds evidence or structured isolation-failure diagnostics rather than claiming a leaked full scene as isolated.
- Screen rendering is a downstream concern — non-goal — `spirectl` no longer ships a renderer, a scene binding catalog, or a `presentation` command group. It publishes semantic state, live Godot scene state, models, localization, and asset bytes; consumers such as `../sts2-couch-coop` own layout, DOM/canvas output, and product policy. The only TypeScript surface shipped here is `@spirectl/presentation`'s two subpaths: `./render` (animation bindings, BBCode tags, play-zone threshold) and `./spine` (DOM-free Spine/geoclip clip parsing and frame sampling).
- `state actions` treasure-room quirk (inherited from an earlier expression-driven action table): the table gates `open-chest`/`take-relic` on `treasure.currentRelicsActive`, a field `state` never emits, so both actions surface whenever a treasure room is shown — `open-chest` is offered even when relics are already active. See `cli/src/state_actions.rs` `surface_treasure`.
- Full render layout parity for combat and later screens — non-goal here — the removed presentation-bundle era (S98/S103/S105/S106) produced render slices, interaction hit-target metadata, and coarse validation artifacts; none of that surface ships now. Exact animation, hover behavior, VFX timing, framebuffer parity, broader overlay layout, and exhaustive asset coverage are downstream renderer concerns. Existing S83-S84 typed state sections, `choices[]`, and `availableActions[]` remain the semantic/action compatibility surfaces, with `choiceKind`, `intentKind`, `ownerPlayerId`, `perspective`, and `preferredAction` metadata where known.
- Runtime state and action self-description now document that split through `inspect actions.stateContract`; gaps in typed fields should be represented as structured notices, not by moving raw scene-tree internals into default `state`.
- In-game console command coverage remains intentionally explicit after M73 — `sts2 dev console <command> [args...]` is shipped through the bridge, runner, wrapper, service, and AI/MCP surfaces, but it is not a semantic action and never appears in default `state`. Output is limited to STS2 `CmdResult.msg`; no screen scraping or arbitrary console UI history inspection is attempted. Persisted-file commands such as `achievement`, `cloud`, and `unlock` require dangerous mode.
- Broader live screen/state coverage beyond the current `main-menu`, `combat`, `map`, `event-room`, `treasure-room`, `relic-selection`, `rest-site`, `shop`, `rewards`, `card-selection`, `simple-card-selection`, `deck-card-selection`, `bundle-selection`, `card-overlay`, `Screens.CharacterSelect.NCharacterSelectScreen`, and `Screens.CharacterSelect.NMultiplayerLoadGameScreen` slice — bounded future work — `card-overlay` now has typed partial support when observable detail data is available, including passive/blocking policy and structured notices, but unsupported named families and unvalidated overlay follow-through controls remain follow-on work. Mid-combat `simple-card-selection` (Headbutt) and bundle (`bundle-selection`, ScrollBoxes) screens now expose typed `run.players[].overlays[].simpleGridCardSelection` / `bundleCardSelection` overlays with the select-card/select-bundle ids inline, and support combat fixture composition; in-hand upgrade-select exposes `run.view.handSelection.previewCard` (the upgraded clone). Remaining polish: the bundle preview/confirm sub-screen is exposed (`previewActive`/`selectedBundleId`) but is not separately modelled in state.
- Broader multiplayer screen coverage beyond the S87 lobby, run, combat, ownership, perspective, and wrong-player legality surface — planned by future focused specs as needed — the shipped model is ownership-aware, but any newly supported screen id still needs explicit runtime and legality coverage before it should claim remote-owned execution.
- Full remote-client multiplayer orchestration — parking-lot after S87/S92/S93 — M55 ships multiplayer-aware scenario metadata and lobby-only/degraded local restore limits, M59 keeps recorded fixtures local/recipe-shaped, and S87 reports unavailable/local-only/host-mediated/configured-client/unsupported capability honestly; one local bridge still does not recreate independent remote clients.
- Screen-independent lookup of non-model selection affordances such as `RANDOM_CHARACTER` (title/description/portrait/select-bg by id) — parking-lot — `models characters` stays the real game-data catalog (`ModelDb.AllCharacters`), so `RANDOM_CHARACTER` is reported as a missing id rather than a hollow model. RANDOM is a runtime selection option already exposed via lobby `state` and the select-character action; a future `reference` topic (the documented sibling of `models` for constant non-model runtime data) is the intended home for static by-id resolution. Deferred.

## Bridge deploy

- The quiescence wait (`--wait-quiescent-ms`, `dev wait-for-transitions`) is not sufficient to gate a fixture
  load after a restart. It polls for "no room transition and no intro animation", and on 2026-09-04 reported
  `quiescent:true` 1.2 s after attach on a freshly restarted game -- inside the window where the boot flow still
  pops fixture-created lobbies. The downstream repo tried replacing its fixed 20 s `dev.delay` with it and the
  scenario hung in the probe until timeout, so the delays stay. Making the wait cover this needs a positive
  signal that the boot flow has finished, not just the absence of a transition.

- `game install-bridge`'s `contentHash` skip cannot fire after a real build. `bridge-mod/Directory.Build.props`
  defaults `SpirectlBridgeBuiltAtUtc` to `UtcNow` and emits it as assembly metadata, so identical sources build
  to different bytes (the timestamp string, and with it the MVID, PDB id and PE TimeDateStamp). Verified live:
  two consecutive `install-bridge` runs report `content_changed`, while two `--no-build` runs report
  `content_unchanged`. Fixing it means giving the stamp a source-derived value instead of the wall clock, which
  changes what the published `buildIdentity.builtAtUtc` means and feeds `bridge-health`'s `stale_live_host`
  comparison — an owner decision, not a mechanical change. Parking-lot.

## Embedded runtime

- Latest-state cache for embedded consumers — parking-lot — S118 specified `ISpirectlRuntime.GetLatestState(EmbeddableLatestStateRequest)`: a cached perspective-aware snapshot with status (`Refreshing` / `TimedOut` / `Unhealthy`), revision, captured/returned timestamps, age, freshness threshold and health diagnostics. It was never implemented, and no cached read exists on the interface today; the embedded surface is `GetCurrentState`, `SubscribeCurrentState` and `WatchCurrentStateAsync`, and an embedder that wants a last-known snapshot keeps one itself off the subscription. Docs promising it were corrected rather than deleted so the decision stays legible. If it is ever built, the entry scoping is the hard part: cache entries have to key on the semantic projection inputs (requested player/perspective, debug inclusion, included sections), because serving one player's private projection to another player is what makes a naive cache wrong rather than merely stale.
- Per-frame streaming of a spine clip extract — parking-lot — a timeline extract buffers every rendered frame and returns them in one payload, so a consumer waits for the whole clip and holds all of it in memory. The seam is identified, not built: an `Action<AssetExtractFrame>? OnFrame` callback on `AssetExtractRequestSnapshot`, invoked per frame where `BuildTimelineResult` currently receives a fully materialised `IReadOnlyList<AssetExtractFrame>` (`bridge-mod/src/Spirectl.Sts2/Live/Sts2AssetExtractProvider.RenderAssets.cs`). Keeping the buffered payload as the default means no existing caller changes. Recorded here so the next person does not re-derive where it goes.

## Mod development ergonomics

- Fast restart-free iteration for most developer-owned mod logic — supported by M57 template — native STS2 mod loading is startup-bound, but `templates/stable-harmony-trampoline/` now provides the stable shell, contract, reloadable logic assembly, marker trigger, structured diagnostics, and unload-reporting pattern for developer-owned logic.
- First-class hot-reload project creation and profile automation — supported by M60 — `sts2 project scaffold stable-harmony-trampoline` now creates downstream shell/contract/logic/test projects plus local config guidance and generated profiles for shell deploy/restart, logic build/copy, and cleanup.
- Explicit CLI reload control for shell-supported hot-reload projects — supported by M61 — `sts2 dev mod-reload` and `dev mod-reload status` use `sts2.hot-reload.yaml` plus the M57 shell protocol to build/request reloads, report status, and feed optional diagnostics capture.
- Hot-reload automation surface for service, wrapper, runner, and AI/MCP — supported by M62 — `hot_reload_status`, `hot_reload`, npm wrapper helpers, MCP dispatch, `dev.hot-reload` runner steps, and `hotReloadAndWait` all preserve the M57/M60 supported-shell boundary and normal-mode semantics.
- Optional hot-reload authoring guardrails — supported by M63 — `EnableHotReloadGuardrails` enables advisory analyzer diagnostics and `[HotPatch]` source generation in the template, `--enable-guardrails` writes the scaffold default, and `HOTMOD_DEV_OVERLAY=1` enables the dev-only reload status overlay.
- Full native hot reload of arbitrary STS2 mods — non-goal — `spirectl` should document the loader boundary and provide an opt-in dev architecture around it instead of pretending the game can safely rediscover, replace, and unload any installed mod after initialization.

## Static inspection

- Broad source navigation or project reconstruction — non-goal — `code decompile --full` now provides exact-match ILSpy text, but the toolchain still does not try to become a full IDE/project reconstruction workflow.

## Test runner

- Richer arbitrary setup control beyond the current authored recipe-backed fixture families — non-goal until each field can be validated honestly — the shipped S89 surface covers typed non-combat, overlay entry, intent-action-visible, and multiplayer ownership examples with `recipeReport` diagnostics, but it still does not patch hidden combat queues, RNG continuation, animation state, enemy AI history, or transient UI internals.
- Broader remote-run orchestration beyond the current CLI-owned automation service — bounded future work — `sts2 service serve` now supports async remote `test run` jobs with memory-only or durable filesystem-backed metadata, restart recovery, explicit `orphaned`/`unknown` recovery states, bounded artifact indexes/downloads for that runner surface, and explicit network MCP over the same catalog/service contracts. First-class remote artifact transfer is still limited to `test run`.
- Broader restore fidelity beyond the current support matrix — bounded by S88/S89 diagnostics — restore remains split between recipe-backed fixtures, `dev.load-scenario` sparse artifacts, and exact native sidecars. Fixture loading reports applied/inferred/omitted/unsupported/degraded multiplayer fields, but broader restore behavior stays bounded and explicit.
- Arbitrary mid-screen checkpoint resume fidelity — non-goal/replaced by M59 — the M52-M55 checkpoint surface overpromised hidden runtime restore for combat queues, played cards, RNG continuation, enemy AI history, relic turn history, and transient UI/action state. M59 replaces `dev checkpoint *` with recorded screen-entry fixtures that recreate supported screen setup instead of patching arbitrary live internals.
- Unstructured arbitrary runtime memory editing — non-goal — restore surfaces must remain bounded, bridge-mediated, explicit, and safe-by-default rather than becoming a generic memory patcher.

## Visual validation

- Hosted screenshot baseline services or remote visual-review workflows — non-goal — visual validation stays CLI-local, filesystem-backed, and explicit rather than becoming a hosted review product.
- Viewport content-scale is not exposed in scene state — parking-lot — a downstream renderer that reproduces label metrics needs the window content-scale factor (which also varies with window aspect ratio) to match on-screen glyph size; the bridge does not report it today.
- Two planned spine-geoclip workstreams (p7 ws4 and ws5) were never implemented — parking-lot — the p7 series ran ws1 (phase profiling + the atlas shadow arm), ws2 (walk), ws3 (audits), then jumped to ws6-ws8. Nothing in the tree records what ws4 and ws5 were to do: that intent lives only in the p7 planning thread, so anyone picking the backlog up should re-derive it from the ws1 profile numbers rather than trust the numbering.
- The bake's scene-tree pause lever is armed by nothing — parking-lot — pausing the tree between the bake's awaited frames is where its parked milliseconds would go, and the lever ships with the proof it needs (a paused bake must demonstrate its rig actually re-posed, or it writes nothing and falls back). It is DEFAULT OFF and stays off: the arming gate wants a short bake, and every profile measured so far is far above that threshold. It is the lever for a future where bakes are short enough, not a knob to turn on today.
- Atlas-first association is on by default behind a kill switch — supported, with an escape hatch — `SPIRECTL_SPINE_GEOCLIP_ASSOC_ATLAS_FIRST` set to `0`/`off`/`false`/`no` restores the previous behaviour exactly (the colour probe discovers, the atlas only shadows and reports); anything else, including unset, arms it. The default was licensed by unarmed runs in which the shadow arm disagreed with the measured association zero times, plus armed runs producing identical output on both test rigs. Keep the switch: it is the one-step revert if a rig is ever found where the cheap evidence is not sufficient.

## Wrapper and external runners

- A second transport or JS-native runtime protocol separate from the CLI — non-goal — the wrapper stays thin and shells back to the CLI.
- Replacing the CLI-local scenario runner with a second JS runner implementation or protocol — non-goal — external runners should continue to layer over the CLI runner rather than fork it.

## AI/MCP

- Hosted debugger dashboards, accounts, collaborative debugger UI, and long-term event storage - non-goal - S91 now supports one mutating controller lease, multiple read-only observers, and bounded debugger event replay/follow with sequence cursors, retention metadata, overflow/expired flags, and structured unsupported-host notices through CLI, runner, service, npm wrapper, and MCP/AI tools. The stream is an in-memory loss-detectable transcript, not durable history.
- Dangerous raw input as AI tools — non-goal — `sts2 --mode dangerous act mouse click ...` exists for CLI/operator fallback use, but it stays out of the standalone AI catalog.
- Hosted account management, dashboards, multi-tenant policy, and managed TLS for MCP — non-goal — network MCP is now supported only as explicit operator-owned service configuration. Local stdio remains the default safe path; non-loopback Streamable HTTP binds require bearer auth plus threat-model acknowledgement, and reverse proxy/TLS responsibility stays outside `spirectl`.
