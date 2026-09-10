# Test Runner Map

See also: [current status](../current-status.md), [known gaps](../known-gaps.md), [testing](../testing.md), [CLI](../cli.md).

## Primary files

- `cli/src/test_runner.rs`
- `cli/src/automation_service.rs`
- `cli/src/lib.rs`
- `tests/scenarios/smoke-main-menu.sts2.yaml`
- `tests/scenarios/smoke-main-menu-live.sts2.yaml`
- `fixtures/basic-main-menu.sts2.fixture.yaml`
- `fixtures/basic-combat.sts2.fixture.yaml`
- `fixtures/basic-map.sts2.fixture.yaml`
- `fixtures/map-first-run.sts2.fixture.yaml`
- `fixtures/map-ancient-done.sts2.fixture.yaml`
- `fixtures/map-act-2.sts2.fixture.yaml`
- `fixtures/map-travel.sts2.fixture.yaml`
- `fixtures/basic-event-room.sts2.fixture.yaml`
- `fixtures/treasure.sts2.fixture.yaml`
- `fixtures/basic-relic-selection.sts2.fixture.yaml`
- `fixtures/basic-shop.sts2.fixture.yaml`
- `fixtures/fake-merchant-pre-open.sts2.fixture.yaml`
- `fixtures/basic-card-selection.sts2.fixture.yaml`
- `fixtures/basic-simple-card-selection.sts2.fixture.yaml`
- `fixtures/basic-deck-card-selection.sts2.fixture.yaml`
- `fixtures/basic-bundle-selection.sts2.fixture.yaml`
- `fixtures/basic-rewards.sts2.fixture.yaml`
- `fixtures/rewards-all.sts2.fixture.yaml`
- `fixtures/rewards-all-with-relics.sts2.fixture.yaml`
- `fixtures/rest-site.sts2.fixture.yaml`
- `fixtures/basic-lobby.sts2.fixture.yaml`
- `fixtures/initial-neow.sts2.fixture.yaml`
- `fixtures/two-ironclad-neow.sts2.fixture.yaml`
- `fixtures/two-ironclad-lobby.sts2.fixture.yaml`
- `fixtures/two-ironclad-nibbits-weak-combat.sts2.fixture.yaml`
- `tests/scenarios/fixture-basic-map.sts2.yaml`
- `tests/scenarios/fixture-map-first-run.sts2.yaml`
- `tests/scenarios/fixture-map-ancient-done.sts2.yaml`
- `tests/scenarios/fixture-map-act-2.sts2.yaml`
- `tests/scenarios/fixture-map-travel.sts2.yaml`
- `tests/scenarios/mock-typed-map-probe.sts2.yaml`
- `tests/scenarios/fixture-basic-event-room.sts2.yaml`
- `tests/scenarios/fixture-treasure.sts2.yaml`
- `tests/scenarios/fixture-basic-relic-selection.sts2.yaml`
- `tests/scenarios/fixture-basic-shop.sts2.yaml`
- `tests/scenarios/fixture-fake-merchant.sts2.yaml`
- `tests/scenarios/fixture-basic-card-selection.sts2.yaml`
- `tests/scenarios/fixture-basic-simple-card-selection.sts2.yaml`
- `tests/scenarios/fixture-basic-deck-card-selection.sts2.yaml`
- `tests/scenarios/fixture-basic-bundle-selection.sts2.yaml`
- `tests/scenarios/fixture-basic-rewards.sts2.yaml`
- `tests/scenarios/fixture-rewards-all.sts2.yaml`
- `tests/scenarios/fixture-rewards-all-with-relics.sts2.yaml`
- `tests/scenarios/fixture-basic-rest-site.sts2.yaml`
- `tests/scenarios/non-combat-intent-actions.sts2.yaml`
- `tests/scenarios/fixture-basic-lobby.sts2.yaml`
- `tests/scenarios/fixture-initial-neow.sts2.yaml`
- `tests/scenarios/fixture-two-ironclad-neow.sts2.yaml`
- `tests/scenarios/fixture-two-ironclad-lobby.sts2.yaml`
- `tests/scenarios/fixture-two-ironclad-nibbits-weak-combat.sts2.yaml`
- `tests/scenarios/couchcoop-target-screenshots.sts2.yaml`
- `tests/scenarios/couchcoop-target-visual-diff.sts2.yaml`
- `tests/scenarios/deploy-and-fixture-smoke.sts2.yaml`
- `tests/scenarios/diagnostics-main-menu.sts2.yaml`
- `tests/scenarios/log-health-main-menu.sts2.yaml`
- `tests/scenarios/visual-main-menu.sts2.yaml`
- `tests/scenarios/regression-main-menu.sts2.yaml`
- `tests/snapshots/main-menu.sts2.snapshot.yaml`
- `tests/snapshots/baselines/main-menu/snapshot.json`
- `tests/scenarios/baselines/mock-main-menu.png`
- `cli/tests/test_runner.rs`

