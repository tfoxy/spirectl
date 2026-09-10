# Runtime Actions Map

See also: [current status](../current-status.md), [known gaps](../known-gaps.md), [CLI](../cli.md), [testing](../testing.md).

## Primary files

- `proto/spirectl/v0/actions.proto`
- `bridge-mod/src/Spirectl.BridgeMod/BridgeRuntime.cs`
- `bridge-mod/src/Spirectl.BridgeMod/Protocol/BridgeRuntimeProtocolAdapter.cs`
- `bridge-mod/src/Spirectl.Sts2/Embedding/SpirectlRuntimeFacade.cs`
- `bridge-mod/src/Spirectl.BridgeMod/Services/GrpcBridgeService.cs`
- `bridge-mod/src/Spirectl.Sts2/Common/Sts2ActionCatalog.cs`
- `bridge-mod/src/Spirectl.Sts2/Live/Sts2ActionHandler.cs`
- `bridge-mod/src/Spirectl.Sts2/Common/Sts2RewardIds.cs`
- `bridge-mod/src/Spirectl.Sts2/Common/Sts2CardSelectionIds.cs`
- `bridge-mod/src/Spirectl.Sts2/Common/Sts2ShopIds.cs`
- `bridge-mod/src/Spirectl.Sts2/Common/Sts2RestSiteIds.cs`
- `bridge-mod/src/Spirectl.Sts2/Common/Sts2TreasureRoomIds.cs`
- `bridge-mod/src/Spirectl.Sts2/Common/Sts2MapIds.cs`
- `bridge-mod/src/Spirectl.Sts2/Common/Sts2EventRoomIds.cs`
- `bridge-mod/src/Spirectl.Sts2/Common/Sts2CrystalSphereIds.cs`
- `bridge-mod/src/Spirectl.Sts2/Live/Sts2EventRoomScreenInspector.cs`
- `bridge-mod/src/Spirectl.Sts2/Live/Sts2RestSiteScreenInspector.cs`
- `cli/src/bridge/mod.rs`
- `cli/src/lib.rs`

## Secondary files

- `bridge-mod/src/Spirectl.Sts2/Core/Actions/PlaceholderActionHandler.cs`
- `cli/tests/dev_workflows.rs`
- `bridge-mod/tests/Spirectl.BridgeMod.Tests/GrpcBridgeServiceTests.RuntimeSceneActionsScreenshots.cs`
- `npm-wrapper/test/client.test.js`

## Supported now

