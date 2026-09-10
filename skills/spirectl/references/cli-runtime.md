# CLI Runtime

Use this reference for direct `sts2` CLI work: setup, lifecycle, live state, semantic actions, logs, fixtures, and runtime assertions.

## Discovery And Lifecycle

Start with read-only setup and bridge metadata:

```bash
"${STS2_BIN[@]}" --json game detect
"${STS2_BIN[@]}" --json game info
```

Use `game launch` when the game is not running, `game attach` when it is already running, `game deploy <mod-project> --build --restart --verify` for bridge plus mod deploy, and `game close` for graceful bounded shutdown. `game kill` is force termination for explicit operator cleanup, not normal automation.

STS2 accepts native multiplayer launch passthroughs after `--`, for example `sts2 game launch -- -fastmp host_standard`. Known `-fastmp` values in STS2 v0.103.2 are `host` to open the host flow, `host_standard` to host a standard multiplayer run, `host_daily` to host a daily multiplayer run, `host_custom` to host a custom multiplayer run, `load` to load an existing multiplayer save, and `join` to open the join-friends flow. `sts2` passes these through unchanged.

Keep machine-specific install roots, mods paths, transport overrides, and tool paths in ignored local config such as `sts2.local.yaml`.

## state

Default `state` is the compact agent-first snapshot:

```bash
"${STS2_BIN[@]}" --json state
"${STS2_BIN[@]}" --json state actions
"${STS2_BIN[@]}" --json state screen
"${STS2_BIN[@]}" --json state full --section eventRoom
```

Prefer:

- `screen` for current screen identity.
- `decision` for phase, active player, and action summary.
- `actions[]` for canonical executable semantic actions.
- `actions[].args` for command arguments.
- `actions[].stateRefs` plus `scene.items[].actionIds` / `scene.controls[].actionIds` to join visible objects to executable actions.
- `fallbackChoices[]` only for generic, provisional, or unmodeled controls.
- `notices[]` for partial support, perspective limits, hidden data, and unstable fields.

Use `state full` only for migration, bridge extraction debugging, or old compatibility payload investigation. Do not teach new agents to plan from legacy top-level compatibility arrays.

For multiplayer, read `playerId`, `ownerPlayerId`, local/host/remote role metadata, selected `perspective`, and remote-orchestration capability before acting. Local-only degraded or unsupported capability is inspectable evidence, not permission to execute as a remote client.

## Actions

Always read `state` and `inspect actions` before mutation:

```bash
"${STS2_BIN[@]}" --json inspect actions
"${STS2_BIN[@]}" --json act play-card --card <card-id> --target <target-id>
"${STS2_BIN[@]}" --json act use-potion --potion <potion-id> --target <target-id>
"${STS2_BIN[@]}" --json act end-turn
"${STS2_BIN[@]}" --json act select-map-node --node <map-node-id>
```

The action surface includes combat/lobby verbs and high-value non-combat intent verbs: rewards, card selection, bundles, shop buy/remove/leave, rest-site options, treasure/relic flow, map back/select, event options, Crystal Sphere controls, and proceed/skip verbs.

Use `--player-id <id>` only from current state/action metadata. Wrong-player and unsupported-perspective failures are expected structured outcomes, not transport failures.

Fallback choose is only for generic, modded, or unmodeled visible choices when `actions[]` has no modeled action:

```bash
"${STS2_BIN[@]}" --json act choose --choice modded-choice:custom-visible-button
```

Preserve `actionFailure.reasonCode` values such as `wrong-screen`, `wrong-player`, `not-visible`, `not-enabled`, `missing-hook`, `ambiguous-hook`, `stale-id`, `unsupported-perspective`, and `dangerous-mode-required`.

## Runtime Dev Tools

```bash
"${STS2_BIN[@]}" --json dev logs --limit 50
"${STS2_BIN[@]}" --json dev wait-for screen.type --equals combat
"${STS2_BIN[@]}" --json dev assert screen.type --equals combat
"${STS2_BIN[@]}" --json dev fixture load --path fixtures/basic-combat.sts2.fixture.yaml
"${STS2_BIN[@]}" --json dev scenario export --output ./.sts2/artifacts/repro.sts2.scenario.yaml
"${STS2_BIN[@]}" --json dev scenario load --path ./.sts2/artifacts/repro.sts2.scenario.yaml
```

Authored fixtures and scenarios report recipe, restore, validation, unsupported-field, and degraded-multiplayer diagnostics. Read those before treating follow-up state/action assertions as meaningful.

Runtime scene-tree commands are dev diagnostics only. They are not default state and are not AI catalog tools.