## Secondary files

- `cli/src/query.rs`
- historical M7 test-runner notes

## Supported now

- `sts2 test run` accepts one `.sts2.yaml` or `.sts2.json` scenario file, a directory discovered recursively in sorted relative-path order, or one `--inline` YAML/JSON scenario body.
- `sts2 test run --profile <name>` resolves a project-defined profile from `test.profiles.<name>` in config, applies the profile config overlay for that invocation, runs optional lifecycle preflight, applies profile/CLI tag filters, and then uses the normal runner.
- Profile preflight supports bridge install, one config-relative `deploy` object using `game.deploy` fields, launch, and attach. Deploy preflight runs once per profiled run before scenario discovery.
- A path supplied with `--profile` replaces the profile's default `paths` while preserving the profile overlay, preflight, filters, and gate policy.
- `sts2 test stress` accepts one `.sts2.yaml` or `.sts2.json` scenario file or a directory, then repeats the same discovery/execution path under bounded iteration or duration controls.
- `npm-wrapper` exposes the same runner through `createSts2Client().testRun({ path | inline | scenario, ... })`, serializing JS `scenario` objects through the existing CLI `--inline` path rather than inventing a second runner implementation.
- `sts2 service serve` now adds an async remote `test run` job API on top of the same Rust runner helpers: `POST /v0/test-runs`, `GET /v0/test-runs/{runId}`, `GET /v0/test-runs/{runId}/artifacts`, and bounded artifact download paths.
- `npm-wrapper` also exposes `testStress({ path, iterations | durationMs, ... })`, still shelling back to the CLI rather than adding a JS-native runner.
- Parser normalizes supported aliases such as `assert.query -> dev.assert`.
- Supported executable steps are:
  - `game.info`
  - `game.deploy`
  - `dev.load-fixture` (canonical artifact id) and `dev.fixture.load` (new command-shaped alias)
  - `dev.logs`
  - `dev.log-health`
  - `dev.diagnostics`
  - `dev.hot-reload`
  - `dev.delay`
  - `dev.load-scenario`
  - `dev.assert`
  - `dev.http`
  - `dev.http-wait`
  - `dev.fetch`
  - `dev.websocket`
  - `project.hook`
  - `dev.wait-for`
  - `dev.screenshot`
  - `dev.screenshot-diff`
  - `dev.snapshot-compare`
  - `act.play-card`
  - `act.use-potion`
  - `act.claim-reward`, `act.skip-rewards`, `act.select-card`, `act.skip-card-selection`, `act.select-bundle`
  - `act.buy-card`, `act.buy-relic`, `act.buy-potion`, `act.remove-card`, `act.leave-shop`, `act.close-shop-inventory`
  - `act.rest`, `act.smith`, `act.use-rest-site-option`, `act.proceed-rest-site`
  - `act.open-chest`, `act.take-relic`, `act.proceed-treasure-room`
  - `act.back-from-map`, `act.select-event-option`, `act.open-event-shop`, `act.use-crystal-sphere-control`, `act.proceed-event`
  - `act.fallback-choose` (`act.choose` is normalized as an alias for older authored scenarios and fallback-only modded/unmodeled controls)
  - `act.confirm-selection`
  - `act.cancel-selection`
  - `act.select-map-node`
  - `act.end-turn`
  - `act.ready`
  - `act.unready`
  - `act.select-character`
