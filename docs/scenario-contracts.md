# Scenario Contracts

This document covers the two shareable restore surfaces: sparse `spirectl.scenario/v0` scenario
export/load, and recorded screen-entry fixtures.

An earlier iteration also shipped live-captured local "checkpoints" (`spirectl.checkpoint/v0`) as an
experiment. That command family was removed and replaced with recorded screen-entry fixtures;
arbitrary mid-screen hidden-state restore was found to be unsafe and stays out of scope. Only the
engineering conclusion is kept here — the checkpoint commands, manifest format, and RPCs are gone.

## Terms

- Fixture: an authored setup recipe, usually committed, loaded through `sts2 dev fixture load`. Fixtures construct known situations from semantic ingredients. They remain recipe-based and are not live-captured snapshots; load JSON reports `recipeReport` and `bridgeValidation` for applied, inferred, omitted, unsupported, and degraded multiplayer fields.
- Recorded screen fixture: a local ignored artifact under `.sts2/fixtures/current-screen/` that records the fixture recipe for entering the current screen id. It is derived from live context but does not promise arbitrary mid-screen runtime restore.
- Runner scenario: an executable `.sts2.yaml` or `.sts2.json` workflow consumed by `sts2 test run`.
- Sparse scenario artifact: a shareable `spirectl.scenario/v0` YAML-first artifact for reproducible state/workflow restore.
- Snapshot regression: evidence/compare tooling under `spirectl.snapshot/v0`; it is not a restore format.

## Recorded Fixtures

The recorded screen-entry fixture commands are:

```bash
sts2 dev fixture record
sts2 dev fixture resume
sts2 dev fixture resume --restart
sts2 dev fixture status
sts2 dev fixture clear
sts2 dev fixture load --path fixtures/basic-combat.sts2.fixture.yaml
```

`dev fixture record` records a screen-entry fixture. It does not record a byte-perfect runtime snapshot. For combat, that means it may recreate the initial combat setup but should explicitly omit played-card history, action queues, current RNG continuation, enemy AI history, relic turn history, transient animation/UI state, and other hidden runtime internals that are not safe fixture fields.

Current recorded-fixture semantics are intentionally narrower than authored fixture loading:

| Screen id | Recorded fixture meaning | Mid-screen state restored? | Current behavior |
| --- | --- | --- | --- |
| `combat` | Recreate the combat entry setup from run/player context and the authoritative encounter id. | no | supported when run, combat, local player, and encounter id are available |
| `main-menu` | Return to main menu as lifecycle/navigation setup. | no | supported with a synthetic default player when no run/player state exists |
| `Screens.CharacterSelect.NCharacterSelectScreen` / `Screens.CharacterSelect.NMultiplayerLoadGameScreen` | Recreate supported start-run or load-run lobby setup. | no | supported when lobby players and visible available characters are available |
| `map` | Recreate map-entry setup where fixture support has reliable run/player data. | no | supported when run and one local player are available |
| `shop` | Recreate shop entry inventory/removal setup. | no | unsupported unless future recorder code can read authoritative shop recipe data |
| `event-room` | Recreate the event screen and visible options only when event-specific recipe data exists. | no | unsupported; choice-label scraping is not used |
| `rewards`, `rest-site` | Recreate entry setup where fixture support has reliable run/player data. | no | supported when run and one local player are available |
| `treasure-room`, `relic-selection` | Recreate entry setup for supported visible choices. | no | unsupported until the recorder has screen-specific recipe data rather than transient choice text |
| card-selection families | Recreate entry setup for supported selection screens. | no | unsupported; staged picks and preview state are not recorded |

## Active Commands

Scenario and recorded fixture commands require `(no mode flag)`:

```bash
sts2 dev scenario export --output <file.sts2.scenario.yaml>
sts2 dev scenario export --output <file.sts2.scenario.yaml> --include-exact
sts2 dev scenario load --path <file.sts2.scenario.yaml>
sts2 dev scenario load --path <file.sts2.scenario.yaml> --restart
sts2 dev scenario load --path <file.sts2.scenario.yaml> --allow-degraded-local-multiplayer
sts2 dev fixture record
sts2 dev fixture status
sts2 dev fixture resume
sts2 dev fixture resume --restart
sts2 dev fixture clear
sts2 dev fixture load --path <file.sts2.fixture.yaml>
```

`scenario_export`, `scenario_load`, and authored `load_fixture` are exposed through `inspect ai-tools` because they operate on shareable scenario or fixture files. Recorded fixture record/resume/status/clear remain omitted because they manage ignored local operator state under `.sts2/fixtures/current-screen/`.

## Restore Quality

