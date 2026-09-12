# Testing

The repo now ships a first formal `sts2 test run` scenario runner for YAML and JSON documents, while still relying on the typed runtime bridge contract in both Rust and .NET.

## Authored Artifacts

- fixture: [fixtures/basic-combat.sts2.fixture.yaml](../fixtures/basic-combat.sts2.fixture.yaml)
- fixture: [fixtures/basic-lobby.sts2.fixture.yaml](../fixtures/basic-lobby.sts2.fixture.yaml)
- fixture: [fixtures/basic-load-run-lobby.sts2.fixture.yaml](../fixtures/basic-load-run-lobby.sts2.fixture.yaml)
- fixture: [fixtures/basic-map.sts2.fixture.yaml](../fixtures/basic-map.sts2.fixture.yaml)
- fixture: [fixtures/map-first-run.sts2.fixture.yaml](../fixtures/map-first-run.sts2.fixture.yaml) — act 1 first run, combat (not Neow) first node
- fixture: [fixtures/map-ancient-done.sts2.fixture.yaml](../fixtures/map-ancient-done.sts2.fixture.yaml) — act 1 after the Neow decision, the visited Neow node (un-ringed, like the game)
- fixture: [fixtures/map-act-2.sts2.fixture.yaml](../fixtures/map-act-2.sts2.fixture.yaml) — act-2 map room after the act-1 boss (act-2 boss art; act colors are a follow-up)
- fixture: [fixtures/map-travel.sts2.fixture.yaml](../fixtures/map-travel.sts2.fixture.yaml) — travelled to two nodes before the boss; the path shows every node kind incl. revealed `?` nodes
- fixture: [fixtures/basic-event-room.sts2.fixture.yaml](../fixtures/basic-event-room.sts2.fixture.yaml)
- fixture: [fixtures/basic-event-room-options.sts2.fixture.yaml](../fixtures/basic-event-room-options.sts2.fixture.yaml)
- fixture: [fixtures/event-remove-card.sts2.fixture.yaml](../fixtures/event-remove-card.sts2.fixture.yaml) — Precarious Shears chosen at the Neow ancient event; the relic's upon-pickup effect has opened the remove-two-cards dialog over the event
- fixture: [fixtures/treasure.sts2.fixture.yaml](../fixtures/treasure.sts2.fixture.yaml)
- fixture: [fixtures/treasure-opened.sts2.fixture.yaml](../fixtures/treasure-opened.sts2.fixture.yaml)
- fixture: [fixtures/basic-relic-selection.sts2.fixture.yaml](../fixtures/basic-relic-selection.sts2.fixture.yaml)
- fixture: [fixtures/basic-shop.sts2.fixture.yaml](../fixtures/basic-shop.sts2.fixture.yaml)
- fixture: [fixtures/basic-shop-inventory.sts2.fixture.yaml](../fixtures/basic-shop-inventory.sts2.fixture.yaml)
- fixture: [fixtures/basic-card-selection.sts2.fixture.yaml](../fixtures/basic-card-selection.sts2.fixture.yaml)
- fixture: [fixtures/basic-simple-card-selection.sts2.fixture.yaml](../fixtures/basic-simple-card-selection.sts2.fixture.yaml)
- fixture: [fixtures/basic-deck-card-selection.sts2.fixture.yaml](../fixtures/basic-deck-card-selection.sts2.fixture.yaml)
- fixture: [fixtures/basic-bundle-selection.sts2.fixture.yaml](../fixtures/basic-bundle-selection.sts2.fixture.yaml)
- fixture: [fixtures/basic-rewards.sts2.fixture.yaml](../fixtures/basic-rewards.sts2.fixture.yaml)
- fixture: [fixtures/rest-site.sts2.fixture.yaml](../fixtures/rest-site.sts2.fixture.yaml)
- fixture: [fixtures/two-ironclad-nibbits-weak-combat.sts2.fixture.yaml](../fixtures/two-ironclad-nibbits-weak-combat.sts2.fixture.yaml)
- fixture: [fixtures/combat-card-strike.sts2.fixture.yaml](../fixtures/combat-card-strike.sts2.fixture.yaml)
- fixture: [fixtures/combat-card-defend.sts2.fixture.yaml](../fixtures/combat-card-defend.sts2.fixture.yaml)
- fixture: [fixtures/combat-orbs.sts2.fixture.yaml](../fixtures/combat-orbs.sts2.fixture.yaml) — Defect with authored orbs (lightning + plasma + empty slot) plus an Ironclad ally holding one frost orb
- fixture: [fixtures/combat-discard.sts2.fixture.yaml](../fixtures/combat-discard.sts2.fixture.yaml) — Silent mid-Survivor discard selection
- scenario: [tests/scenarios/fixture-combat-orbs.sts2.yaml](../tests/scenarios/fixture-combat-orbs.sts2.yaml)
- scenario: [tests/scenarios/fixture-basic-combat.sts2.yaml](../tests/scenarios/fixture-basic-combat.sts2.yaml)
- scenario: [tests/scenarios/fixture-basic-lobby.sts2.yaml](../tests/scenarios/fixture-basic-lobby.sts2.yaml)
- scenario: [tests/scenarios/fixture-basic-load-run-lobby.sts2.yaml](../tests/scenarios/fixture-basic-load-run-lobby.sts2.yaml)
- scenario: [tests/scenarios/fixture-basic-map.sts2.yaml](../tests/scenarios/fixture-basic-map.sts2.yaml)
- scenario: [tests/scenarios/fixture-basic-event-room.sts2.yaml](../tests/scenarios/fixture-basic-event-room.sts2.yaml)
- scenario: [tests/scenarios/fixture-basic-event-room-options.sts2.yaml](../tests/scenarios/fixture-basic-event-room-options.sts2.yaml)
- scenario: [tests/scenarios/fixture-event-remove-card.sts2.yaml](../tests/scenarios/fixture-event-remove-card.sts2.yaml)
- scenario: [tests/scenarios/fixture-treasure.sts2.yaml](../tests/scenarios/fixture-treasure.sts2.yaml)
- scenario: [tests/scenarios/fixture-treasure-opened.sts2.yaml](../tests/scenarios/fixture-treasure-opened.sts2.yaml)
- scenario: [tests/scenarios/fixture-basic-relic-selection.sts2.yaml](../tests/scenarios/fixture-basic-relic-selection.sts2.yaml)
- scenario: [tests/scenarios/fixture-basic-shop.sts2.yaml](../tests/scenarios/fixture-basic-shop.sts2.yaml)
- scenario: [tests/scenarios/fixture-basic-shop-inventory.sts2.yaml](../tests/scenarios/fixture-basic-shop-inventory.sts2.yaml)
- scenario: [tests/scenarios/fixture-basic-card-selection.sts2.yaml](../tests/scenarios/fixture-basic-card-selection.sts2.yaml)
- scenario: [tests/scenarios/fixture-basic-simple-card-selection.sts2.yaml](../tests/scenarios/fixture-basic-simple-card-selection.sts2.yaml)
- scenario: [tests/scenarios/fixture-basic-deck-card-selection.sts2.yaml](../tests/scenarios/fixture-basic-deck-card-selection.sts2.yaml)
- scenario: [tests/scenarios/fixture-basic-bundle-selection.sts2.yaml](../tests/scenarios/fixture-basic-bundle-selection.sts2.yaml)
- scenario: [tests/scenarios/fixture-basic-rewards.sts2.yaml](../tests/scenarios/fixture-basic-rewards.sts2.yaml)
- scenario: [tests/scenarios/fixture-basic-rest-site.sts2.yaml](../tests/scenarios/fixture-basic-rest-site.sts2.yaml)
- scenario: [tests/scenarios/fixture-two-ironclad-nibbits-weak-combat.sts2.yaml](../tests/scenarios/fixture-two-ironclad-nibbits-weak-combat.sts2.yaml)
- scenario: [tests/scenarios/deploy-and-fixture-smoke.sts2.yaml](../tests/scenarios/deploy-and-fixture-smoke.sts2.yaml)
- scenario: [tests/scenarios/diagnostics-main-menu.sts2.yaml](../tests/scenarios/diagnostics-main-menu.sts2.yaml)
- scenario: [tests/scenarios/log-health-main-menu.sts2.yaml](../tests/scenarios/log-health-main-menu.sts2.yaml)
- scenario: [tests/scenarios/visual-main-menu.sts2.yaml](../tests/scenarios/visual-main-menu.sts2.yaml)
- scenario: [tests/scenarios/regression-main-menu.sts2.yaml](../tests/scenarios/regression-main-menu.sts2.yaml)
- scenario: [tests/scenarios/smoke-main-menu.sts2.yaml](../tests/scenarios/smoke-main-menu.sts2.yaml)
- scenario: [tests/scenarios/smoke-main-menu-live.sts2.yaml](../tests/scenarios/smoke-main-menu-live.sts2.yaml)
- snapshot spec: [tests/snapshots/main-menu.sts2.snapshot.yaml](../tests/snapshots/main-menu.sts2.snapshot.yaml)
- snapshot baseline: [tests/snapshots/baselines/main-menu/snapshot.json](../tests/snapshots/baselines/main-menu/snapshot.json)