- Handshake/catalog surfaces advertise the S85/S86 current action contract for combat/lobby verbs, fallback choose, and high-value non-combat intent verbs across reward/card, shop, rest-site, treasure/relic, map, and event-room groups, plus dangerous-mode `mouse-click`.
- Combat action ids are unified and game-native: the bridge action catalog, the compact `sts2 state` combat ids, and the action executor all use the same ids. `play-card.cardId` is the `NetCombatCardDb` id (= `NetPlayCardAction.card`), every `targetId` is `creature:{Creature.CombatId}` (= the action's `targetId`, resolved via `CombatState.GetCreature`), and `use-potion.potionId` is the potion slot index (= `NetUsePotionAction.potionIndex`, resolved via `Player.GetPotionAtSlotIndex`). These equal the state ids, so an agent can read `state` and act without an id-translation step. Card legality and target lists are computed live by the bridge; a downstream renderer can narrow candidate targets client-side from creature side plus alive state.
- Live execution exists for:
  - `play-card` for currently legal combat hand cards and legal targets
  - `use-potion` for currently usable combat potions and legal optional combat targets
  - S86 intent verbs: `claim-reward`, `skip-rewards`, `select-card`, `skip-card-selection`, `select-bundle`, `buy-card`, `buy-relic`, `buy-potion`, `remove-card`, `leave-shop`, `close-shop-inventory`, `rest`, `smith`, `use-rest-site-option`, `proceed-rest-site`, `open-chest`, `take-relic`, `proceed-treasure-room`, `back-from-map`, `select-event-option`, `open-event-shop`, `use-crystal-sphere-control`, `proceed-event`, and run top-bar `toggle-map`, `toggle-deck`, `toggle-settings`
  - combat card-pile viewers `view-draw-pile` / `view-discard-pile` / `view-exhaust-pile`: open the in-game `NCardPileScreen` by releasing the combat HUD `NCombatCardPile` button. The pile cards are already in `run.players[].combat.{draw,discard,exhaust}Pile.cards`; when the viewer is open, `run.view.capstone.cardPileView` reports `{ pileType, playerId }`. `view-exhaust-pile` is surfaced only when the exhaust pile is non-empty (the in-game button hides while empty)
  - in-hand selection `select-hand-card` / `deselect-hand-card` / `confirm-hand-selection`: drive `NPlayerHand`'s SimpleSelect/UpgradeSelect mode (a card effect asking for hand cards, e.g. Survivor's "Discard 1 card." or Armaments' upgrade pick) against `run.view.handSelection`. Selecting at `maxSelect` swaps the most recent pick (mirrors the UI); `deselect-hand-card` is rejected in upgrade-select mode (the UI swaps instead); confirm resolves the awaiting card effect with full multiplayer choice sync. There is intentionally no cancel action — the game offers no user-facing cancel. While selection is active the normal combat surface (`play-card`, `end-turn`, pile viewers, potion use) is suppressed, matching the in-game selection backstop.
  - `choose` for `menu:start-run` when the active main-menu screen resolves a callable hook and as explicit fallback for generic, modded, or unmodeled visible controls with no modeled `preferredAction`
  - `confirm-selection` for staged `simple-card-selection` and `deck-card-selection` overlays when the current overlay state exposes a legal follow-through path
  - `cancel-selection` for staged `simple-card-selection` and `deck-card-selection` overlays when the current overlay state exposes a legal staged-selection rollback path
  - `select-map-node` for currently travelable map nodes
  - `end-turn` during legal combat turns
  - `ready` / `unready` in multiplayer lobby flows
  - `select-character` in multiplayer start-run and load-run lobbies when visible character buttons are present
  - dangerous-mode `mouse-click` for raw viewport-coordinate fallback
- CLI, mock bridge, Node helper, MCP adapter, automation service, and the M75 in-process `ISpirectlRuntime.ExecuteAction` facade expose the same semantic protobuf action requests and structured action failure payloads; semantic CLI/AI/Node action calls can carry an optional local-scope `playerId` through `ActionRequest.perspective`, while dangerous raw input stays viewport-scoped and omitted from the AI catalog.
- Action descriptors include current `kindDescriptor`, `argumentSchema`, `perspectiveBehavior`, `checkedHookPaths`, and `failureReasonCodes`. Stable failure reasons include `wrong-screen`, `wrong-player`, `not-visible`, `not-enabled`, `missing-hook`, `ambiguous-hook`, `stale-id`, `unsupported-perspective`, and `dangerous-mode-required`.
- S87 action legality resolves the requested player, current owner, local player, host/remote role, screen, action kind, perspective, and remote-orchestration capability before execution. A semantic action for a remote-owned player is rejected with structured `wrong-player` or `unsupported-perspective` failure metadata unless an explicit configured client/bridge owns that player or the runtime reports a real host-mediated path for that action.
- S108 host-local seat orchestration is covered as an ownership distinction, not a remote-client shortcut: host-local seat actions may execute only when the requested player owns the visible action surface, wrong-player host-local mismatches still return `wrong-player`, and true remote players continue to return `unsupported-perspective` / remote-client-required diagnostics with requested/resolved/local/host player and capability metadata.
- Loaded multi-player combat fixtures preserve host-local seat ownership for action refs and execution checks. An explicitly authored host-local combat seat can become actionable when it owns the current action surface; true remote combat players remain non-actionable without configured ownership.
- Remote orchestration capability reporting is separate from action availability. `availableActions[]` describes what is legal now from the resolved perspective, while capability metadata explains whether remote execution is unavailable, local-only degraded, host-mediated, explicitly configured-client backed, or unsupported.
- The CLI command catalog and AI-tool metadata now call out S86 intent verbs and the load-run lobby `select-character` path, so `inspect commands`, `inspect examples`, and `inspect ai-tools` stay aligned with the shipped legality-backed action surface.
- Reward and card-selection surfaces now follow the same visible-vs-executable split as other supported runtime families: disabled reward buttons, skip/proceed controls, card picks, or alternative buttons can remain visible in `choices[]` while only executable intent entries appear in `availableActions[]` and family-specific notices explain the gap.
- Staged simple/deck card-grid overlays now split execution across `select-card` for card picks plus `confirm-selection` / `cancel-selection` for follow-through. Simple overlays clear staged picks on cancel, while deck overlays use preview-aware confirm/cancel behavior before falling back to clearing staged picks outside preview.

## Current limits

- Choice execution is intentionally narrow and documented as fallback choose. Visible target markers are not a general `choose` surface, and reward/map/event/treasure-room/rest-site/shop/card-selection compatibility IDs are now migration/fallback metadata rather than the preferred first-party action path.
- Action legality is screen-bound and derived from current live state.
- Main-menu `choose menu:start-run` now reports hook-probing diagnostics on runtime failure, including the active screen class, resolved hook path, ordered checked probe paths, and present candidates discovered during the shared legality inspection.
- Dangerous raw `mouse-click` is intentionally not part of `availableActions` and is only discoverable through dangerous-mode `supportedActions`.
- Broader combat and unmodeled/modded UI coverage remains intentionally partial; S87 hardens player ownership and wrong-player legality beyond the current current fields.
- One local bridge still cannot impersonate independent remote clients. Wrong-player and unsupported-perspective failures are expected success cases for callers that probe remote-owned controls without a configured client, and runners should assert those structured failures rather than retrying through raw input. Host-local seats are the bounded exception only when the current host bridge owns that local seat and the requested player matches the visible owner. Scenario restore diagnostics should likewise report remote-owned omissions as degraded local multiplayer instead of implying executable remote control.

## Primary validation

- `scripts/validate.sh bridge-tests --json`
- `cargo test -p sts2 --test test_runner non_combat_intent_actions`
- `scripts/validate.sh npm-wrapper-tests --json`
- `scripts/validate.sh bridge-live-host-tests --filter FullyQualifiedName~NonCombatIntentActions --json`
- `cargo run -p sts2 -- --json inspect actions`

## If you change actions, also update

- Proto action kinds, parameters, and field numbers in `proto/spirectl/v0/actions.proto`
- Handshake descriptors and status metadata in `bridge-mod/src/Spirectl.BridgeMod/BridgeRuntime.cs`
- Live legality/execution in `Sts2ActionCatalog.cs` and `Sts2ActionHandler.cs`
- Mock bridge parity in `cli/src/bridge/mock_data`
- CLI parsing/help/examples and AI tool metadata in `cli/src/lib.rs`
- Wrapper and MCP callers in `npm-wrapper/src/index.js`, `npm-wrapper/src/index.d.ts`, and `npm-wrapper/src/mcp-tool-executor.js`
- Tests in `cli/tests/dev_workflows.rs`, `cli/tests/cli_snapshots.rs`, `bridge-mod/tests/Spirectl.BridgeMod.Tests/GrpcBridgeServiceTests.RuntimeSceneActionsScreenshots.cs`, `bridge-mod/tests/Spirectl.BridgeMod.Tests/EmbeddableRuntimeFacadeTests.cs`, and Node wrapper/MCP tests