- Runs persist `summary.json`, per-scenario `result.json`, per-step deploy/load-fixture/load-scenario/hot-reload/log-health/diagnostics JSON artifacts, screenshot artifact paths, visual diff `comparison.json` bundle paths, snapshot-compare bundle paths, and a diagnostics-owned `failure/evidence/diagnostics.json` bundle with sibling evidence files when best-effort failure capture succeeds. Profile runs add profile metadata and any preflight result, including preflight deploy output, to the summary. `dev.load-fixture` step artifacts preserve the same `recipeReport` JSON as the direct command, including applied/inferred/omitted/unsupported/degraded multiplayer field arrays and `bridgeValidation`. `dev.load-scenario` step artifacts preserve the full field-level restore validation payload, including expected/observed summaries, mismatches, support classes, reason codes, suggested next steps, exact/sparse usage, and degraded multiplayer notices.
- Target-screen artifact scenarios use the same runner artifact model: [tests/scenarios/couchcoop-target-screenshots.sts2.yaml](../../tests/scenarios/couchcoop-target-screenshots.sts2.yaml) captures `1280x720` PNG baselines after loading the target lobby, initial Neow, and two-player `NIBBITS_WEAK` fixtures, while [tests/scenarios/couchcoop-target-visual-diff.sts2.yaml](../../tests/scenarios/couchcoop-target-visual-diff.sts2.yaml) writes screenshot-diff baseline, actual, diff, and `comparison.json` bundles for those screens. These generated outputs stay under the ignored `.sts2/` artifact tree.
- `dev.hot-reload` resolves `project` relative to the scenario file for file-backed scenarios and relative to `cwd` for inline/object scenarios. `expectGenerationChanged` is allowed only with `wait: true`, because the runner needs a completed shell report to compare generations.
- Completed remote job summaries now add additive `remoteArtifacts[]` entries with relative paths and download URLs, but the on-disk `summary.json` / `result.json` tree stays unchanged.
- Failed scenario results now mark `failure.phase` as `setup` for `game.deploy`, `dev.load-fixture`, and `dev.load-scenario` failures, and the human summary uses the same setup-vs-execution wording instead of a generic step label.
- Stress runs persist one aggregate `summary.json` under `artifacts/test-stress/` plus per-iteration nested `runSummaryPath` / `runRootDir` references back into the normal `test-runs/` tree.
- The checked-in smoke split is explicit: `smoke-main-menu.sts2.yaml` stays on explicit mock transport through `--config tests/sts2.mock.yaml` or the repo `mock` test profile for baseline-safe runner coverage, while `smoke-main-menu-live.sts2.yaml` is an opt-in live scenario tagged `liveValidation` that waits for executable `choices[id=menu:start-run].preferredAction` before issuing explicit fallback `act.fallback-choose`.
- `non-combat-intent-actions.sts2.yaml` is the focused S86 runner surface for success paths across reward/card, shop, rest-site, treasure/relic, map, and event-room groups; validate it with `cargo test -p sts2 --test test_runner non_combat_intent_actions`.

## Current limits