The intent is:

- fixtures stay declarative and diff-friendly
- combat fixtures may model multiple locally controlled host seats only when each non-primary local seat uses explicit S108 host-local metadata. The deterministic example is [fixtures/two-ironclad-nibbits-weak-combat.sts2.fixture.yaml](../fixtures/two-ironclad-nibbits-weak-combat.sts2.fixture.yaml), validated by [tests/scenarios/fixture-two-ironclad-nibbits-weak-combat.sts2.yaml](../tests/scenarios/fixture-two-ironclad-nibbits-weak-combat.sts2.yaml): both players are authored as Ironclad local seats and the room resolves `encounterId: NIBBITS_WEAK`, not `NIBBITS_NORMAL`.
- runner scenarios describe user or AI workflows in `.sts2.yaml` or `.sts2.json` and can be executed through `sts2 test run`; `*.sts2.scenario.yaml` sparse artifacts use the separate `spirectl.scenario/v0` restore contract and can be loaded from runner workflows with `dev.load-scenario`, preserving the full field-level restore validation payload, field-level mismatches, suggested next steps, and optional `multiplayerRestore` in step artifacts
- `sts2 test run --profile <name>` uses project-defined `test.profiles.<name>` config, so names such as `mock`, `live`, `ci`, or `release` are repo policy rather than hardcoded CLI behavior
- `smoke-main-menu.sts2.yaml` stays as the lightweight mock baseline when run with `--config tests/sts2.mock.yaml` or `--profile mock`, while `smoke-main-menu-live.sts2.yaml` is tagged for explicit live validation and starts by loading the authored main-menu fixture so profile runs do not depend on prior scenario state
- snapshot specs describe bounded regression captures and can be exported/compared through `sts2 dev snapshot *` or reused inside `test run`
- `dev fixture load` now executes additive authored `main-menu`, `combat`, `map`, `rewards`, `rest-site`, `event-room`, `treasure-room`, `relic-selection`, `shop`, `card-selection`, `simple-card-selection`, `deck-card-selection`, `bundle-selection`, `Screens.CharacterSelect.NCharacterSelectScreen`, and `Screens.CharacterSelect.NMultiplayerLoadGameScreen` fixture slices through the live bridge while keeping the fixture schema recipe-based and diff-friendly; the M43 slice now includes authored load-run lobby setup plus explicit event options, opened treasure-room proceed flow, and shop inventory/card-removal overrides; combat fixtures can also author per-player orbs (`players[].orbs` + `orbSlotCount`), which channel into the live orb queue after combat entry (clearing any combat-start orb so the authored set is exact) and are not gated on Defect, so allies can hold orbs too
- runner outputs are persisted into predictable artifact directories for later debugging or CI inspection
- target-screen screenshot and diff scenarios live in [tests/scenarios/couchcoop-target-screenshots.sts2.yaml](../tests/scenarios/couchcoop-target-screenshots.sts2.yaml) and [tests/scenarios/couchcoop-target-visual-diff.sts2.yaml](../tests/scenarios/couchcoop-target-visual-diff.sts2.yaml). They load the target lobby, initial Neow, and two-player `NIBBITS_WEAK` fixtures, then exercise generic `dev.screenshot` and `dev.screenshot-diff` at `1280x720`. Generated PNGs, actuals, diffs, and comparison JSON bundles are written under the normal ignored `.sts2/` artifact tree and must not be committed.

## Fixture Schema (spirectl.fixture/v0)

Authored fixtures mirror the state snapshot shape with exact state field
names (`run`, `characterSelect`, `run.players[].characterId`,
`run.players[].creature.currentHp`, `run.currentRoom.{combat|event|treasure|shop|restSite|mapRoom}`,
`run.view.playerId`, `run.players[].overlays[]`). The legacy
`spirectl.fixture/v0` schema (top-level `screen`/`perspective`/`players` plus
per-screen sections) is rejected with a migration error.

Key rules:

- The loader recipe is derived from structure, never authored: `characterSelect`
  selects the lobby recipes (via fixture-only `characterSelect.kind:
  start-run|load-run`), `rootScene: main-menu` selects the main menu,
  `run.currentRoom.*` selects the room recipes (`treasure.relicSelectionOpen:
  true` opens the choose-a-relic screen), and a single
  `run.players[].overlays[]` entry selects the rewards/selection/card-overlay
  recipes (`deckCardSelection.kind: upgrade|transform|enchant|select`,
  `cardOverlay.policy: passive` derives passive-card-overlay). A rest-site room
  may compose one card-less `deckCardSelection` overlay (the smith dialog).
- The `rewards` overlay authors the rewards screen item by item. `rewards.items[]`
  is a tagged-union list — each entry is exactly one of `gold: <n>`,
  `card: {modelIds: [...]}` or `card: {roomType, count}` (specific cards, any
  rarity, vs. a pooled choice; `canSkip: false` makes it non-skippable),
  `specialCard: {modelId}` (a single specific card), `potion: {modelId}` (or
  `potion: {}` for random), `relic: {modelId}` / `{rarity}` / `{}`,
  `cardRemoval: true`, or `linked: {items: [...]}` (a LinkedRewardSet). An empty
  `rewards: {}` falls back to a single 30-gold reward. `rewards.roomType`
  (`monster|elite|boss`, default `elite`) enters a synthetic finished combat room
  so granted reward-modifying relics' hooks fire against the authored rewards
  (e.g. Amethyst Aubergine adds gold, Prayer Wheel/White Star add a card reward,
  the eggs upgrade card-reward cards, Driftwood adds reroll, Pael's Wing adds the
  Sacrifice alternative). The game caps card-reward alternatives at 2, so combine
  reroll- and alternative-adding relics with `card.canSkip: false`. See
  `fixtures/rewards-all.sts2.fixture.yaml` and
  `fixtures/rewards-all-with-relics.sts2.fixture.yaml`.
- Defaults keep fixtures minimal: `run.players` defaults to one Ironclad
  (`p:1`), `run.view.playerId` to the single/host-owned local player,
  `run.currentActIndex` to `0`, `run.actFloor` to `1`, and seeds to the fixture
  name (author seeds explicitly when generated content matters — shop
  inventories and maps are seed-dependent). `characterSelect.lobby` has matching
  lobby defaults.
- `run.players[].deck.cards` APPENDS the authored cards to the character's
  starter deck on load (live recipe behavior); it does not replace the deck the
  way state reports it. `run.players[].relics`/`potions` grant before room
  entry so rest-site hooks fire.
- Couch-coop authoring keeps fixture-only fields with no state equivalent:
  `isHostLocalSeat`/`slotId` on run players, `characterSelect.kind`,
  `treasure.relicSelectionOpen`, and the
  `simpleCardSelection`/`bundleSelection`/`cardOverlay` overlay payloads.
- `run.view.selectedCard: {playerId?, cardModelId}` pre-selects a combat hand
  card in spirectl view state (`state.run.view.selectedCard`, the entry the
  `select-card` action toggles). `playerId` defaults to the view player; the
  loader picks the first opening-hand card matching `cardModelId` and fails the
  load if the seeded hand lacks it (adjust `run.seed`). State reports the
  resolved runtime card id, not the model id.
- `run.view.inspectRelic: {relicModelId}` opens the relic-details overlay
  (NInspectRelicScreen, the screen the `inspect-relic`/`close-inspect-relic`
  actions drive) on the view player's relic-bar relic matching `relicModelId`
  — the starter relic or one of the authored `relics[]`. State reports the
  resolved browse position (`state.run.view.inspectRelic.{relicModelId,index,count}`).

## Test Profiles

Profiles are declared in config under `test.profiles`:

```yaml
test:
  profiles:
    mock:
      description: Deterministic mock scenario baseline.
      config:
        transport:
          kind: mock
          mockScenario: main-menu
      paths:
        - tests/scenarios/smoke-main-menu.sts2.yaml
      excludeTags:
        - liveValidation
    live:
      description: Real-game validation gate.
      config:
        transport:
          kind: ipc
      paths:
        - tests/scenarios
      includeTags:
        - liveValidation
      preflight:
        deploy:
          path: mods/MyMod
          build: true
          restart: true
          verify: true
        timeoutMs: 60000
        intervalMs: 500
        verifyStableMs: 2000
      gate:
        requireMatchingScenario: true
        allowNotApplicable: true
        requireLiveTransport: true
```

Common commands:

```bash
sts2 --json test run --profile mock
sts2 --json test run --profile live
sts2 --json test run --profile live --tag spec:S74
sts2 --json test run --profile live tests/scenarios/fixture-fake-merchant.sts2.yaml
```

When a path is supplied with `--profile`, the path replaces profile `paths` but the profile config overlay, lifecycle preflight, tag filters, and gate policy still apply. `test run` without `--profile` keeps the previous behavior and uses the active config directly.

Profile preflight can install the bridge, deploy one mod, launch, or attach before scenario discovery. `preflight.deploy` uses the same fields as `game.deploy` and resolves its `path` relative to the config directory, so expensive mod deploy/restart work can run once per profiled run instead of once per scenario.

Scenario metadata can opt into live validation with tags:

```yaml
name: fixture-fake-merchant
tags:
  - liveValidation
  - spec:S74
steps:
  - dev.load-fixture:
      path: ../../fixtures/fake-merchant-pre-open.sts2.fixture.yaml
```

If a spec has no runtime behavior worth validating against the live game, use an explicit not-applicable declaration instead of leaving the live gate silent:

```yaml
name: docs-only-spec
tags:
  - spec:S99
liveValidation:
  status: notApplicable
  reason: "Static docs-only spec; no live runtime behavior changes."
steps:
  - game.info
```

The repo `live` profile rejects a resolved mock transport, fails when preflight cannot prepare the real game/bridge, fails matching scenario errors, and fails missing live validation unless a selected scenario has valid `notApplicable` metadata and the profile gate allows it.

The current runner subset is intentionally small:

- supported steps: `game.info`, `game.deploy`, `dev.console`, `dev.load-fixture`, `dev.fixture.load`, `dev.load-scenario`, `dev.logs`, `dev.log-health`, `dev.diagnostics`, `dev.hot-reload`, `dev.debug-events`, `dev.delay`, `dev.assert`, `dev.http`, `dev.http-wait`, `dev.fetch`, `dev.websocket`, `project.hook`, `dev.wait-for`, `dev.screenshot`, `dev.screenshot-diff`, `dev.snapshot-compare`, `act.play-card`, `act.use-potion`, `act.confirm-selection`, `act.cancel-selection`, `act.select-map-node`, `act.end-turn`, `act.ready`, `act.unready`, `act.select-character`, and fallback choose through `act.choose`
- S86 non-combat intent steps are supported as first-party actions: `act.claim-reward`, `act.skip-rewards`, `act.select-card`, `act.skip-card-selection`, `act.select-bundle`, `act.buy-card`, `act.buy-relic`, `act.buy-potion`, `act.remove-card`, `act.leave-shop`, `act.close-shop-inventory`, `act.rest`, `act.smith`, `act.use-rest-site-option`, `act.proceed-rest-site`, `act.open-chest`, `act.take-relic`, `act.proceed-treasure-room`, `act.back-from-map`, `act.select-event-option`, `act.open-event-shop`, `act.use-crystal-sphere-control`, and `act.proceed-event`
- action steps follow the current action/choice contract: assert typed state sections first, read `preferredAction` from visible choices when present, then execute the named intent step; reserve fallback choose for generic, modded, or unmodeled visible choices with no modeled `preferredAction`
- compatibility aliases: underscore/hyphen spelling variants plus `assert.query -> dev.assert`
- input sources: checked-in `.sts2.yaml` or `.sts2.json` files, directories of scenarios, one `--inline` YAML/JSON scenario body, or profile-provided paths through `--profile`
- `dev.hot-reload` resolves `project` relative to the scenario file for file-backed scenarios and relative to `cwd` for inline/object scenarios; `expectGenerationChanged` requires `wait: true` so the runner can compare a completed shell generation report
- `dev.console` accepts structured `command` plus optional `args`, uses the run's global CLI mode, and persists successful output as `steps/NNN-dev-console.json`; dangerous console commands such as `achievement`, `cloud`, and `unlock` require `--mode dangerous`
- `dev.debug-events` accepts `session`, `fromSequence`, `limit`, `follow`, `timeoutMs`, and optional `expectEventKind`, delegates to `dev debug events`, and persists successful transcripts as `steps/NNN-dev-debug-events.json`; debugger hooks are required only for this debugger-specific step, so ordinary `state`, `act`, `dev.assert`, and `dev.wait-for` runner validation remains independent of debugger stream support
- For manual S91 transcript checks, run the focused debugger workflow test or a scenario using [tests/scenarios/debugger-event-streams.sts2.yaml](../tests/scenarios/debugger-event-streams.sts2.yaml), then inspect the persisted `steps/NNN-dev-debug-events.json` artifact for event `sequence` values, `nextSequence`, `oldestRetainedSequence`, `newestSequence`, `retention.limit`, session/lease role metadata, notices, and any disconnect/drop/overflow evidence before treating the transcript as complete.
- `sts2 test stress` repeats one file or directory input under either an iteration cap or duration cap, persists one aggregate summary under `artifacts/test-stress/`, and references each nested `test run` summary/root instead of inventing a second runner protocol
- rejected for now: the old `state.expect` mini-DSL

Hot-reload runner step example:

```yaml
name: hot-reload-loop
steps:
  - dev.hot-reload:
      project: ../mods/MyHotMod
      build: true
      wait: true
      expectGenerationChanged: true
```

Action migration example:

```yaml
name: non-combat-intent-actions
steps:
  - dev.load-fixture:
      path: ../../fixtures/basic-rewards.sts2.fixture.yaml
  - act.claim-reward:
      reward: reward:p1:0
  - dev.load-fixture:
      path: ../../fixtures/basic-card-selection.sts2.fixture.yaml
  - act.select-card:
      card: card-selection:card:bash:0
  - dev.load-fixture:
      path: ../../fixtures/basic-shop.sts2.fixture.yaml
  - act.buy-card:
      shopItem: shop:p1:card:strike:0
  - act.leave-shop
  - dev.load-fixture:
      path: ../../fixtures/rest-site.sts2.fixture.yaml
  - act.rest
  - act.use-rest-site-option:
      restOption: heal
  - dev.load-fixture:
      path: ../../fixtures/treasure.sts2.fixture.yaml
  - act.open-chest
  - act.take-relic:
      relic: anchor
  - dev.assert:
      path: map.nodes[id=map-node:3:1].travelable
      equals: true
  - act.select-map-node:
      node: map-node:3:1
  - act.back-from-map
  - dev.load-fixture:
      path: ../../fixtures/basic-event-room.sts2.fixture.yaml
  - act.select-event-option:
      eventOption: event-room:gain-gold:0

  # fallback choose remains valid for generic, modded, or unmodeled visible choices only
  - act.choose:
      choice: modded-choice:custom-visible-button
```

The focused S86 runner target is:

```bash
cargo test -p sts2 --test test_runner non_combat_intent_actions
```

Live-host regression for the same action family is:

```bash
scripts/validate.sh bridge-live-host-tests --filter FullyQualifiedName~NonCombatIntentActions --json
```

That live-host gate may return structured `environment_blocked` when local STS2 assemblies, live-host build flags, or game prerequisites are absent. Treat that as an explicit environment result to preserve in validation output, not as a reason to replace the command with a broader raw test.

## Current Automated Tests

Rust:

- command parsing
- static-inspection command parsing for `locate`, `describe`, `refs`, `derived`, and `decompile`
- `toolchain info`, `project recover`, `project scaffold stable-harmony-trampoline`, and `project profile *` command parsing plus config/provenance coverage
- `assets extract` command parsing plus helper-backed mixed filesystem/packed discovery coverage, offline `source` and `raster` export coverage, attached live IPC success, literal combat background exact-path rendering, deterministic composed `composed://combat-background/<id>/image` alias coverage, `assets explain` parser and IPC-backed fixed-response coverage for live combat-background aliases, individual combat layer scene regression coverage, packed-raster offline export, timeline-manifest writing, auto-format preservation, per-export extraction timing, shared batch asset indexing and batch timing diagnostics (`indexMs`, `resolveMs`, `liveSetupMs`, `exportMs`), auto-launch/install fallback, artifact-tree exclusion when artifacts live under the resources root, CLI-side transcoding when the bridge returns PNG bytes for an explicit WebP request, and `assets extract-batch --dry-run` probing without artifact writes
- config parsing for live bridge game paths
- static-inspection path-resolution coverage for config fallback and explicit assembly roots
- snapshot-style JSON assertions for `inspect commands`
- snapshot-style JSON assertions for `inspect examples`
- snapshot-style JSON assertions for `inspect ai-tools`
- snapshot-style JSON assertions for `inspect actions`
- snapshot-style JSON assertions for `state`
- snapshot-style JSON assertions for the map-screen state/action slice
- snapshot-style JSON assertions for the event-room state/action slice
- snapshot-style JSON assertions for the treasure-room state/action slice
- snapshot-style JSON assertions for the rest-site state/action slice
- snapshot-style JSON assertions for additive choice ownership via `choices[*].ownerPlayerId` on player-scoped runtime screens
- snapshot-style JSON assertions for richer multiplayer lobby state including visible local `choices[]`, `playersById`, `availableCharactersById`, `localPlayerId`, `hostPlayerId`, and `localPlayerRole`
- S87 runner coverage for mock multiplayer ownership scenarios, including host-owned, local non-host, remote-owned visible controls, local-only degraded restore metadata, explicit perspective assertions, and structured wrong-player / unsupported-perspective action failures
- snapshot-style JSON assertions for `dev logs`
- direct command coverage for `dev log-health` unhealthy/filtered success paths, `dev mod-reload` metadata/build/failure mapping, and `dev diagnostics` bundle-writing/partial-capture behavior, including viewport-preset passthrough and optional hot-reload status capture for diagnostics-owned evidence
- mock screenshot capture coverage for `dev screenshot`, explicit preset catalogs, and `dev screenshot-diff` live/offline bundle generation
- local fixture parsing/normalization plus bridge success/error coverage for `dev fixture load`, including additive `room` / `lobby` / `eventRoom` / `treasureRoom` / `shop` / `selection` recipe sections, `run.ascensionLevel` for combat fixtures, executable `map` / `rewards` / `rest-site` / `event-room` / treasure-family / shop / card-selection-family recipe coverage, host-local multiplayer treasure-room fixtures (mirroring the event-room host-local contract: every player `isLocal`/`slotId`/`isHostLocalSeat`, exactly one non-host seat matching `perspective.playerId`) plus authored `treasureRoom.playerVotes[]` (`playerId` + optional 0-based relic `index`; only valid on opened treasure rooms, applied live via the relic vote synchronizer so authored players already show `voteReceived: true`), local rejection of impossible authored values such as `run.act < 1`, `run.floor < 1`, `players[].hp > players[].maxHp`, malformed lobby player ids, unsupported `eventRoom.canProceed` and `shop.canLeave`, `treasureRoom.playerVotes` on closed/relic-selection chests, and canonicalization of whitespace-padded ids / blank seeds before IPC
- runner `dev.screenshot` coverage with scenario-local default artifact paths plus `dev.screenshot-diff` live/offline and `dev.snapshot-compare` bundle persistence
- `test run` command parsing plus `inspect` catalog snapshot coverage
- `service serve` integration coverage for startup metadata, auth gating, AI-tool catalog parity, representative remote tool calls, async remote `test run` jobs, and bounded artifact download
- `test stress` command parsing plus aggregate pass/fail iteration coverage
- `dev debug session start|status|end`, session-aware `dev debug status|pause|resume|step|wait`, and `dev breakpoint list|add|remove` CLI coverage for the explicit bridge-backed live-debug surface, including lease ownership, richer breakpoint kinds, and query-backed registration/removal flows
- script-friendly `dev assert` success and failure coverage
- multiplayer-oriented query coverage for by-id lobby paths, array wildcards, nested/object-wide array-filter selectors, and enriched missing-path failure payloads
- script-friendly `dev wait-for` timeout coverage
- IPC-backed `dev wait-for` success coverage against a state-changing test bridge service plus stuck-state-RPC timeout coverage against a socket that accepts without responding
- `game detect` configured/detected status reporting plus live-bridge path reporting
- `game install-bridge` packaging/install coverage, `game launch` auto-heal coverage for missing, invalid, stale, and incomplete deployed bridge layouts, and lifecycle shutdown coverage for `game close` bridge/configured-command paths plus exact Linux `game kill` process termination
- `project recover --kind decompile` integration coverage for owned output/manifest writing plus `--kind all` partial-success/nonzero behavior when GDRE is unavailable
- `project scaffold stable-harmony-trampoline` scaffold output/refusal/validation coverage, generated profile assertions, and generated MSBuild hot-reload copy/clean target coverage
- `project profile list` / `show` checked-in profile loading coverage, including command-step cwd/env interpolation, plus `project profile run` fail-fast step reporting and command-step execution coverage
- Linux launcher-derivation coverage for native `SlayTheSpire2`, ambiguity reporting, `launch_timeout`, and `launch_exited_before_ipc`
- live transport error taxonomy coverage for Unix-socket-specific failures plus additive `transport_misconfigured` / `transport_connection_failed` cases
- typed transport error coverage for `act` and unavailable `tcp`
- successful and failing semantic action coverage on the mock bridge, including combat/lobby verbs, S86 reward/card, shop, rest-site, treasure/relic, map, and event-room intent verbs, fallback `act.choose`, and dangerous `act mouse click`
- reward-screen `act.claim-reward` / `act.skip-rewards` mock coverage with stable `reward:<player-id>:<rewards-set-index>` ids
- event-room `act.select-event-option` / `act.open-event-shop` / `act.use-crystal-sphere-control` / `act.proceed-event` mock coverage with stable first-party event and Crystal Sphere ids
- treasure-room `act.open-chest` / `act.take-relic` / `act.proceed-treasure-room` mock coverage
- rest-site `act.rest` / `act.smith` / `act.use-rest-site-option` / `act.proceed-rest-site` mock coverage
- shop-screen buy/remove/leave/close mock coverage with stable `shop:<player-id>:<kind>:...` ids
- card-selection `act.select-card` / `act.skip-card-selection` / `act.select-bundle` mock coverage
- scenario parser coverage for alias normalization, inline YAML input, bare no-arg steps, `game.deploy`, `dev.console`, malformed YAML, unsupported runner steps, and rejection of legacy `state.expect`
- `test run` integration coverage for persisted `summary.json`, per-scenario `result.json`, per-step deploy/console/load-fixture/load-scenario/hot-reload/log-health/diagnostics artifacts, full scenario-load validation JSON preservation including degraded multiplayer restore output, explicit `failure.phase: setup` classification for `game.deploy` / `dev.load-fixture` / `dev.load-scenario` failures, scenario-local screenshot paths, inline scenario execution, `.sts2.json` scenario execution, file-backed and inline `game.deploy` path resolution, `dev.console` mode gates, `dev.load-fixture`, `dev.load-scenario`, and `dev.hot-reload` path resolution for file-backed versus inline/object scenarios, failure-time `failure/evidence/diagnostics.json` bundle generation, `--artifacts-dir` override behavior, `--failure-artifacts never`, improved human summary output, partial artifact-collection failures, mixed `.sts2.yaml` / `.sts2.json` directory execution order, IPC-backed wait-for success, wait-for timeout, wait-for bridge RPC timeout, aggregate exit-code behavior, and lobby/map action steps
- generated protobuf client/server contract coverage against the stub bridge service
- mock scenario coverage for `main-menu`, `combat`, `map`, `event-room`, `treasure-room`, `rest-site`, `shop`, `rewards`, `card-selection`, `lobby`, and `lobby-ready`
- standalone live-transport client coverage against the framed protocol over Unix-domain sockets and loopback TCP via fake bridge services
- structured action-argument, richer combat-state coverage, and shared bridge-version coverage for the mock contract
- real `code` command coverage against a deterministic .NET fixture assembly, including helper-error passthrough plus `refs` / `derived`
- real `code scene-*` coverage against deterministic text, binary, and packed Godot fixture assets, including provenance fields, duplicate-collapse notes, and helper-error passthrough for malformed binary files
- real `code verify-references` coverage against a synthetic control/candidate game-assembly pair plus a consumer compiled against the control, exercising the clean verdict, the broken verdict with all four buckets, and the nonzero exit codes

Node:

- repo-local `npm-wrapper/` client coverage for `gameDetect()`, `gameInfo()`, `toolchainInfo()`, `gameLaunch()`, `gameAttach()`, `gameDeploy()`, `logHealth()`, `diagnostics()`, `assetsExtract()`, `assetsExplain()`, `skillInstall()`, `state()`, `logs()`, `inspectViewportPresets()`, `inspectReferenceTopics()`, and `inspectAiTools()` success-path JSON parsing or argv shaping against the real/fake CLI as appropriate
- repo-local `npm-wrapper/` client coverage for direct `loadFixture()`, `hotReloadStatus()`, `hotReload()`, `scenarioExport()`, `scenarioLoad()` including degraded multiplayer opt-in, `http()`, `httpWait()`, `fetch()`, `websocket()`, `projectProfileList()`, `projectProfileShow()`, `projectProfileRun()`, `projectHookList()`, `projectHookShow()`, `projectHookRun()`, `debugStatus()`, `debugPause()`, `debugResume()`, `debugStep()`, `breakpointList()`, `breakpointAdd()`, `breakpointRemove()`, `screenshot()`, `screenshotDiff()`, `codeHooks()`, and `codeHookInfo()` helper argv shaping plus wrapper-side required-argument validation
- repo-local `npm-wrapper/` client coverage for `assert()` and `waitFor()` failure-path handling against the real CLI
- repo-local `npm-wrapper/` client coverage for `inspectAiTools()`, `testRun()` path and inline/object scenario inputs, `testStress()`, `snapshotExport()`, `snapshotCompare()`, the expanded lobby/map/shop action variants, and dangerous-mode raw mouse clicks
- fake-binary coverage proving the wrapper preserves `stdout`, `stderr`, `argv`, and parsed JSON error payloads for nonzero exits
- MCP adapter coverage for promoted `debug_*`, `breakpoint_*`, and hot-reload tool dispatch plus structured CLI failure preservation through the thin wrapper layer
- wrapper binary-resolution coverage for explicit `binaryPath`, `STS2_BINARY_PATH`, external `PATH`, verified release cache/download, and repo-local build fallbacks
- wrapper packaging coverage proving `cd npm-wrapper && npm pack --dry-run --json` publishes JavaScript without a staged native binary
- Playwright fixture coverage for `withSts2(...)`, `hotReloadAndWait(...)`, typed failure-artifact capture, predictable `sts2-diagnostics.json` plus `sts2-evidence/` output paths, and failure-artifact screenshot viewport passthrough into diagnostics capture
- MCP adapter coverage for stdio tool registration against the CLI catalog, promoted `toolchain_info`, `project_profile_show`, `diagnostics`, `screenshot_diff`, `snapshot_export`, `snapshot_compare`, `test_stress`, `inspect_viewport_presets`, `code_hook_info`, and `inspect_reference_topics` dispatch via the wrapper client, promoted `load_fixture` / `skill_install` / `project_hook_run` structured failure preservation, and synthetic adapter-only error preservation

.NET:

- shared bridge build metadata coverage
- bridge bootstrap perspective defaults
- bridge runtime host wiring
- gRPC bridge service coverage for handshake, state, actions, fixture loading, logs, bridge-version consistency, query-backed breakpoint validation/evaluation, and the debug-control surface
- gRPC bridge service coverage for screenshot capture metadata, dedicated asset extraction/explain metadata, and scaffolded screenshot/asset failures
- protobuf mapping coverage for action status metadata, action parameters, richer lobby/map serialization, select-character / select-map-node / use-potion requests, and dangerous-mode mouse-click handshake exposure
- structured action success mapping and structured invalid-action mapping coverage
- bounded recent-log buffer ordering and eviction coverage
- perspective override coverage
- runtime-state mapper coverage for main menu, combat, local perspective, omniscient perspective, and richer lobby perspective/by-id mapping
- STS2 host common-surface coverage for endpoint resolution, hosted status updates, shared framed-protocol server paths, saved-run snapshot resolution, live-gated fixture-loader helper surface, stable event-room/treasure-room/rest-site choice ids, deterministic combat background alias/path/frame/render helper coverage, and shared action-availability helpers including lobby/map/event-room/treasure-room/rest-site action legality
- representative structured error coverage for `not_implemented`, `invalid_action`, `invalid_query_filter`, and bridge-side `invalid_fixture` request validation
- helper tool `locate`, `describe`, `refs`, `derived`, `hooks`, `hook-info`, and both metadata/full `decompile` outputs against a deterministic test-symbol assembly
- helper tool `decompile-export` coverage for deterministic ILSpy corpus writing against the same deterministic test-symbol assembly
- structured no-match, ambiguous-query, exact-method, reference-navigation, inheritance-navigation, metadata-decompile, ILSpy-full decompile, include-mods, and text/binary/packed `scene-*` coverage for static inspection

The live STS2 host adapter itself still needs a local game assembly path for in-game manual verification; repo CI continues to rely on the mock transport plus fallback host build. The lifecycle path is now CLI-native, but launch/restart still depends on a Linux-capable install plus explicit config when executable derivation is ambiguous, and the Godot-backed live asset provider remains under the same compile gate as the rest of the STS2 host adapter.

S87/S108 ownership assertions should be written against metadata, not prose. Representative runner checks include `dev.assert` paths such as `choices[id=target:e_1].ownerPlayerId`, `combat.playersById.p1.isLocal`, `lobby.hostPlayerId`, `availableActions[id=<host-local-action>].ownerRole`, and capability fields on the active typed section or action refs. Wrong-player and unsupported-perspective probes should assert the preserved error payload directly: `actionFailure.reasonCode`, requested player, resolved owner, local player, host player, local role, action, and `remoteOrchestration.state`. Host-local positive checks should use the existing expected-failure artifact pattern for mismatches and a normal runner pass only when the requested host-local seat owns the visible surface. Degraded local-only scenario restore should assert the `multiplayerRestore` status/notices and `validation.mismatches[].supportClass == degraded-local-multiplayer` where applicable instead of treating remote players as controllable.

For bridge-mod edits, keep the current verification split explicit:

- `cli/tests/game_lifecycle.rs::game_install_bridge_builds_and_installs_bridge_only` validates the CLI packaging/install flow with a fake `dotnet` binary.
- The whole `cli/tests/game_lifecycle/*` suite (deploy build freshness, launch stdio capture, quiescence waits, close/kill process matching, install-bridge staging) now runs as the last step of `scripts/validate.sh cli-tests --json`, so lifecycle regressions surface without a separate `cargo test -p sts2 --test game_lifecycle`. It needs no live host: the bridge is a stub over a Unix socket and `dotnet` is a fake on `PATH`.
- Bridge-mod .NET builds/tests can resolve the live STS2 assemblies root from `STS2_ASSEMBLIES_DIR`, `sts2.local.yaml`, or `sts2.config.yaml`, in that order.
- Use `scripts/validate.sh bridge-tests --json` instead of raw `dotnet test` for default bridge test validation; the wrapper passes `-m:1` to avoid MSBuild output-file contention. Use `scripts/validate.sh bridge-build --json` for the serial bridge host build path.
- `scripts/validate.sh bridge-tests --json` and `scripts/validate.sh bridge-live-host-tests ... --json` both keep structured JSON reporting and include a narrow one-shot retry path for known locked `obj/.../ref/*.dll` bridge outputs before surfacing a final failure. If the local environment prevents the .NET test host from binding its socket, the bridge wrapper reports `status: "blocked"` / `code: "environment_blocked"` instead of a generic regression.
- Use `scripts/validate.sh npm-wrapper-tests --json` for repo-local npm wrapper validation; it preserves the normal npm test command and reruns failed tests without Node's per-file isolation only to expose sandbox diagnostics. Use `scripts/verify_parallel.sh --json` for the aggregate Rust/bridge/npm gate; it first preflights repo-pinned `mise.toml` tools and reports `environment_blocked` with `mise install` guidance for missing or invalid shims, then runs all independent stages and reports `blocked` when local socket or child-process restrictions prevent a stage from running.
- Use `scripts/validate.sh producer-walk-profile --json` to turn the bridge-owned live scene watcher's
  producer-walk profiler output into a diffable `perf-report/1` envelope. Capture a log first
  (`SPIRECTL_SCENE_WATCH_PROFILE=1` on the live host, with a scene-delta subscriber attached) and pass it with
  `--log <path>`; record the A/B lever under test with `--param <k=v>`. The indented report is written under the
  gitignored `.sts2/perf-reports/`. It is an instrument, not a gate: it applies no wall-clock thresholds and
  never fails because a number moved. It is not an `ISpirectlRuntime` port.
