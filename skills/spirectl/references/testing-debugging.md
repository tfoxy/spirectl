# Testing And Debugging

Use this reference for deterministic validation, scenario runners, diagnostics, hot reload, and live debugger sessions.

## Scenario Runner

Run checked-in YAML/JSON scenarios, directories, profiles, or inline bodies:

```bash
"${STS2_BIN[@]}" --json test run tests/scenarios/smoke-main-menu.sts2.yaml
"${STS2_BIN[@]}" --json test run --profile mock
"${STS2_BIN[@]}" --json test run --inline '{name: smoke-inline, steps: [game.info]}'
"${STS2_BIN[@]}" --json test stress tests/scenarios/regression-main-menu.sts2.yaml --iterations 10
```

Profiles are project-defined config entries. `test run` persists summaries, per-scenario results, step artifacts, failure diagnostics, screenshot/diff bundles, and remote artifact metadata when routed through a durable automation service job.

Prefer runner steps over ad hoc scripts for game info/deploy, fixtures, scenarios, logs, log health, diagnostics, hot reload, delay, HTTP/fetch/websocket probes, project hooks, waits, asserts, screenshots, screenshot diff, snapshot compare, and semantic actions.

Use `act.fallback-choose` only for fallback-only generic/modded/unmodeled controls in authored scenarios. New scenarios should prefer semantic action steps matching current `actions[]`.

## Waits, Assertions, And Diagnostics

```bash
"${STS2_BIN[@]}" --json dev wait-for screen.type --equals combat --timeout-ms 5000
"${STS2_BIN[@]}" --json dev assert combat.players[0].energy --gte 1
"${STS2_BIN[@]}" --json dev log-health --tail 100
"${STS2_BIN[@]}" --json dev diagnostics --bundle-dir ./.sts2/artifacts/diagnostics
```

Assertions use simple path queries over current state. For compact state, assert against `screen`, `decision`, `actions[]`, `scene`, `run`, `combat`, `fallbackChoices[]`, and `notices[]`. Use `state full` expectations only for migration/debug cases that intentionally need the expanded bridge-native compatibility payload.

Diagnostics bundles should keep structured log health, exceptions, screenshots, hot-reload capture, and evidence paths intact.

## Fixtures And Scenarios

Authored fixtures are sparse recipes:

```bash
"${STS2_BIN[@]}" --json dev fixture load --path fixtures/basic-map.sts2.fixture.yaml
"${STS2_BIN[@]}" --json dev scenario export --output ./.sts2/artifacts/repro.sts2.scenario.yaml
"${STS2_BIN[@]}" --json dev scenario load --path ./.sts2/artifacts/repro.sts2.scenario.yaml --allow-degraded-local-multiplayer
```

Inspect `recipeReport`, `bridgeValidation`, `restoreSupport`, validation summaries, mismatch `supportClass` / `reasonCode` / `suggestedNextStep`, and `multiplayerRestore`. Active multiplayer repros need explicit degraded-local opt-in when remote clients are omitted.

Recorded screen-entry fixtures are local operator state and are not promoted AI tools. Historical checkpoints are not active runner surfaces.

## Hot Reload

Use hot reload only for M57/M60 shell-supported projects:

```bash
"${STS2_BIN[@]}" --json dev mod-reload status --project ./mods/MyHotMod
"${STS2_BIN[@]}" --json dev mod-reload --project ./mods/MyHotMod --build --wait
```

This builds and requests reload for a supported shell project. It does not deploy/restart shells, edit source, or reload arbitrary native mods. Inspect failed reload payloads for previous active generation and restart-required status.

## Debugger Sessions

Use debug only when you need explicit live runtime pause/step/breakpoint control:

```bash
"${STS2_BIN[@]}" --json dev debug status
"${STS2_BIN[@]}" --json dev debug session start --role controller --name investigation --pause
"${STS2_BIN[@]}" --json dev breakpoint add --session <session-id> --path screen.type --equals '"combat"' --name combat-match
"${STS2_BIN[@]}" --json dev debug wait --session <session-id> --timeout-ms 5000
"${STS2_BIN[@]}" --json dev debug session end --id <session-id> --resume
```

Start with `debug status`. Use a controller session for mutating pause/resume/step/wait and breakpoint changes. Use observer sessions for bounded `debug events` replay/follow without taking the lease.

Preserve lease conflicts, observer-mutation notices, retention/overflow fields, dropped events, breakpoint hit counts, auto-remove behavior, and last-observed values. `debug wait` is resume-until-hit; it is not a replacement for normal `wait-for` or `test run`.