- `exact`: the bridge believes enough game-native data was captured/restored to continue faithfully for the supported state.
- `partial`: the bridge restored the major semantic state, but known transient internals, hidden queues, RNG, animation/UI state, or unsupported details were omitted or inferred.
- `unsupported`: the current screen/state cannot be captured or restored by the active surface.
- `degraded`: exact restore was requested or available, but the command fell back to sparse restore and reported the downgrade.

Field reports use finer fidelity terms:

- `exact`: captured or restored from reliable public/runtime state.
- `partial`: the main semantic value is present, but known details are missing.
- `inferred`: reconstructed from stable IDs, public choices, defaults, or fixture recipes.
- `omitted`: intentionally not captured.
- `unsupported`: in scope, but not safely restorable through current host hooks.
- `degraded-local-multiplayer`: a local-only restore intentionally omitted remote clients or remote-owned private detail.

`dev scenario export` returns a root current `restoreSupport` block and writes the same reports under `restore.fieldReports` in YAML. Each report names the public path, support class, stable reason code, and suggested next step. With `--include-exact`, exact sidecars report `exactBundleKind`: `save-backed` for native run-save continuation where supported, or `fixture-backed` for the existing JSON fixture fallback. Multiplayer captures also write an optional `multiplayer` block with local/host ids, player ids, slots, selected characters, readiness, role flags, lobby metadata, ownership/perspective metadata, and active-restore limitations. `dev scenario load` returns `validation.expectedSummary`, `validation.observedSummary`, `validation.mismatches`, and `validation.checked`; each mismatch includes `fieldPath`, expected/observed summaries, `supportClass`, `reasonCode`, and `suggestedNextStep`. A validation mismatch exits nonzero with `restore_validation_mismatch`. `dev fixture resume --restart` reports the same restart-stop metadata before loading the recorded fixture recipe.

`dev fixture load` local validation uses stable reason codes for authored failures such as `invalid_run_act`, `invalid_run_floor`, `hp_exceeds_max_hp`, `invalid_lobby_player`, `unknown_screen_id`, `unsupported-recipe-field`, and `invalid-authored-value`. Local-only degraded multiplayer recipes may model the local bridge perspective and omitted remote players, but they do not create remote clients or make remote-owned controls actionable.

## Safety

- Restore never writes normal STS2 save/profile files.
- Restore is bridge-mediated and in-memory.
- Save-backed exact sidecars are opt-in, local scenario artifacts; the CLI writes them only next to the requested scenario output and validates them by hash/size on load.
- Recorded fixtures are recipe files, not memory images; they omit hidden queues, RNG continuation, enemy AI history, relic turn history, and transient UI/action state unless future fixture fields model those semantics explicitly.
- Lobby multiplayer restore is bridge-mediated and reports `multiplayerRestore.mode: "lobby-only"`, with remote players materialized as placeholders when supported.
- Active multiplayer run/combat artifacts require `--allow-degraded-local-multiplayer` before the bridge may restore a local-only degraded state. The result reports `restoreQuality: "degraded"`, `multiplayerRestore.mode: "degraded-local-only"`, and `omittedRemotePlayerIds`.
- Without explicit degradation opt-in, active multiplayer artifacts fail with structured `degradation_flag_required` or `remote_clients_required` errors instead of silently flattening remote clients.
- Exact scenario sidecars are version-sensitive and not stable public artifacts.

## Sparse Scenario Example

```yaml
schemaVersion: spirectl.scenario/v0
name: combat-smoke
description: Shareable sparse combat scenario contract example.
createdAt: "2026-04-24T00:00:00Z"
source:
  gameVersion: unknown
  bridgeVersion: spirectl-bridge/0.1.0
  spirectlVersion: sts2/0.1.0
  screen:
    id: combat
    title: Combat
    instanceId: screen:combat:1
  perspective:
    scope: local
    playerId: p1
    usesDefault: true
restore:
  mode: sparse
  quality: partial
  fieldReports:
    - path: screen.id
      capture: exact
      restore: exact
      validationKey: true
      reasonCode: screen-id
      message: Screen id is captured and verified.
    - path: combat.hand
      capture: exact
      restore: partial
      validationKey: true
      reasonCode: combat-hand-observable
      message: Visible hand cards are captured by stable card ids and partially restored through fixtures.
    - path: combat.rng
      capture: omitted
      restore: unsupported
      validationKey: false
      reasonCode: combat-rng-hook-unavailable
      message: Runtime RNG internals are not exposed through current safe host hooks.
  compatibilityNotes:
    - code: exact-data-omitted
      message: Exact local runtime data was not requested.
run:
  seed: EXAMPLE
  act: 1
  floor: 3
  ascension: 0
  players:
    - id: p1
      character: IRONCLAD
      isLocal: true
      isHost: true
      isRemote: false
screenState:
  combat:
    turn: 1
    handCardIds: []
    drawPileCardIds: []
    discardPileCardIds: []
    exhaustPileCardIds: []
  choices: []
  availableActions: []
notices:
  - code: pile-cards-partial
    message: Discard pile card details were not observable.
    provisional: true
    path: combat.discardPile.cards
    severity: partial
    source: Sts2PresentationStateResolver
```