- Use `scripts/validate.sh docs-map-paths --json` to check the subsystem maps. It resolves every backticked token in `docs/maps/*.md` that looks like a repo path (contains a slash, first segment is a directory at the repo root) and fails with the offending file and token when one no longer exists. The maps are the routing table an agent reads before touching a subsystem, so a path that moved during a refactor sends the next reader nowhere while the doc still reads as authoritative. Globs and scheme-like tokens (`res://`, `model://`, screen ids) are skipped; a `path:12` line reference is checked as `path`.
- Use `scripts/validate.sh cargo-package-sts2 --json` for the Rust crate packaging gate. It runs full `cargo package -p sts2 --allow-dirty` verification when possible; while the crate still depends on workspace-relative proto/version inputs, it reports `package_verification_unsupported` only after the accepted `--no-verify` package smoke succeeds.
- Use `scripts/validate.sh cargo-test-filter --package sts2 --test <integration-test> --filter <test-name> --json` for focused Rust integration-test filters. It wraps `cargo test --color never -p <package> --test <integration-test> <test-name>` and fails with structured `zero_tests_matched` output when cargo reports zero executed tests, while preserving real cargo failures. Use `scripts/validate.sh cargo-unit-test-filter --package sts2 --filter <unit-test-filter> --json` for package/unit test filters, including unit tests that live under `src/lib.rs` rather than an integration target.
- Use `scripts/validate.sh dotnet-format --include <bridge path> --json` for selected C# formatting checks; it supports selected files and selected directories, verifies selected whitespace and formatter style diagnostics (`IDE0055`), and excludes generated `bin` / `obj` artifacts from selected directory diffs without treating unrelated analyzer warnings elsewhere in the solution as the selected result.
- If you explicitly need the live-host compile gate in .NET tests, run `scripts/validate.sh bridge-live-host-tests --filter FullyQualifiedName~MapScreenInspector --json` or pass `--assemblies-dir <authoritative STS2 assemblies dir>`. This sets `RunSts2LiveHostTests=true`, enables the host references, and includes tests under `#if ENABLE_STS2_LIVE_HOST` together.
- For S87 specifically, the focused validation set is `scripts/validate.sh bridge-tests --json`, `cargo test -p sts2 --test test_runner multiplayer_ownership`, `scripts/validate.sh npm-wrapper-tests --json`, `scripts/validate.sh bridge-live-host-tests --filter FullyQualifiedName~MultiplayerOwnershipLegality --json`, and `cargo run -p sts2 -- --config tests/sts2.mock.yaml --json test run tests/scenarios/fixture-basic-lobby.sts2.yaml`; the live-host command may return structured `environment_blocked` when the local STS2 host is unavailable.
- For protobuf/Rust fallout checks when unrelated local edits make the normal workspace noisy, run `scripts/validate.sh rust-proto-selected --path proto/spirectl/v0/runtime.proto --json`; it builds a temporary copy from committed `HEAD`, overlays only selected paths, and reports ignored dirty paths without stashing or reverting user work.
- Before live smoke reads against an existing game, run `cargo run -p sts2 -- --json game bridge-health`; it distinguishes missing, refused/stale, timed-out, version-mismatched, wrong-game-build (`game_version_mismatch`, exit 4, with the per-field comparison under `compatibility.gameBuild`), presentation/render RPC-incompatible, and reachable/current bridge states without deploying, restarting, killing, or mutating game files. Add `--verbose` when the full diagnostic payload is needed.
- A bridge built for one Slay the Spire 2 build and loaded into another starts and then throws on the first lobby walk, so cross-build work needs both legs: build the bridge against each install with the lane auto-detected (`scripts/validate.sh bridge-live-host-tests --assemblies-dir <install>/data_sts2_linuxbsd_x86_64 -- --filter FullyQualifiedName~GameApi` is the fast one), and check an already-built payload against the other install with `sts2 code verify-references`, which reports reshaped signatures a missing-symbol probe cannot see. See the Game Build Binding section of `docs/cli.md` for what the deployed manifest and the handshake claim.
- Bridge-mod source or lifecycle packaging changes must also run a real host publish against the authoritative local STS2 assemblies root, then re-run `cargo run -p sts2 -- game install-bridge`.
- `bridge-mod/AGENTS.md` carries the same requirement so repo-local AI agents automatically pick up the real publish/install gate when they touch bridge-mod code.

Static-inspection automated tests use a small test assembly fixture, not a real local STS2 install. That keeps them deterministic while still exercising the real helper-tool metadata path.

Current live/manual validation focus for the shipped runtime surface. Modeled first-party controls should be validated through typed intent actions and `preferredAction`; generic `choose` remains a fallback-only check for unmodeled visible controls such as provisional main-menu start-run.

- `sts2 act choose --choice menu:start-run`
- `cargo run -p sts2 -- --json test run tests/scenarios/smoke-main-menu-live.sts2.yaml` after installing the bridge and attaching to the live bridge
- `sts2 act claim-reward --reward <reward-id>`
- `sts2 act skip-rewards`
- `sts2 act select-card --card <card-id>`
- `sts2 act skip-card-selection`
- `sts2 act select-bundle --bundle <bundle-id>`
- `sts2 act buy-card|buy-relic|buy-potion|remove-card --shop-item <shop-item-id>`
- `sts2 act leave-shop`
- `sts2 act close-shop-inventory`
- `sts2 act rest`
- `sts2 act smith`
- `sts2 act use-rest-site-option --option <rest-option-id>`
- `sts2 act proceed-rest-site`
- `sts2 act open-chest`
- `sts2 act take-relic --relic <relic-id>`
- `sts2 act proceed-treasure-room`
- `sts2 act select-event-option --event-option <event-option-id>`
- `sts2 act open-event-shop --event-option <event-option-id>`
- `sts2 act use-crystal-sphere-control --control <control-id>`
- `sts2 act proceed-event`
- `sts2 act confirm-selection`
- `sts2 act cancel-selection`
- `sts2 act play-card --card <id> [--target <id>]`
- `sts2 act use-potion --potion <id> [--target <id>]`
- `sts2 act select-map-node --node <map-node-id>`
- `sts2 --mode dangerous act mouse click --x <x> --y <y> [--button <button>]`
- `sts2 --json assets explain composed://combat-background/overgrowth/image --execution live`
- shop-screen extraction on `NMerchantInventory`
- reward-screen extraction on `NRewardsScreen`
- event-room extraction on `NEventRoom`
- treasure-room and `relic-selection` extraction on `NTreasureRoom` / `NTreasureRoomRelicCollection`
- rest-site extraction on `NRestSiteRoom`
- card-selection extraction on `NCardRewardSelectionScreen` / `NChooseACardSelectionScreen`
- `simple-card-selection` and `deck-card-selection` staged-pick extraction plus `sts2 act confirm-selection` / `sts2 act cancel-selection`
- `bundle-selection` overlay extraction plus `sts2 act select-bundle --bundle <stable-bundle-id>`
- custom-event mock coverage for `fake-merchant-pre-open`, `crystal-sphere`, and `crystal-sphere-finished`, including `sts2 act open-event-shop`, `sts2 act use-crystal-sphere-control`, and `sts2 act proceed-event`
- `card-overlay` identity-only extraction plus the `card-overlay-partial` notice on unsupported overlay variants that still lack card-specific state/actions
- map-screen extraction on `NMapScreen`
- `sts2 act end-turn`
- `sts2 act ready`
- `sts2 act unready`
- `sts2 act select-character --character <id>`
- `sts2 dev logs --limit ... --level ... --target ...`
- `sts2 dev logs --after-cursor ...`
- `sts2 dev logs --tail ...`
- `sts2 dev logs --follow --tail ...`
- `sts2 dev log-health --tail ...`
- `sts2 dev diagnostics --bundle-dir ...`
- `sts2 --json dev mod-reload --project ... --build --wait`
- `sts2 --json dev mod-reload status --project ...`
- `sts2 --json dev diagnostics --hot-reload-project ... --bundle-dir ...`
- `sts2 --json inspect viewport-presets`
- `sts2 --json inspect viewport-presets --preset-catalog tests/visual/sample-catalog.sts2.viewport-presets.yaml`
- `sts2 --json assets explain composed://encounters/kaiser_crab_boss/scene-package --execution live`
- `sts2 --json assets extract-batch --manifest ./.sts2/artifacts/encounters/kaiser_crab_boss/manifest.json --output ./.sts2/artifacts/encounters/kaiser_crab_boss --execution live --format png`
- `scripts/validate.sh m78-live-encounter-artifacts --encounter kaiser_crab_boss --json`
- `sts2 dev wait-for ...`
- `sts2 dev assert ...`
- `sts2 dev fixture load --path fixtures/basic-combat.sts2.fixture.yaml`
- `sts2 dev fixture load --path fixtures/basic-map.sts2.fixture.yaml`
- `sts2 dev fixture load --path fixtures/basic-event-room.sts2.fixture.yaml`
- `sts2 dev fixture load --path fixtures/basic-event-room-options.sts2.fixture.yaml`
- `sts2 dev fixture load --path fixtures/treasure.sts2.fixture.yaml`
- `sts2 dev fixture load --path fixtures/treasure-opened.sts2.fixture.yaml`
- `sts2 dev fixture load --path fixtures/basic-relic-selection.sts2.fixture.yaml`
- `sts2 dev fixture load --path fixtures/basic-shop.sts2.fixture.yaml`
- `sts2 dev fixture load --path fixtures/basic-shop-inventory.sts2.fixture.yaml`
- `sts2 dev fixture load --path fixtures/basic-card-selection.sts2.fixture.yaml`
- `sts2 dev fixture load --path fixtures/basic-simple-card-selection.sts2.fixture.yaml`
- `sts2 dev fixture load --path fixtures/basic-deck-card-selection.sts2.fixture.yaml`
- `sts2 dev fixture load --path fixtures/basic-bundle-selection.sts2.fixture.yaml`
- `sts2 dev fixture load --path fixtures/basic-rewards.sts2.fixture.yaml`
- `sts2 dev fixture load --path fixtures/rest-site.sts2.fixture.yaml`
- `sts2 dev fixture load --path fixtures/basic-lobby.sts2.fixture.yaml`
- `sts2 dev fixture load --path fixtures/basic-load-run-lobby.sts2.fixture.yaml`
- `sts2 dev screenshot --preset desktop-1080p --rpc-timeout-ms 5000 --output ...`
- `sts2 dev screenshot-diff --baseline tests/scenarios/baselines/mock-main-menu.png --rpc-timeout-ms 5000 --bundle-dir ...`
- `sts2 dev screenshot-diff --baseline tests/scenarios/baselines/mock-main-menu.png --actual tests/scenarios/baselines/mock-main-menu.png --bundle-dir ...`
- `sts2 dev screenshot-diff --baseline tests/scenarios/baselines/lobby.png --actual ./.sts2/artifacts/visual/lobby.png --mask ./.sts2/artifacts/visual/lobby-mask.png --regions ./.sts2/artifacts/visual/lobby-regions.json --foreground-max-diff-ratio 0.001 --roi-max-diff-ratio 0.001 --required-comparison foreground --required-comparison roi --bundle-dir ...`
- `sts2 dev snapshot export --spec tests/snapshots/main-menu.sts2.snapshot.yaml --output ...`
- `sts2 dev snapshot compare --spec tests/snapshots/main-menu.sts2.snapshot.yaml --baseline tests/snapshots/baselines/main-menu --bundle-dir ...`
- `sts2 --config tests/sts2.mock.yaml --json test run tests/scenarios/visual-main-menu.sts2.yaml`
- `sts2 --config tests/sts2.mock.yaml --json test run tests/scenarios/regression-main-menu.sts2.yaml`
- `sts2 --config tests/sts2.mock.yaml --json test stress --iterations 10 tests/scenarios/regression-main-menu.sts2.yaml`
- start-run lobby extraction on `Screens.CharacterSelect.NCharacterSelectScreen`
- load-run lobby extraction on `Screens.CharacterSelect.NMultiplayerLoadGameScreen`
- load-run lobby `run.seed` / `run.floor` / `run.act` / `run.players[]` preservation from the loaded save
- visible local lobby `choices[]` for ready/unready and discoverable character rows
- `availableActions` honesty for executable lobby actions only
- null-plus-notice behavior when player-name platform lookups fail or load-run character buttons are absent on the active screen