- `dev.load-fixture` / `dev.fixture.load` now execute the shipped authored fixture subset: `main-menu`, `combat`, `map`, `rewards`, `rest-site`, `event-room`, `treasure-room`, `relic-selection`, `shop`, `card-selection`, `simple-card-selection`, `deck-card-selection`, `bundle-selection`, `card-overlay`, `passive-card-overlay`, `Screens.CharacterSelect.NCharacterSelectScreen`, and `Screens.CharacterSelect.NMultiplayerLoadGameScreen` including initial Neow, the two-Ironclad host-local Neow event target, the authored load-run lobby slice, the two-Ironclad host-local lobby target, explicit event options, Fake Merchant pre-open validation, opened treasure-room proceed flow, shop inventory/card-removal overrides, visible overlay entry, ownership examples, and local-only degraded multiplayer reporting.
- Multiplayer-lobby fixtures include both single-local and host-local-seat examples. [fixtures/two-ironclad-lobby.sts2.fixture.yaml](../../fixtures/two-ironclad-lobby.sts2.fixture.yaml) keeps `p:1` as the host bridge owner, marks `p:2` as an explicit host-local seat, and [tests/scenarios/fixture-two-ironclad-lobby.sts2.yaml](../../tests/scenarios/fixture-two-ironclad-lobby.sts2.yaml) asserts both Ironclad selections plus `host-local-seat` ownership/orchestration state.
- Combat fixture recipes can include multiple locally controlled host seats only when the authored player metadata is explicit about S108 ownership. [fixtures/two-ironclad-nibbits-weak-combat.sts2.fixture.yaml](../../fixtures/two-ironclad-nibbits-weak-combat.sts2.fixture.yaml) is the deterministic host-local example: `p:1` and `p:2` are both Ironclad local seats, `p:2` is marked as a host-local seat, and [tests/scenarios/fixture-two-ironclad-nibbits-weak-combat.sts2.yaml](../../tests/scenarios/fixture-two-ironclad-nibbits-weak-combat.sts2.yaml) asserts the loaded `NIBBITS_WEAK` encounter, both players, and local action metadata.
- `dev.load-scenario` composes `spirectl.scenario/v0` sparse artifacts with executable `.sts2.yaml` runner workflows and accepts `allowDegradedLocalMultiplayer` for active multiplayer artifacts that should intentionally omit remote clients. Runner scenarios remain workflow files, scenario artifacts are restore anchors loaded by a step, exact sidecars are separate opaque hash/size-validated continuation data, and checkpoint commands are not runner steps. M59 removes the active checkpoint command surface and replaces the local operator loop with recorded screen-entry fixtures; runner behavior continues to prefer authored sparse fixtures and shareable scenarios. S88 owns current restore diagnostics and S89 owns additional recipe-backed fixture coverage.
- The checked-in examples add load-run lobby, explicit event-room options, Fake Merchant open/close validation, opened treasure-room proceed, and shop inventory/card-removal coverage without changing the runner contract itself.
- The automation service keeps remote job state in memory by default and supports durable filesystem-backed job manifests, status recovery, and artifact indexes when started with durable mode. Network MCP reuses this durable `test_run` flow and artifact metadata; it is not a distributed runner or a generic remote file-transfer surface for every artifact-writing command.
- The repo config defines `test.profiles.mock` for deterministic mock smoke coverage and `test.profiles.live` for real-game spec validation. The `live` profile requires non-mock transport after overlay resolution, prepares the bridge/game through preflight, selects `liveValidation` scenarios by default, and fails when no matching live validation exists unless an applicable scenario explicitly declares `liveValidation.status: notApplicable` with a reason and the gate allows it.

## If you change the runner, also update

- Step parsing, aliases, execution, and artifact shapes in `cli/src/test_runner.rs`
- CLI help/examples and AI-tool metadata in `cli/src/lib.rs` if `test run` behavior changes
- Shared query behavior in `cli/src/query.rs` when assertion semantics change
- Example scenarios in `tests/scenarios/` and fixture examples in `fixtures/`
- Docs in `docs/testing.md` and the historical M7 test-runner notes
- Tests in `cli/tests/test_runner.rs`