Exact opaque data is allowed in scenario artifacts only when explicitly requested with `--include-exact`. The CLI writes it as a sidecar next to the YAML file, validates `sha256` and `sizeBytes` on load, and permits sparse fallback only when the scenario restore metadata allows it.

## Restore Support Matrix

The matrix is field-level and tied to the typed state/action contract rather than a screen-level table. Common validation keys are `screen.id`, `run.act`, `run.floor`, `run.seed`, `perspective.playerId`, and `perspective.scope` when available. Multiplayer validation also checks `localPlayerId`, `hostPlayerId`, player ids, ownership flags, selected characters, readiness, and explicit degraded/omitted-remote notices.

| Public field group | Sparse scenario restore | Exact sidecar/native continuation | Recorded screen-entry fixture | Validation and diagnostics |
| --- | --- | --- | --- | --- |
| `screen`, `source`, game/bridge/tool versions | exact for public anchors | exact metadata; version-sensitive sidecar boundaries | exact screen id only | mismatch uses `screen-id` or version reason codes |
| `run.seed`, `run.act`, `run.floor`, `run.playersById` public identity/HP/gold summaries | exact or partial depending on live visibility | exact only when native save/run data exposes it | partial/inferred from recipe inputs | validate stable ids and summaries; suggest re-export or authored fixture when missing |
| typed non-combat sections: `map`, `eventRoom`, `treasureRoom`, `relicSelection`, `restSite`, `shop`, `rewards`, `cardSelection`, `simpleCardSelection`, `deckCardSelection`, `bundleSelection`, `lobby` | exact for visible stable ids and labels; partial for screen-private backing data | save-backed exact only for supported save/run data; otherwise fixture-backed partial | entry setup only, no mid-screen staged history unless a recipe field models it | mismatches name the typed path and whether the value is exact, partial, inferred, or unsupported |
| `combat` public sections: local player, observable hand/potions/enemies, visible piles/status summaries, turn and active-player metadata | partial; observable fields can validate, hidden continuations are omitted | native save-backed continuation may be exact for supported save/run data; JSON fixture fallback remains partial | combat entry setup, not played-card history | hidden queues, RNG continuation, enemy AI history, relic turn history, and transient animation/UI state are omitted or unsupported |
| `choices[]` compatibility entries | exact for visible stable ids and current metadata, partial when labels/availability are provisional | exact only if the native continuation returns to the same public screen state | entry-visible choices only | validate `id`, `choiceKind`, `intentKind`, `ownerPlayerId`, `perspective`, and `preferredAction` where present |
| `availableActions[]` and preferred intent actions | exact for advertised action kind/arguments at capture time, but legality must be rechecked after load | exact only for a restored native state that still exposes the same legal action | inferred from recipe-created entry state | mismatch suggests re-reading `state` and using current intent actions instead of stale ids |
| ownership and perspective metadata: `playerId`, `ownerPlayerId`, `isLocal`, `isHost`, `isRemote`, `hostPlayerId`, `localPlayerId`, `localPlayerRole`, `perspective`, remote orchestration capability | exact when observable; degraded-local-multiplayer for omitted remote clients | native continuation may preserve supported local save/run identity, but does not invent remote clients | local recipe owner only unless lobby recipe models more players | active multiplayer local-only restore must report omitted remote player ids and degraded support classes |
| notices and presentation/asset refs | partial or inferred; useful evidence but not a full restore target | sidecar may carry more data, but public restore claims stay field-level | usually omitted unless recipe-owned | validation should not fail only because advisory notices differ unless marked as validation keys |
| exact sidecar metadata: `exactBundlePath`, `exactBundleKind`, hash, size | not part of sparse recipe | opaque, version-sensitive, hash/size validated; `save-backed` or `fixture-backed` | not applicable | fallback to sparse is explicit and reports downgraded/degraded quality |

Unsupported or out-of-scope screen ids still report `screen.id` plus an unsupported field report instead of pretending to restore hidden state. Do not re-introduce live-capture checkpoint commands, a checkpoint manifest format, runner steps, or AI tools.