Foreground-aware screenshot diff tests should keep full-image compatibility and then opt into stricter scopes. The default screenshot-diff bundle remains `comparison.json`, `baseline.png`, `actual.png`, and `diff.png`. Supplying `--mask` adds `comparisons.foreground` and `foreground-diff.png`; supplying `--regions` adds `comparisons.roi` and `roi-diff.png`. ROI region files are JSON arrays, or objects with a `regions` array, of `{ "id", "x", "y", "width", "height" }` rectangles. `--required-comparison foreground` without `--mask` fails with a `missing-mask` notice, and `--required-comparison roi` without `--regions` fails with a `missing-region` notice. `maxDiffRatio`, `foregroundMaxDiffRatio`, and `roiMaxDiffRatio` values must be less than `1.0`. The `dev.screenshotDiff` scenario step also accepts `pixelTolerance` (integer 0-255) and `ignoreAlpha` (boolean), matching the `--pixel-tolerance` / `--ignore-alpha` CLI flags: they widen what counts as an identical pixel across all three comparators without changing the `maxDiffPixels`/`maxDiffRatio` thresholds.

Fixture restore recipes are authored setup contracts, not a native hidden-state restore path. S89 recipe-backed fixtures cover typed non-combat and overlay entry families (`main-menu`, `combat`, `map`, `rewards`, `rest-site`, `event-room`, `treasure-room`, `relic-selection`, `shop`, `card-selection`, `simple-card-selection`, `deck-card-selection`, `bundle-selection`, `card-overlay`, `passive-card-overlay`, `Screens.CharacterSelect.NCharacterSelectScreen`, and `Screens.CharacterSelect.NMultiplayerLoadGameScreen`) plus ownership examples. Combat players may author `players[].potionIds` to replace that player's starting potion slots with deterministic installed potion models. Runner `dev.load-scenario` artifacts remain sparse `spirectl.scenario/v0` restore anchors with current validation payloads, and exact sidecars remain opaque native continuation data that is hash/size validated separately. Local-only degraded multiplayer fixtures may omit remote clients and must report those omissions instead of implying remote orchestration.

Still explicitly future work for live validation:

- broader combat redesign, multiplayer ownership hardening, and modded/unmodeled UI controls beyond the shipped typed intent surface
- broader fixture loading beyond the current additive `main-menu`, `combat`, `map`, `rewards`, `rest-site`, `event-room`, `treasure-room`, `relic-selection`, `shop`, `card-selection`, `simple-card-selection`, `deck-card-selection`, `bundle-selection`, `Screens.CharacterSelect.NCharacterSelectScreen`, and `Screens.CharacterSelect.NMultiplayerLoadGameScreen` sparse recipe slices. Local checkpoints shipped as M52-M55 experiments, shareable scenario restore shipped in M53, S88 reports current per-field restore support and mismatch diagnostics, and M59 replaces checkpoint restore with recorded screen-entry fixtures because arbitrary hidden combat/runtime state is not safely reproducible. Exact sidecars and native save-backed continuation remain bounded, opaque, hash/size-validated artifacts rather than reviewable fixture recipes. Full remote-client orchestration remains future work. Unstructured arbitrary runtime memory editing and broad per-screen scripting remain outside the current shipped validation surface.

For AI-surface validation, the current practical smoke loop is:

1. `cargo run -p sts2 -- --json inspect ai-tools`
2. `cargo run -p sts2 -- --json game detect`
3. `cargo run -p sts2 -- --json assets extract hand --execution offline --resources-dir <fixture-resources-dir>`
4. `cargo run -p sts2 -- --json skill install --path <temp-dir>`
5. `cargo run -p sts2 -- --json service serve --listen 127.0.0.1:4317 --auth-token local-dev-token` plus the `curl` smoke loop from [automation-service](./automation-service.md) when remote access behavior changes
6. `scripts/validate.sh npm-wrapper-tests --json`
7. optional manual adapter bring-up with `npm --prefix npm-wrapper exec sts2-mcp`
8. consult [mcp-migration](./mcp-migration.md) before deleting any downstream vendored MCP tree

## Runner Artifacts

`sts2 test run` now persists a run directory under `<artifacts-dir>/test-runs/run-<unix-ms>/`, where `<artifacts-dir>` comes from `config.artifacts.dir` unless `--artifacts-dir` overrides it.

The current layout is intentionally simple:

- `summary.json`: full run report, matching the JSON envelope returned by `sts2 --json test run ...`
- `scenarios/<NNN>-<slug>/result.json`: full per-scenario report for every scenario
- `scenarios/<NNN>-<slug>/steps/<NNN>-game-deploy.json`: persisted output for successful runner deploy steps
- `scenarios/<NNN>-<slug>/steps/<NNN>-dev-console.json`: persisted output for successful console steps
- `scenarios/<NNN>-<slug>/steps/<NNN>-dev-load-fixture.json`: persisted output for successful fixture-load steps, including `recipeReport.recipeName`, applied/inferred/omitted/unsupported/degraded-multiplayer field reports, and `bridgeValidation.status/details`
- `scenarios/<NNN>-<slug>/steps/<NNN>-dev-load-scenario.json`: persisted output for successful scenario-load steps, including the full field-level restore validation payload with expected/observed summaries, field-level mismatches, support classes, reason codes, suggested next steps, exact/sparse usage, and multiplayer restore notices
- `scenarios/<NNN>-<slug>/steps/<NNN>-dev-hot-reload.json`: persisted output for successful shell-supported hot-reload steps
- `scenarios/<NNN>-<slug>/steps/<NNN>-dev-debug-events.json`: persisted bounded debugger event transcript for successful `dev.debug-events` steps, including sequence cursors, retention metadata, event session roles, and any stream notices
- `scenarios/<NNN>-<slug>/steps/<NNN>-dev-screenshot.png`: default path for successful screenshot steps that do not set an explicit `output`
- `scenarios/<NNN>-<slug>/steps/<NNN>-dev-screenshot-diff/`: default bundle directory for successful screenshot-diff steps that do not set an explicit `bundleDir`
- `scenarios/<NNN>-<slug>/steps/<NNN>-dev-snapshot-compare/`: default bundle directory for successful snapshot-compare steps that do not set an explicit `bundleDir`
- `scenarios/<NNN>-<slug>/failure/summary.json`: failing step metadata for failed scenarios
- `scenarios/<NNN>-<slug>/failure/evidence/diagnostics.json`: stable failure-time diagnostics payload
- `scenarios/<NNN>-<slug>/failure/evidence/game-info.json`: best-effort game/transport snapshot when diagnostics captured it
- `scenarios/<NNN>-<slug>/failure/evidence/state.json`: best-effort state capture when diagnostics captured it
- `scenarios/<NNN>-<slug>/failure/evidence/logs.json`: best-effort recent logs capture when diagnostics captured it
- `scenarios/<NNN>-<slug>/failure/evidence/inspect-actions.json`: best-effort supported/current action capture when diagnostics captured it
- `scenarios/<NNN>-<slug>/failure/evidence/scene-tree.json`: best-effort runtime scene tree when diagnostics captured it
- `scenarios/<NNN>-<slug>/failure/evidence/runtime.png`: best-effort screenshot when diagnostics captured it

Failure artifact capture is best-effort, not a second failure mode:

- the scenario keeps its original failing step and exit code
- collection/write problems are surfaced in `artifacts.errors[]`
- `--failure-artifacts never` still writes `summary.json` and `result.json` but skips the failure capture files

For practical debugging, the usual loop is:

1. read `summary.json` to find the failed scenario and step
2. open that scenario’s `result.json` for the full step history and structured error
3. inspect `steps/` for successful mutation outputs or screenshots, then `failure/evidence/diagnostics.json` plus the sibling evidence files it references when they exist

`sts2 test stress` persists one aggregate directory under `<artifacts-dir>/test-stress/stress-<unix-ms>/`:

- `summary.json`: aggregate stress-run report matching the JSON envelope returned by `sts2 --json test stress ...`
- `iterations[*].runSummaryPath`: path to each nested `test run` summary written under the normal `test-runs/` tree
- `iterations[*].runRootDir`: root directory for each nested `test run` artifact tree

Manual static-inspection validation against a real install now looks like:

Use `sts2.local.yaml` for machine-specific install, assemblies, resources, and mods roots so the default config stack prefers authoritative local paths over copied sibling `libs/` folders. Pass `--config sts2.local.yaml` when you want the validation command to pin that exact file explicitly.

```bash
sts2 --json code locate type CombatScreen --config sts2.local.yaml
sts2 --json code describe type type:sts2:Your.Namespace.CombatScreen --config sts2.local.yaml
sts2 --json code refs type type:sts2:Your.Namespace.CombatScreen --config sts2.local.yaml
sts2 --json code derived type type:sts2:Your.Namespace.BaseScreen --config sts2.local.yaml
sts2 --json code hooks OnDeckChanged --config sts2.local.yaml
sts2 --json code hook-info method:sts2:Your.Namespace.ScreenHooks::OnDeckChanged(Your.Namespace.DeckState) --config sts2.local.yaml
sts2 --json code decompile type type:sts2:Your.Namespace.CombatScreen --config sts2.local.yaml
sts2 --json code decompile type type:sts2:Your.Namespace.CombatScreen --config sts2.local.yaml --full
sts2 --json code scene-search HandPanel --config sts2.local.yaml
sts2 --json code scene-tree res://ui/CombatScreen.tscn --config sts2.local.yaml
sts2 --json code scene-node res://ui/shared/HandPanel.tscn /HandPanel --config sts2.local.yaml
sts2 --json inspect reference-topics
sts2 --json dev scene tree /root --config sts2.local.yaml
sts2 --json dev scene node /root --config sts2.local.yaml
```

## Recommended Frontend Testing Split

For frontend work, prefer two layers instead of trying to make every UI test a live game test:

- mocked frontend tests:
  fast component and interaction coverage with mocked API/game responses
- true end-to-end tests with live STS2:
  a smaller set of Playwright tests that drive the real phone/web UI and validate the live game through `sts2`

The intended live E2E loop is:

1. deploy the bridge and current mod with `sts2 game deploy <mod-project> --build --restart --verify`
2. or preinstall the bridge with `sts2 game install-bridge`, then use `sts2 game launch` / `sts2 game attach` when you want launch and attach as separate steps
3. use `sts2 --json game close` for graceful bounded shutdown checks, and reserve `sts2 --json game kill` for explicit force-termination cleanup
4. extend Playwright with `@spirectl/sts2/playwright` when you want an `sts2` fixture plus opt-in failure artifacts
5. run a Playwright test that drives the frontend UI
6. call the Node helper from that test to run `gameInfo()`, `state()`, `waitFor()`, `assert()`, `logs()`, `logHealth()`, `diagnostics()`, `loadFixture()`, `http()`, `httpWait()`, `fetch()`, `websocket()`, `projectHookRun()`, `screenshot()`, or `screenshotDiff()`
7. inspect bridge logs or saved frontend artifacts when the UI and runtime disagree

See [npm-wrapper/examples/playwright-live-smoke.spec.ts](../npm-wrapper/examples/playwright-live-smoke.spec.ts) for a realistic sample scaffold.

## Platform Coverage

The integration suites under `cli/tests/` drive the CLI against a Unix-domain-socket bridge stub and
shell out to `bash` fixtures, so the ones that do (`assets_commands`, `bridge_contract`,
`dev_workflows`, `game_lifecycle`, `ipc_bridge_support`, `test_runner`) carry a file-level
`#![cfg(unix)]` and build to empty targets elsewhere. Porting them would mean a named-pipe stub
server; until then, Windows coverage is:

- the crate's unit tests, which include the platform-independent halves of the Windows-only code —
  `Get-CimInstance` / `tasklist` parsing, Windows command-line splitting, path comparison and the
  process-matching rules (`cli/src/lifecycle/lifecycle_process_windows.rs`), and the per-platform
  user-data roots (`cli/src/host_paths.rs`);
- a cross-target compile check that covers every `cfg(windows)` branch:

```bash
rustup target add x86_64-pc-windows-gnu
cargo check --target x86_64-pc-windows-gnu -p sts2 --all-targets
```

Cross-*checking* needs no Windows linker, but `ring`'s build script does compile C for the target, so
a host without `mingw-w64` needs a stub compiler (`cargo check` never links, so the object files only
have to exist). Nothing about the Windows paths has been exercised against a real game — that is an
open gap, tracked in `.ai/tool-improvements.md`.

## Suggested Commands

```bash
mise exec -- cargo test -p sts2
mise exec -- cargo test -p sts2 --test test_runner
scripts/validate.sh bridge-build --json
scripts/validate.sh bridge-tests --json
scripts/validate.sh cargo-test-filter --package sts2 --test cli_snapshots --filter models_card --json
scripts/validate.sh cargo-unit-test-filter --package sts2 --filter state_actions --json
scripts/validate.sh cargo-package-sts2 --json
scripts/validate.sh docs-map-paths --json
scripts/verify_parallel.sh --json
scripts/validate.sh bridge-live-host-tests --filter FullyQualifiedName~MapScreenInspector --json
scripts/validate.sh npm-wrapper-tests --json
cd npm-wrapper && npm pack --dry-run --json
cargo run -p sts2 -- --json inspect ai-tools
```

S83 focused runtime-state validation:

```bash
cargo run -p sts2 -- --config tests/sts2.mock-map.yaml --json test run tests/scenarios/mock-typed-map-probe.sts2.yaml
scripts/validate.sh bridge-live-host-tests --filter FullyQualifiedName~NonCombatPresentationState --json
```

For `bridge-live-host-tests`, `environment_blocked` is an infrastructure outcome (for example no reachable live host); any reachable-host test failure is a regression.
Treat this as a gate distinction, not a pass override: only infrastructure-unreachable runs may report `environment_blocked`; if a live host is reachable and these tests fail, the result is a regression.

S90/S95/S107 encounter visual reliability uses the same distinction. `dev visual-preflight --print-only` is safe for command discovery and emits the cataloged `assets explain`, manifest-ready `assets extract-batch`, and `m78-live-encounter-artifacts` commands. The m78 helper is opt-in live validation: it writes only repo-local `.sts2/artifacts/...` files, does not mutate installed game files, and reports `unavailable` when the live bridge cannot be reached. With `--json`, it preserves child `assets extract-batch` diagnostics, including `renderDiagnostics` or `error.details.diagnostic` payloads with selector, frame, hook, timing, alpha, and RGB evidence. When the bridge is reachable, blank, misframed, non-isolated, selector-missing, or bounds-invalid artifacts should fail validation.

Review generated encounter artifacts through their manifest and result JSON before judging the PNGs. For Kaiser-style packages, background, overlay, and visual-part outputs should have distinct selector/bounds or artifact-check evidence; if the bridge cannot prove isolation, the artifact should carry structured isolation-failure diagnostics instead of being accepted as an isolated render. Knowledge Demon-style VFX-backed parts are live-only: look for the package notice and either a rendered part with selector/bounds evidence or the preserved diagnostic payload explaining why the burn-fire target could not be isolated.

Manual live verification:

```bash
sts2 game install-bridge
sts2 game deploy ./mods/MyMod --build --restart --verify
# or split it out:
sts2 game launch
sts2 game attach
sts2 --json game close
sts2 --json game kill
```
