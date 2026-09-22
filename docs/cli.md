# CLI

The primary executable is `sts2`.

The CLI now uses the runtime protobuf contract for bridge-backed commands, defaults to the live local IPC bridge, and keeps `mock` available through explicit config for deterministic tests and examples.

## Global Flags

- `--json`: emit structured JSON output
- `--progress`: stream newline-delimited JSON progress lines to **stderr** while long lifecycle commands run (`{"progress":"game deploy","phase":"bridge.publish","elapsedMs":1234}`). Off by default. stdout keeps exactly the structured result, so `--json ... | jq` is unaffected. Phases are emitted for the deploy build command, the bridge build/publish/stage/copy, the attach poll, post-attach stability samples, the quiescence wait, and the process-stop wait; with `--progress` the bridge build's own `dotnet` output also streams through to stderr as it arrives instead of appearing only on failure.
- `--config <path>`: load a YAML config file
- `--mode <normal|dangerous>`: select CLI mode metadata and gate dangerous raw-input commands
- `--instance <name>`: target a named game instance with its own bridge socket and Godot user-dir (also settable via `SPIRECTL_INSTANCE`, otherwise `instances.default`); use `auto` to allocate a random name. See [Multi-Instance](#multi-instance).
- `--isolated-build`: with an active instance, give the instance its own game/mods mirror so it can run a different bridge build alongside others (Unix only). This can force isolated mode on when `instances.isolatedBuild` is false.

## Config Files

By default, the CLI layers config in this order:

1. built-in defaults
2. `sts2.config.yaml`
3. `sts2.local.yaml`

### Where the default stack is read from

The CLI does not require you to stand in the directory that holds the config. The directory the
default stack is read from is resolved in this order:

1. `--config <path>` — pins that one file and bypasses the stack entirely.
2. `SPIRECTL_CONFIG_DIR` — read `sts2.config.yaml`/`sts2.local.yaml` from this directory. The
   command fails with `config_dir_missing` (exit 2) if neither file is there, so a stale value in
   your environment cannot silently fall through to defaults.
3. The nearest ancestor of the working directory that holds `sts2.config.yaml` or
   `sts2.local.yaml`. The walk stops without a match at the first directory containing `.git`, so
   a checkout never picks up config from outside itself.
4. The working directory, unchanged.

Everything anchored on the config directory follows the resolved directory: relative config values,
the profiles/hooks files, managed toolchain roots, and the `sts2.local.yaml` autodiscovery cache
write. Running `sts2` from a subdirectory therefore behaves the same as running it from the root.

- `sts2.config.yaml` is the committed baseline config and should stay free of machine-specific absolute STS2 paths.
- Copy `sts2.local.example.yaml` to `sts2.local.yaml` for local `game.path`, `game.assembliesDir`, `game.resourcesDir`, `game.modsDir`, and optional `game.modLoadout` overrides, then keep `sts2.local.yaml` uncommitted.
- M28 also adds repo/project ownership keys:
  - `tools.gdrePath`
  - `toolchain.dir`
  - `toolchain.sharedCacheDir`
  - `project.profilesFile`
- M64 adds opt-in local command tracking keys:
  - `usageTracking.enabled`
  - `usageTracking.dir`
- Multi-instance defaults may be set in local config:
  - `instances.default`
  - `instances.isolatedBuild`
- When no `--config` flag is passed, `sts2.local.yaml` automatically overrides matching values from `sts2.config.yaml`.
- When the default config stack resolves `game.path` through Steam autodiscovery, commands cache the detected STS2 install root into `sts2.local.yaml`; commands that resolve an assemblies directory also cache missing `game.assembliesDir`.
- Use `--config <path>` when you want to bypass the default stack and pin a specific config file for a single command or script.
- Explicit `--config <path>` usage is read-only for this cache behavior: it never creates or rewrites sibling `sts2.local.yaml`.
- `game.path: auto` now searches Steam library metadata on the current host OS. Under WSL, the same auto mode also checks Windows Steam roots under `/mnt/<drive>/...`.
- Treat copied sibling mod-repo `libs/` folders as fallback-only unless you explicitly want them.

Usage tracking is disabled by default. Set `usageTracking.enabled: true` in local config to write top-level canonical command paths to `<usageTracking.dir>/cli-history.txt` and aggregate counts to `<usageTracking.dir>/cli-stats.csv`; arguments, flags, paths, ids, URLs, inline bodies, timestamps, exit codes, and environment values are not recorded. The default tracking directory is `./.sts2`, and writes are best-effort so tracking cannot fail the user command.

## Resolving Config Into Paths

`sts2 --json config resolve [key...]` prints the install paths the CLI itself would use, already
absolute, so a script never has to re-parse `sts2.local.yaml` by hand:

```bash
sts2 --json config resolve
sts2 --json config resolve modsDir
sts2 --json config resolve gamePath assembliesDir
```

Keys: `gamePath`, `assembliesDir`, `resourcesDir`, `modsDir`, `instancesDir`, `artifactsDir`. With no
keys, all six are returned. An unknown key fails with `unknown_config_key` (exit 2) and lists the
valid ones.

Every path goes through the same `install_paths` resolvers the real commands use, so the answer
cannot drift from what the CLI actually does — including Steam autodiscovery and assemblies/resources/
mods directories derived from `game.path`. The payload is flat:

| Field | Meaning |
| --- | --- |
| `<key>` | absolute path, or `null` when it cannot be resolved |
| `sources.<key>` | `local-config`, `checked-in-config`, `explicit-config`, `discovered` (Steam autodiscovery), `derived` (from `game.path`), or `default` |
| `configDir` | the directory the default config stack was read from |
| `configDirSource` | `explicit-config`, `environment`, `discovered`, or `current-dir` |
| `configFiles` | the config files that actually contributed, in layering order |
| `errors` | `{key, message}` for each requested key that could not be resolved |

The command is read-only and always exits 0 when the keys are valid: an unresolvable path is
reported as `null` plus an `errors` entry rather than failing the whole call, so one missing key
never hides the ones that did resolve. It does not write the `sts2.local.yaml` autodiscovery cache.

`instancesDir` and `artifactsDir` follow the same rule the rest of the CLI applies to them: a
relative configured value is anchored on the working directory, not on `configDir`.

## Shell Completion

CLI-managed shell completion is shipped as part of the completed M17 work, and the same completion commands are exposed through the npm wrapper's packaged or resolved `sts2` binary. The completion text is generated from the authoritative clap command tree, so new commands and flags do not require a second hand-maintained completion definition.

Supported command shapes:

```bash
sts2 completion
sts2 completion bash
sts2 completion zsh
sts2 completion fish
sts2 completion powershell
sts2 completion install
sts2 completion install zsh
sts2 completion install --path ~/.config/spirectl/completions/sts2.bash
sts2 --json completion fish
sts2 --json completion install powershell
```

Behavior:

- Bash, Zsh, Fish, and PowerShell are the initial shell targets
- omitting the shell auto-detects the current shell where possible
- explicit shell arguments override detection for cross-shell or CI usage
- `install` writes to a spirectl-managed per-user completion directory by default instead of a native shell-owned completion directory
- `install` keeps the direct `source` or dot-source activation line for compatibility and also reports shell-specific activation strategies when a native lazy-load path exists
- wrapper and `npx` flows reuse this exact CLI behavior through `sts2 completion ...` and `sts2 completion install ...`; the wrapper does not add shell-profile mutation or a second install mechanism

Shell-specific activation strategy behavior:

- Bash adds a `bash-completion-user-dir` strategy that symlinks to the per-user `bash-completion` directory for on-demand loading when `bash-completion` is installed and sourced
- Fish adds a `fish-user-completions` strategy that symlinks to the standard Fish user completions directory for on-demand loading
- Zsh adds a `zsh-fpath` strategy only when the current environment exposes a user-owned absolute `FPATH` entry; the target directory must already be in `fpath`, and shell startup must run `compinit`
- PowerShell keeps only the direct dot-source guidance in this slice; the comparable persistent model is still profile/module-based rather than a per-command lazy-load directory

Output shape:

- `sts2 completion ...` without `--json` prints raw completion text to `stdout`
- `sts2 --json completion ...` returns:
  - `source`
  - `command`
  - `shell`
  - `shellSource`
  - `script`
- `sts2 completion install ...` writes the file, and `--json` returns:
  - `source`
  - `command`
  - `shell`
  - `shellSource`
  - `path`
  - `pathSource`
  - `activationCommand`
  - `activationStrategies[]`
  - `managedDir` for default managed installs, otherwise `null`

`activationStrategies[]` is ordered by immediacy:

- index `0` is always the direct source or dot-source strategy, and its `command` matches `activationCommand`
- later entries are shell-specific native/autoload suggestions when they are available for the active shell/environment
- each strategy includes:
  - `id`
  - `summary`
  - `command`
  - `loadsOnDemand`
  - `notes[]`

Default managed install roots:

- Unix: `XDG_CONFIG_HOME/spirectl/completions` or `~/.config/spirectl/completions`
- Windows: `%APPDATA%\\spirectl\\completions`

## Implemented Read Commands

```bash
sts2 inspect commands
sts2 inspect examples
sts2 inspect ai-tools
sts2 inspect actions
sts2 --json inspect actions --offline
sts2 --config tests/sts2.mock.yaml --json inspect actions
sts2 inspect viewport-presets
sts2 inspect viewport-presets --preset-catalog tests/visual/sample-catalog.sts2.viewport-presets.yaml
sts2 state
sts2 state full
sts2 state screen
sts2 state actions
sts2 state refs
sts2 state
sts2 --json state --watch --max-events 1
sts2 --json state watch --max-events 1
sts2 --json state watch actions --timeout-ms 5000 --poll-interval-ms 250
sts2 --json state watch refs --timeout-ms 5000 --poll-interval-ms 250
sts2 inspect state-schema
sts2 state --rpc-timeout-ms 1000
sts2 --json state full --section eventRoom
sts2 --json models characters
sts2 --json models cards strike-ironclad
sts2 --json models characters ironclad silent
sts2 --json models relics burning-blood
sts2 --json models events room-full-of-cheese
sts2 --json models ancients neow
sts2 --json models acts overgrowth
sts2 --json models monsters jaw-worm
sts2 --json models encounters jaw-worm-weak
sts2 --json models powers strength
sts2 --json models enchantments innate
sts2 --json reference colors
sts2 --json reference colors aqua gold screenBackdrop
sts2 --json reference version
sts2 --json dev wait-for-transitions --timeout-ms 5000 --interval-ms 50 --stable-samples 3
sts2 --json dev console help
sts2 --json dev console draw 3
sts2 --json dev console help draw
sts2 --json dev console die
sts2 --mode dangerous --json dev console achievement ...
sts2 --json dev heal --amount 10
sts2 --json dev heal --target creature:2 --full
sts2 dev logs
sts2 dev log-health --tail 100
sts2 dev diagnostics --bundle-dir ./.sts2/artifacts/manual-diagnostics
sts2 --json dev mod-reload --project ./mods/MyHotMod --build --wait
sts2 --json dev mod-reload status --project ./mods/MyHotMod
sts2 dev debug session start --name smoke-investigation --pause
sts2 dev debug session start --role observer --name transcript-watcher
sts2 dev debug session status --id dbg:1
sts2 dev debug status --session dbg:1
sts2 dev debug wait --session dbg:1 --timeout-ms 5000
sts2 dev debug events --session dbg:observer --from-sequence 1 --limit 50
sts2 dev debug events --session dbg:observer --follow --timeout-ms 1000
sts2 dev screenshot --preset desktop-1080p --output ./.sts2/artifacts/combat-1080p.png
sts2 dev breakpoint add --session dbg:1 --path combat.players[0].energy --kind change --min-hit-count 2 --auto-remove-on-hit
sts2 dev screenshot-diff --baseline tests/scenarios/baselines/mock-main-menu.png --bundle-dir ./.sts2/artifacts/visual/main-menu
sts2 dev screenshot-diff --baseline tests/scenarios/baselines/mock-main-menu.png --actual tests/scenarios/baselines/mock-main-menu.png
sts2 dev screenshot-diff --baseline tests/scenarios/baselines/lobby.png --actual ./.sts2/artifacts/visual/lobby.png --mask ./.sts2/artifacts/visual/lobby-mask.png --regions ./.sts2/artifacts/visual/lobby-regions.json --foreground-max-diff-ratio 0.001 --roi-max-diff-ratio 0.001 --required-comparison foreground --required-comparison roi --bundle-dir ./.sts2/artifacts/visual/lobby-diff
sts2 dev snapshot export --spec tests/snapshots/main-menu.sts2.snapshot.yaml --output ./.sts2/artifacts/snapshots/main-menu
sts2 dev snapshot compare --spec tests/snapshots/main-menu.sts2.snapshot.yaml --baseline tests/snapshots/baselines/main-menu --bundle-dir ./.sts2/artifacts/snapshots/main-menu

Snapshot export and comparison metadata uses repository-relative paths for files inside the resolved checkout, `$HOME/...` for paths under the user home, and absolute paths only for unrelated external files. This keeps checked-in snapshot bundles portable across machines.
sts2 dev wait-for run.currentRoom.scene --equals rooms/combat_room
sts2 dev assert run.players[id=p1].combat.energy --gte 1
sts2 dev scene tree /root
sts2 --json dev scene tree /root --properties --computed-transform
sts2 dev scene node /root
sts2 --json dev scene node /root/CombatScreen/BgContainer --properties --computed-transform
sts2 --json dev scene node /root/EventRoom/MegaRichTextLabel --properties
sts2 --json dev scene children /root/CombatScreen --properties
sts2 --json dev scene hover --path /root/CharacterSelect/DEFECT_button --hover-tip
sts2 --json dev scene hover --path /root/Dialog/Panel/List/Row7 --ensure-visible
sts2 --json dev scene unhover --path /root/CharacterSelect/DEFECT_button
sts2 --json dev scene unhover
sts2 dev scene set-visible /root/Game/RootSceneContainer/Run/GlobalUi/DebugInfo --hidden
sts2 --json dev scene set-visible /root/Game/RootSceneContainer/Run/GlobalUi/DebugInfo --visible --verify-after-ms 300
sts2 game detect
sts2 game info
sts2 toolchain info
sts2 project scaffold stable-harmony-trampoline --output ./mods/MyHotMod --mod-id my-hot-mod --name "My Hot Mod" --namespace MyHotMod
sts2 project scaffold stable-harmony-trampoline --output ./mods/MyHotMod --mod-id my-hot-mod --name "My Hot Mod" --namespace MyHotMod --enable-guardrails
sts2 project profile list
sts2 project profile show install-launch-and-attach
sts2 code locate type CombatScreen
sts2 code describe type MegaCrit.Sts2.CardState
sts2 code refs type MegaCrit.Sts2.CardState
sts2 code derived type MegaCrit.Sts2.BaseCard
sts2 code decompile type MegaCrit.Sts2.CardState
sts2 code scene-search HandPanel --resources-dir ./game-project
sts2 code scene-tree res://ui/CombatScreen.tscn --resources-dir ./game-project
sts2 code scene-node res://ui/shared/HandPanel.tscn /HandPanel --resources-dir ./game-project
sts2 --config tests/sts2.mock.yaml test run tests/scenarios/smoke-main-menu.sts2.yaml
sts2 --json test run --profile mock
sts2 --json test run --profile live --tag spec:S74
sts2 --config tests/sts2.mock.yaml test stress --iterations 10 tests/scenarios/regression-main-menu.sts2.yaml
```

`dev screenshot-diff` always reports the historical full-image fields (`matched`, `diffPixels`, `diffRatio`, `maxDiffPixels`, `maxDiffRatio`) when only `--baseline` and optional `--actual`/viewport inputs are supplied. Add `--mask <png>` to compare only foreground pixels where the mask has nonzero color and alpha. Add `--regions <json>` for ROI comparison; the JSON may be either an array of `{ "id", "x", "y", "width", "height" }` objects or `{ "regions": [...] }`. `--foreground-max-diff-ratio` and `--roi-max-diff-ratio` set independent thresholds; every diff ratio threshold must be `>= 0.0` and `< 1.0`. Pixel equality is exact RGBA by default. `--pixel-tolerance <0-255>` sets the largest per-channel delta that still counts as an identical pixel (useful against renderer anti-aliasing jitter), and `--ignore-alpha` compares RGB only. Both apply to the full, foreground and ROI comparators alike, and both are echoed back as `pixelTolerance`/`ignoreAlpha`. They change what counts as a differing pixel, not how many are allowed: `maxDiffPixels` and `maxDiffRatio` keep their exact meaning.

When mask or ROI inputs are used, the comparison JSON adds `comparisons.full`, `comparisons.foreground`, and/or `comparisons.roi` result objects with independent `matched`, `diffPixels`, `diffRatio`, `comparedPixels`, and threshold fields. `--required-comparison foreground` or `--required-comparison roi` makes missing inputs fail with structured notices: `missing-mask` or `missing-region`. Bundles still write `comparison.json`, `baseline.png`, `actual.png`, and `diff.png`; requested foreground and ROI modes additionally write `foreground-diff.png` and `roi-diff.png`.

These commands return real structured envelopes. `state`, `state full`, `state screen`, `state actions`, `state refs`, `state`, `inspect state-schema`, `models`, `reference`, `dev wait-for-transitions`, `dev console`, `dev logs`, `dev log-health`, `dev diagnostics`, `dev mod-reload`, `dev debug`, `dev breakpoint`, `dev screenshot`, `dev screenshot-diff`, `dev snapshot export`, `dev snapshot compare`, `game info`, `inspect actions`, and `inspect viewport-presets` all flow through the typed bridge client or shared CLI-local diagnostics/visual-validation helpers. `state` now returns the current agent decision snapshot with canonical `actions[]`, normalized `scene`, `decision`, `fallbackChoices[]`, gameplay facts, and notices. `state` is experimental and returns a runtime-only envelope with `schemaVersion`, `language`, `rootScene`, `characterSelect`, and `run`; Character Select populates `characterSelect`, active runs populate `run` plus `run.currentRoom.scene`, active combat rooms populate `run.currentRoom.combat` and player-owned `run.players[].combat`, and inactive sections remain `null`. `state` view objects are attached-client local UI state, including `run.map.view.isOpen` and `run.currentRoom.shop.view.isOpen`; they are not remote-player UI state. Other developer, diagnostics, code, asset, project, service, and test commands keep their structured output contracts described below.

`spirectl` does not render screens. There is no `presentation` command group, no scene binding catalog, and no bundled dev renderer; downstream consumers render from live scene state, semantic state, models, and asset bytes.

`dev wait-for-transitions` is a developer bounded wait for scriptable capture and automation. It polls live runtime transition diagnostics until there are stable quiescent samples, treating finite screen transitions, finite running tweens, and non-looping playing animations as blockers while ignoring infinite/idle loops. The JSON response includes attempts, elapsed time, the final status, `blockingCount`, `ignoredInfiniteCount`, `screen`, and compact blocker diagnostics.

`models` exports immutable live model metadata for `characters`, `cards`, `relics`, `potions`, `events`, `ancients`, `acts`, `monsters`, `encounters`, `powers`, `orbs`, `afflictions`, `enchantments`, `card-pools`, `relic-pools`, `potion-pools`, `modifiers`, and `achievements`. Character output includes localized character select text such as `characterSelectTitle`, `characterSelectDesc`, and `unlockText`, plus opaque `model://characters/<id>/...` asset keys for renderable character assets. Cards include canonical costs, X-cost flags, tags, keywords, max upgrade level, `dynamicVars`, and an optional single-step `upgrade` preview containing changed dynamic vars and/or changed `energyCost`; mutable instance upgrade state still lives in runtime state through `upgradeLevel` plus sparse state overrides when model data is insufficient. `events` includes normal events and ancient events; `ancients` is a filtered convenience family with the same event shape and ancient-specific fields such as `epithet` and map/run-history asset keys. The added combat and catalog families include stable common metadata such as `typeName`, `categorySortingId`, `entrySortingId`, and `shouldReceiveCombatHooks` when the game model exposes it. Cross-family references stay as model ID arrays, for example encounter `monsterIds`, pool content IDs, and modifier mutual exclusions. Where the live model exposes a real authored resource, companion `*Path` fields expose the normalized `res://` path. For example, `characterSelectBgAssetKey` remains the rendered/fallback asset key, while `characterSelectBgPath` is the authored Godot scene path such as `res://scenes/screens/char_select/char_select_bg_ironclad.tscn`. Virtual model renders such as `visualsAssetKey` do not have synthetic path companions.

`reference <topic> [keys...]` exports live game reference data that is constant at runtime but is not a game model. It is the sibling of `models`: the topic is a free string the bridge resolves, the surface is extensible, and a JSON envelope is returned with `schemaVersion: spirectl.reference-result/v0`, `topic`, `status` (`ok`, `partial`, `unsupported-topic`, or `unavailable`), `missingKeys`, `notices`, and a topic-specific payload. It is served by the live bridge; the mock transport returns synthetic placeholder data. Built-in topics:

- `reference colors [names...]` returns the `StsColors` palette as a flat `{ name: "#RRGGBB" }` map (keyed by the field name, e.g. `aqua`, `screenBackdrop`). Each value is a `#`-prefixed uppercase hex string — 8 digits (`#RRGGBBAA`) when alpha ≠ 1, otherwise 6 — matching how the game hardcodes the palette; consumers parse the hex themselves. Optional positional `names` filter to specific colors (case-insensitive); unknown names are returned in `missingKeys` with `status: partial`.
- `reference version` returns the game build metadata shown in the in-game DebugInfo node: `version` (e.g. `v0.103.2`), `versionDate` (e.g. `2026.04.16`), `commit`, `branch`, `mainAssemblyHash`, and a `modding` summary (`isRunningModded`, `loadedModCount`, `totalModCount`). The full mod list stays in `game mods`. If `release_info.json` is unavailable the version fields are empty with `status: partial` and a `release-info-unavailable` notice, while the `modding` summary is still populated.

Unknown topics return `status: unsupported-topic` with a notice rather than an error envelope, so adding a topic server-side never breaks existing CLI clients.

The CLI `state` JSON shapes are client formatters over the shared current semantic state, not the embedded runtime contract. `state watch` is the read-only CLI/automation stream over that same current observation path. It emits newline-delimited JSON events with `type`, `sequence`, `observedAtUtc`, `fingerprint`, and either `state` or `error`; the first event is `initial`, later state events are `changed`, and duplicate semantic states are suppressed instead of streamed in a tight loop. Live IPC/TCP bridges use reactive watch when they advertise `state-watch`; otherwise the CLI uses polling fallback. Structured state errors are preserved as `error` events and the stream continues until bounded unless `--fail-fast` is set. Use `--max-events`, `--timeout-ms`, `--poll-interval-ms`, `--rpc-timeout-ms`, and `--watch-mode` to make scripts reproducible, for example `sts2 --json state watch --max-events 1`.

`state --watch` is the same read-only NDJSON stream contract applied to the experimental `state` envelope: each event carries `type`, `sequence`, `observedAtUtc`, `fingerprint`, and either a full `state` (the same shape as `state`) or `error`. The first event is `initial`, later state events are `changed`, and duplicate envelopes are suppressed. Live IPC/TCP bridges use reactive push when they advertise `state-watch`; otherwise the CLI falls back to polling. It shares the `--max-events`, `--timeout-ms`, `--poll-interval-ms`, `--rpc-timeout-ms`, `--watch-mode`, and `--fail-fast` flags, for example `sts2 --json state --watch --max-events 1`. The reactive bridge push is driven by the game's main-thread tick with semantic-fingerprint de-duplication, so latency is roughly one capture interval and a quiescent game emits nothing. Embedded callers (for example `../sts2-couch-coop`) should subscribe in-process via `ISpirectlRuntime.SubscribeCurrentState` / `WatchCurrentStateAsync` rather than shelling out to this CLI stream.

Runtime text diagnostics from `dev scene node --properties` include effective theme font/color/outline values and `properties.text.shadow` when Godot exposes label or rich-text shadow metadata. In live visual renders, `--asset-format auto` keeps larger art in WebP but prefers PNG for alpha-sensitive icon and glyph roles.

`dev scene hover` is reachability-aware. A node path resolves wherever the node has been scrolled to, so the hover position used to be the centre of the target's global rect even when that point was off the viewport or outside the clip of an ancestor `ScrollContainer` — a coordinate that hovers, and that a following `act mouse click` lands on, whatever else is painted there. The hover now intersects the target's rect with every clip that governs it (each ancestor with `clip_contents`, each ancestor `ScrollContainer`, then the root viewport's visible rect) and:

- refuses a target with nothing left, naming the clip that hid it (`clipped_by` error details carry the node path and its rect, plus a `remedy` detail);
- hovers the centre of the surviving region when the target is only partly visible, so the position is always on the target (a fully visible target keeps the exact centre it has always returned);
- reports what it found under `visibility`: `fullyVisible`, `controlRect`, `visibleRect`, `clippedBy[]` (`{nodePath, nodeType, reason, rect}` with `reason` one of `viewport`, `clip-contents`, `scroll-container`) and `scrolled[]`.

`--ensure-visible` scrolls the target's ancestor scroll containers into range first, innermost outwards, one container per frame so each pass measures the moved rect rather than the stale one; every container it moved is reported in `visibility.scrolled` as `{nodePath, previousHorizontal, previousVertical, horizontal, vertical}`, and in `notes`. It mutates the game's own UI, which is why it is opt-in. `--allow-offscreen` restores the old behaviour for a caller that genuinely wants the unreachable coordinate; the response then carries a note saying the pointer landed on whatever else is painted there.

`state watch` is a CLI/automation wrapper over S119 current observation. Live IPC/TCP bridges that advertise `state-watch` use a reactive bridge stream by default; older bridges fall back to the polling loop, and `--watch-mode poll` keeps explicit compatibility behavior. `--poll-interval-ms` is the polling interval in poll mode and the minimum capture/coalescing interval in reactive mode. It is not CouchCoop, browser startup, route/session/retry semantics, a WebSocket envelope, the latest-state cache, a screenshot stream, logs, debugger events, raw scene-tree inspection, or a UI contract, and it never executes semantic actions. Embedded callers should use `GetCurrentState(CurrentStateRequest)` for direct current observation and treat `state screen` / `state actions` / `state watch` as derived CLI conveniences over the same observed state.

## Scenario And Recorded Fixture Restore

M53 implements shareable sparse scenario export/load. M59 replaces the local checkpoint command family with recorded screen-entry fixtures under ignored `.sts2/fixtures/current-screen/`.

```bash
sts2 dev scenario export --output ./scenarios/combat.sts2.scenario.yaml
sts2 dev scenario export --output ./scenarios/combat.sts2.scenario.yaml --include-exact
sts2 dev scenario load --path ./scenarios/combat.sts2.scenario.yaml
sts2 dev scenario load --path ./scenarios/combat.sts2.scenario.yaml --restart
sts2 dev scenario load --path ./scenarios/combat.sts2.scenario.yaml --allow-degraded-local-multiplayer
sts2 dev fixture record
sts2 dev fixture status
sts2 dev fixture resume
sts2 dev fixture resume --restart
sts2 dev fixture clear
sts2 dev fixture load --path ./fixtures/basic-combat.sts2.fixture.yaml
sts2 dev fixture load-latest
sts2 dev fixture load-latest --restart
```

Scenario export writes reviewable sparse `spirectl.scenario/v0` YAML by default, and `--include-exact` writes a separate opaque sidecar when native save-backed or fixture-backed continuation data is available. Exact sidecars are hash/size validated and remain distinct from sparse recipe fixtures, runner `dev.load-scenario` artifacts, and recorded screen-entry fixtures. Export JSON includes current `restoreSupport` with field-level support classes (`exact`, `partial`, `inferred`, `omitted`, `unsupported`, or `degraded-local-multiplayer`), reason codes, and suggested next steps. Scenario load reports restore quality, exact-vs-sparse usage, validation keys, expected/observed summaries, `validation.mismatches[]` with `fieldPath`, `supportClass`, `reasonCode`, and `suggestedNextStep`, optional `multiplayerRestore`, and any bridge verification payload; validation mismatches fail nonzero with structured mismatch fields. Active multiplayer artifacts must use `--allow-degraded-local-multiplayer` before local-only restore may omit remote clients, and the result must report those omissions.

`dev fixture record` means "record the recipe for entering this screen", not "save the exact current runtime moment." A combat recorded after cards have been played resumes to the combat entry setup, with played cards, transient queues, relic turn history, current RNG continuation, enemy AI history, and partially executed action state explicitly omitted. `dev fixture resume --restart` uses restart-stop semantics before loading the recorded recipe. `dev fixture load` remains the authored sparse recipe loader and replaces the old direct `dev load-fixture` command in public docs. The JSON result includes top-level `recipeReport` and `loaded.recipeReport` with `recipeName`, `appliedFields`, `inferredFields`, `omittedFields`, `unsupportedFields`, `degradedMultiplayerFields`, and `bridgeValidation.status/details`; field reports use `fieldPath`, `valueSummary`, `reasonCode`, and `message`. Local validation rejects impossible authored values with reason codes such as `invalid_run_act`, `invalid_run_floor`, `hp_exceeds_max_hp`, `invalid_lobby_player`, `unknown_screen_id`, `unsupported-recipe-field`, and `invalid-authored-value`. See [scenario-contracts](./scenario-contracts.md) for the scenario contract and recorded fixture semantics.

Every successful `dev fixture load` also records the requested fixture path as the worktree's latest fixture in `.sts2/fixtures/last-loaded.json` (CWD-relative, gitignored, surfaced in the load result under `latestFixtureRecorded`). `dev fixture load-latest` replays that fixture — re-resolving the path against the current working directory — and supports `--restart`/`--timeout-ms`/`--interval-ms` exactly like `dev fixture resume`. Because the pointer is per-worktree, this composes with `scripts/foreach-worktree.sh sts2 dev fixture load-latest` to reload each worktree's own last fixture across every running game instance. With no recorded fixture it fails nonzero (exit code 2) with error code `latest_fixture_not_found`.

## Toolchain And Project Commands

Common M28 flows:

```bash
sts2 --json toolchain info
sts2 --json project recover --kind decompile
sts2 --json project recover --kind all
sts2 --json project profile list
sts2 --json project profile show install-launch-and-attach
sts2 --json project profile run install-launch-and-attach
```

Common M60 scaffold flow:

```bash
sts2 project scaffold stable-harmony-trampoline \
  --output ./mods/MyHotMod \
  --mod-id my-hot-mod \
  --name "My Hot Mod" \
  --namespace MyHotMod
cd ./mods/MyHotMod
cp sts2.local.example.yaml sts2.local.yaml
sts2 project profile run my-hot-mod-shell-deploy-restart
sts2 project profile run my-hot-mod-logic-build
touch <deployed-mod>/hot-reload/reload.marker
```

Optional guardrail-enabled scaffold:

```bash
sts2 project scaffold stable-harmony-trampoline \
  --output ./mods/MyHotMod \
  --mod-id my-hot-mod \
  --name "My Hot Mod" \
  --namespace MyHotMod \
  --enable-guardrails
```

`project scaffold stable-harmony-trampoline` refuses non-empty output directories and returns a structured summary with created files, generated profile names, local config guidance, guardrail metadata, and next commands. Generated profiles keep shell deployment on `game deploy`; the logic-build profile is a repo-local command step that builds/copies the reloadable logic DLL but does not own explicit reload triggering. `--enable-guardrails` writes `EnableHotReloadGuardrails=true` as the generated default only; developers can still override it with normal MSBuild properties, and the dev overlay remains off until the generated local launch env sets `<MOD>_DEV_OVERLAY` to `1` or `true`.

Behavior:

- `toolchain info` reports:
  - managed repo-local toolchain root
  - shared cache root
  - checked-in profiles file path
  - provenance for those resolved values plus `tools.dotnetToolsPath` and `tools.gdrePath`
  - static-inspection helper launch diagnostics in JSON output, including the configured helper path, resolved invocation path, launch kind, availability, and whether a helper build is needed
  - `launch.staleChecked: false`, because `toolchain info` never builds: it skips the recursive scan of the helper project that decides whether the built DLL is stale, and says so in `launch.notes`. A missing build output is still reported (that is one existence check). Any `code`/`assets` command re-checks staleness eagerly before launching the helper, so this only affects the report, never a build decision.
- `project recover --kind decompile` rewrites only `<toolchain.dir>/decompile/` and `<toolchain.dir>/manifests/decompile.json`
- `project recover --kind recovered-project` rewrites only `<toolchain.dir>/recovered-project/` and `<toolchain.dir>/manifests/recovered-project.json`, invoking GDRE with the resolved game resource root when `tools.gdrePath` is available and returning structured failure metadata otherwise
- With the default managed root, the CLI-owned recovery outputs are `.sts2/toolchain/decompile/sts2` for decompiled STS2 game sources and `.sts2/toolchain/recovered-project` for recovered game resources and scenes.
- `project recover --kind all` preserves successful outputs on disk even when another requested kind fails, and exits nonzero when any requested kind failed
- `project profile show` resolves relative `deploy.path` and command `cwd` values against the profile file location, interpolates supported `${config:...}` values for command args/env, and applies step defaults in the rendered JSON (`build` / `restart` / `verify` default false; `timeoutMs` / `intervalMs` default to `30000` / `250`)
- `project profile run` honors per-step `build`, `restart`, `verify`, `timeoutMs`, and `intervalMs` settings, executes `kind: command` steps with captured stdout/stderr/status, and still fails fast on the first failed step while returning earlier completed steps in `steps[]`

## AI Tool Catalog

`sts2 --json inspect ai-tools` is the authoritative AI-facing surface description.

Its top-level JSON shape is:

- `source`
- `adapter`
- `tools[]`

Each tool entry includes:

- `name`
- `summary`
- `status`
- `readOnly`
- `mapsTo`
- `inputSchema`
- `outputShape`
- `limitations`
- `recommendedUsage`

The exposed tool set is intentionally smaller than the full CLI:

- `game_detect`
- `game_info`
- `toolchain_info`
- `game_launch`
- `game_attach`
- `game_deploy`
- `project_profile_list`
- `project_profile_show`
- `project_profile_run`
- `project_hook_list`
- `project_hook_show`
- `project_hook_run`
- `assets_extract`
- `assets_explain`
- `assets_extract_batch`
- `state`
- `inspect_actions`
- `act`
- `logs`
- `log_health`
- `diagnostics`
- `hot_reload_status`
- `hot_reload`
- `http`
- `http_wait`
- `fetch`
- `websocket`
- `debug_session_start`
- `debug_session_status`
- `debug_session_end`
- `debug_events`
- `load_fixture`
- `skill_install`
- `screenshot`
- `inspect_viewport_presets`
- `screenshot_diff`
- `snapshot_export`
- `snapshot_compare`
- `debug_status`
- `debug_pause`
- `debug_resume`
- `debug_step`
- `debug_wait`
- `breakpoint_list`
- `breakpoint_add`
- `breakpoint_remove`
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

The AI catalog still excludes lower-level or intentionally narrow surfaces such as `game install-bridge`, `completion`, `completion install`, `inspect commands`, `inspect examples`, `dev scene tree`, `dev scene node`, `dev scene children`, `dev scene set-visible`, shell deploy/restart, source-editing helpers, and arbitrary native mod hot reload.
Dangerous raw-input fallbacks such as `act mouse click` also stay outside the standalone AI tool surface.

`sts2-mcp` remains a thin stdio adapter over that catalog, and [mcp-migration](./mcp-migration.md) documents the downstream replacement path and safe-delete criteria for retiring vendored legacy MCP trees.

## Automation Service

`sts2 service serve` runs the CLI-owned HTTP/JSON automation service described in [automation-service](./automation-service.md).

Supported M44 endpoints:

- `GET /v0/service`
- `GET /v0/ai-tools`
- `POST /v0/tools/call`
- `GET|POST|DELETE /v0/mcp` when `--mcp-mode network` is enabled
- `POST /v0/debug-sessions`
- `GET /v0/debug-sessions/{id}`
- `DELETE /v0/debug-sessions/{id}`
- `GET /v0/debug-sessions/{id}/events`
- `POST /v0/debug-sessions/{id}/events`
- `POST /v0/debug-sessions/{id}/wait`
- `POST /v0/test-runs`
- `GET /v0/test-runs/{runId}`
- `GET /v0/test-runs/{runId}/artifacts`
- `GET /v0/test-runs/{runId}/artifacts/{relativePath...}`

Key boundaries:

- loopback-safe by default; non-loopback binds require `--auth-token`
- network MCP is opt-in through `--mcp-mode network`; its default bind is loopback, and non-loopback MCP binds require `--mcp-auth-token` plus `--acknowledge-non-loopback-mcp-threat-model`
- startup prints one JSON metadata line in `--json` mode
- `GET /v0/ai-tools` stays aligned with `sts2 --json inspect ai-tools`
- remote `test run` preserves the existing artifact tree and adds additive `remoteArtifacts[]` download metadata on completed summaries
- only `test run` has first-class remote artifact download support in this slice

Safe loopback service plus network MCP:

```bash
sts2 --json service serve \
  --listen 127.0.0.1:4317 \
  --mcp-mode network \
  --mcp-listen 127.0.0.1:4318
```

Public MCP binds must make auth and acknowledgement visible:

```bash
STS2_MCP_AUTH_TOKEN='replace-with-a-secret' \
sts2 --json service serve \
  --listen 127.0.0.1:4317 \
  --mcp-mode network \
  --mcp-listen 0.0.0.0:4318 \
  --mcp-auth-token "$STS2_MCP_AUTH_TOKEN" \
  --acknowledge-non-loopback-mcp-threat-model
```

## Bridge-Backed Actions

S85 is a breaking current action/choice contract. First-party modeled flows should be driven from the typed state section and the advertised intent action, not from generic visible-choice IDs. `choices[]` remains an observable affordance list with `id`, `choiceKind`, `intentKind`, `label`, `description`, `ownerPlayerId`, `perspective`, typed `arguments`, `enabled`, `disabledReason`, and optional `preferredAction`; when `preferredAction` is present, use that action as the first-party path. Generic `choose` is a fallback choose surface for generic, modded, or unmodeled visible controls, or for a compatibility entry that has no modeled `preferredAction`.

```bash
sts2 act play-card --card c_1 --target e_1
sts2 act use-potion --potion potion:p1:0:fire-potion --target e_1
sts2 act confirm-selection
sts2 act cancel-selection
sts2 act select-map-node --node map-node:3:1
sts2 act back-from-map
sts2 act claim-reward --reward reward:p1:0
sts2 act skip-rewards
sts2 act select-card --card card-selection:card:bash:0
sts2 act skip-card-selection
sts2 act select-bundle --bundle card-selection:bundle:offensive-pack:0
sts2 act buy-card --shop-item shop:p1:card:strike:0
sts2 act buy-relic --shop-item shop:p1:relic:anchor:0
sts2 act buy-potion --shop-item shop:p1:potion:fire-potion:0
sts2 act remove-card --shop-item shop:p1:card-removal:0
sts2 act leave-shop
sts2 act close-shop-inventory
sts2 act rest
sts2 act smith
sts2 act use-rest-site-option --option heal
sts2 act proceed-rest-site
sts2 act open-chest
sts2 act take-relic --relic anchor
sts2 act proceed-treasure-room
sts2 act select-event-option --event-option event-room:gain-gold:0
sts2 act open-event-shop --event-option event-room:fake-merchant:open-shop
sts2 act use-crystal-sphere-control --control crystal-sphere:tool:big
sts2 act proceed-event
sts2 act toggle-map
sts2 act toggle-deck
sts2 act toggle-settings
sts2 act end-turn
sts2 act ready
sts2 act unready
sts2 act select-character --character silent
```

These semantic actions route through the runtime bridge contract.

- `choose`: fallback-only compatibility action for generic, modded, or unmodeled visible choices when no modeled `preferredAction` exists; old choose-heavy IDs such as `reward:<player-id>:<rewards-set-index>`, `event-room:<text-key>:<index>`, `treasure-room:relic:<model-id>:<index>`, `rest-site:<player-id>:<option-id>:<index>`, `shop:<player-id>:card:<model-id>:<slot-index>`, `map:back`, `card-selection:card:<model-id>:<index>`, and `reward-flow:skip` may still appear for migration and fallback, but first-party docs should prefer the typed section plus `preferredAction`
- `confirm-selection`: executable for staged `simple-card-selection` and `deck-card-selection` overlays when the live overlay state exposes a legal follow-through path
- `cancel-selection`: executable for staged `simple-card-selection` and `deck-card-selection` overlays when the live overlay state exposes a legal staged-selection rollback path
- `select-map-node`: executable on the map screen for currently travelable node IDs such as `map-node:3:1`
- `back-from-map`: executable when the visible map back control is legal
- `claim-reward` / `skip-rewards`: executable for visible reward entries and visible reward proceed/skip flow
- `select-card` / `skip-card-selection` / `select-bundle`: executable for supported visible card-selection, simple/deck selection, and bundle-selection flows
- `buy-card` / `buy-relic` / `buy-potion` / `remove-card` / `leave-shop` / `close-shop-inventory`: executable for legal visible shop inventory, card-removal, normal shop exit, and opened Fake Merchant inventory close controls
- `rest` / `smith` / `use-rest-site-option` / `proceed-rest-site`: executable for visible legal rest-site options and proceed flow
- `open-chest` / `take-relic` / `proceed-treasure-room`: executable for visible treasure-room and relic-selection controls
- `select-event-option` / `open-event-shop` / `use-crystal-sphere-control` / `proceed-event`: executable for modeled visible event-room controls, including the Fake Merchant pre-open and Crystal Sphere slices
- `toggle-map` / `toggle-deck` / `toggle-settings`: toggle the run top-bar map, deck, and settings surfaces using the game’s native top-bar controls
- `view-draw-pile` / `view-discard-pile` / `view-exhaust-pile`: open the in-game combat card pile viewer (`NCardPileScreen`) for the local player by releasing the combat HUD pile button. The pile contents are already in `state` at `run.players[].combat.{draw,discard,exhaust}Pile.cards`; when the viewer is open, `run.view.capstone.cardPileView` reports `{ pileType, playerId }`. `view-exhaust-pile` is only surfaced when the exhaust pile is non-empty (the in-game button hides while empty)
- `end-turn`: executable now for combat when the action is currently legal
- `play-card`: executable in combat for currently legal hand cards, with `targetId` required when the chosen card surfaces legal targets
- `use-potion`: executable in combat for currently usable potion ids from `state.combat.potions[]`, with `targetId` required when the chosen potion surfaces legal combat targets
- `ready`: executable in multiplayer lobby screens when the local player can currently ready up
- `unready`: executable in multiplayer lobby screens when the local player is already ready
- `select-character`: executable in multiplayer start-run and load-run lobbies for visible, unlocked, not-already-selected characters

For live main-menu state, `choices` may still show `menu:start-run` even when `availableActions` omits it. When that happens, `state.notices[]` includes `main-menu-start-run-hook-unavailable`, and a forced `sts2 act choose --choice menu:start-run` failure reports the active screen class plus the resolved hook path, checked probe paths, and present candidates from the bridge inspection result.

Intent-first JSON examples:

```bash
sts2 --json state
sts2 --json state --perspective local --player-id p1
sts2 --json inspect actions
sts2 --json inspect actions --offline
sts2 --config tests/sts2.mock.yaml --json inspect actions
sts2 --json act claim-reward --reward reward:p1:0
sts2 --json act skip-rewards
sts2 --json act select-card --card card-selection:card:bash:0
sts2 --json act select-bundle --bundle card-selection:bundle:offensive-pack:0
sts2 --json act buy-card --shop-item shop:p1:card:strike:0
sts2 --json act remove-card --shop-item shop:p1:card-removal:0
sts2 --json act leave-shop
sts2 --json act rest
sts2 --json act smith
sts2 --json act use-rest-site-option --option heal
sts2 --json act open-chest
sts2 --json act take-relic --relic anchor
sts2 --json act proceed-treasure-room
sts2 --json act select-map-node --node map-node:3:1
sts2 --json act back-from-map
sts2 --json act select-event-option --event-option event-room:gain-gold:0
sts2 --json act open-event-shop --event-option event-room:fake-merchant:open-shop
sts2 --json act use-crystal-sphere-control --control crystal-sphere:tool:big
sts2 --json act toggle-map
sts2 --json act toggle-deck
sts2 --json act toggle-settings
```

Dangerous raw fallback:

```bash
sts2 --mode dangerous act mouse click --x 100 --y 200
sts2 --mode dangerous act mouse click --x 960 --y 540 --button right
```

- `mouse click`: dispatches a raw viewport click using screenshot/viewport coordinates and bypasses semantic legality checks
- dangerous raw input is only advertised through `supportedActions` when the CLI runs in `--mode dangerous`
- dangerous raw input is intentionally omitted from `availableActions` and `inspect ai-tools`
- event-room, treasure-room, shop, reward, rest-site, Crystal Sphere, card-selection, and map-flow compatibility IDs can still appear in `choices[]`, but current clients should read the typed family section and follow `preferredAction` before using fallback choose
- staged `simple-card-selection` and `deck-card-selection` overlays use `select-card` for card picks plus `confirm-selection` / `cancel-selection` for follow-through; simple cancel clears staged picks, while deck confirm/cancel respects the preview screen flow before clearing staged picks outside preview
- passive card detail overlays expose typed `cardOverlay` data while leaving the underlying screen choices authoritative; blocking card overlays take precedence and may expose validated close/back compatibility controls, while unsupported or partial overlays emit structured notices instead of raw scene-tree internals
- generic `choose` is retained for currently executable compatibility controls or unmodeled visible overlay controls only; S85-S86 own the broader intent-first action model for first-party overlay verbs

Confirm-like UI buttons stay modeled as `choose` IDs when they are generic menu choices. Lobby-specific ready flows use `ready` / `unready`, but lobby state now still exposes visible local ready/unready and character-row `choices[]` with stable `lobby:*` ids for observation and query/assert flows.

`availableActions` is now stricter and only lists actions that are truly executable in the current state. In other words, `inspect actions` can still show bridge-known action kinds, while `state.availableActions` carries the screen-specific executable card/target, map-node, map-flow, or lobby action combinations.

On supported live screens, `choices[]` can still be intentionally broader than `availableActions[]`. Besides the main-menu `main-menu-start-run-hook-unavailable` case, map, rewards, event-room, treasure-room, rest-site, shop, and card-selection now emit family-specific `state.notices[]` entries when visible choices are present but currently non-executable.

`sts2 --json inspect actions --offline` renders static `stateContract` and `supportedActions` metadata without opening the live bridge transport. `sts2 --config tests/sts2.mock.yaml --json inspect actions` is the deterministic mock shortcut for validating the same JSON shape in repo-local docs and tests without a live host.

`inspect actions --json` now includes:

- `screen`
- `resolvedPerspective`
- `notices`
- `supportedActions`
- `availableActions`

Each supported action descriptor includes:

- `status`: `implemented` or `scaffolded`
- `parameters`: explicit argument metadata for JSON-first callers
- `kindDescriptor`: current action metadata including `intentKind`, modes, screen applicability, and whether the action is a fallback
- `argumentSchema`, `ownerPlayerId`, `perspectiveBehavior`, `checkedHookPaths`, and `failureReasonCodes`

This lets automation tell the difference between bridge-known action kinds and the currently executable action instances for the active screen.

For player-scoped runtime screens, visible `choices[]` can also carry additive `ownerPlayerId` even before a matching `availableActions[]` entry exists. The live/mock map, rewards, event-room, treasure-room, rest-site, shop, and card-selection slices now expose that field when the bridge already knows which player owns the visible choice.

For S83/S84-style non-combat and overlay state, the typed section is the preferred read surface and `choices[]` / `availableActions[]` are compatibility fallbacks. Supported families use `map.nodes[]`, `eventRoom.options[]`, `treasureRoom.relics[]`, `relicSelection.relics[]`, `restSite.controls[]`, `shop.purchasableItems[]`, `rewards.rewards[]`, `cardSelection.cards[]`, `simpleCardSelection.choices[]`, `deckCardSelection.deckCards[]`, `bundleSelection.bundles[]`, `lobby.players[]` / `availableActions[]`, and typed `cardOverlay` detail when observable. Blocking overlays take precedence in default state, passive overlays retain the underlying screen as authoritative while adding overlay detail, and unsupported or partial overlays should emit structured notices instead of raw scene-tree internals. Compatibility choices and executable actions may include `choiceKind`, `intentKind`, `ownerPlayerId`, `perspective`, and `preferredAction`; structured notices use `path`, `severity`, `source`, `stability`, and `perspective` when the bridge can explain partial, inferred, or perspective-filtered data.

Action failures use a structured error shape across bridge, CLI, runner, wrapper, service, and MCP passthroughs. Callers should preserve `actionFailure`, stable `reasonCode` values such as `wrong-screen`, `wrong-player`, `not-visible`, `not-enabled`, `missing-hook`, `ambiguous-hook`, `stale-id`, `unsupported-perspective`, and `dangerous-mode-required`, plus `screen`, `playerId`, `perspective`, and checked hook paths when present. Do not parse prose messages to determine legality.

S87 adds explicit multiplayer ownership and perspective semantics to that same surface. `state --perspective local --player-id <id>` asks the bridge to resolve state as that local player when the runtime can prove the selected player is local or otherwise mediated by a configured client. Local-owned controls should expose matching `ownerPlayerId`, while remote-owned controls remain visible only to the degree they are observable and should report local-only, host-mediated, configured-client, unavailable, or unsupported remote-orchestration capability metadata instead of implying that one local bridge can drive every player.

Wrong-player execution is a normal structured legality failure. For example, a local-only bridge asked to execute a remote-owned control should fail like this rather than falling back to raw input:

```bash
sts2 --json act select-card --player-id p:remote-2 --card card-selection:card:bash:0
```

Expected callers should inspect fields equivalent to:

```json
{
  "actionFailure": {
    "reasonCode": "wrong-player",
    "requestedPlayerId": "p:remote-2",
    "ownerPlayerId": "p:local-1",
    "localPlayerId": "p:local-1",
    "role": "local-only",
    "screen": "card-selection",
    "action": "select-card"
  }
}
```

Scenario restore keeps the same honesty boundary. `sts2 dev scenario load --allow-degraded-local-multiplayer` can load a local-only degraded approximation for review and assertions, but the resulting `multiplayerRestore` / notices must still describe remote players as degraded, omitted, host-owned, remote-owned, or unsupported rather than making them actionable clients.

## Multiplayer Lobby State

`state --json` now exposes richer multiplayer lobby data when the live hooks provide it:

- `lobby.players[]` as the canonical ordered list
- `lobby.localPlayerId`
- `lobby.hostPlayerId` when directly derivable
- `lobby.localPlayerRole`
- per-player `isLocal`, `isHost`, and `isRemote` flags on `lobby.players[]`
- `lobby.availableCharacters[]`
- `choices[]` on `Screens.CharacterSelect.NCharacterSelectScreen` / `Screens.CharacterSelect.NMultiplayerLoadGameScreen`, including `lobby:ready`, `lobby:unready`, and visible `lobby:character:<id>` rows owned by the local lobby player when the live screen exposes real character buttons
- `state full` keeps `lobby.playersById` and `lobby.availableCharactersById` as full bridge-native compatibility indexes for scripts that need stable object paths
- `run.seed`, `run.floor`, `run.act`, and `run.playersById` on load-run lobbies when the loaded save model is available
- additive `isLocal`, `isHost`, and `isRemote` flags on `run.players[]`, `run.playersById`, `combat.players[]`, and `combat.playersById`
- additive `ownerPlayerId` on combat target `choices[]` as well as other player-scoped visible choice families

Example query paths:

```bash
sts2 --json dev assert characterSelect.lobby.players[id=p:100].isReady --equals true
sts2 --json dev assert characterSelect.characterButtons[characterId=silent].isLocked --equals false
sts2 --json dev assert run.players[id=p:100].characterId --equals ironclad
sts2 --json dev assert run.players[id=p1].isLocal --equals true
sts2 --json dev assert actions[kind=play-card].ownerPlayerId --source actions --equals p1
sts2 --json dev wait-for characterSelect.lobby.localPlayerId --exists
```

The bridge keeps partial support explicit:

- player names remain `null` when the current hook does not expose them
- `hostPlayerId` is omitted when it cannot be derived directly
- load-run lobbies now expose visible `availableCharacters` when their character buttons can be discovered, preserve the loaded save summary through `run`, and advertise executable `select-character` actions when a callable character-button press path is present
- fallback character lists derived from static model data are marked through notices rather than pretending they came from a live button list
- local perspective can filter remote-owned presentation/control details while preserving remote identity and HP summaries; omniscient remains a dev/validation perspective
- remote-owned actions require a configured client or host-mediated capability and otherwise fail with structured `wrong-player` or `unsupported-perspective`

## Dev Automation

Recent logs:

```bash
sts2 dev logs
sts2 dev logs --limit 20 --level warn --target bridge.action
sts2 dev logs --after-cursor 120
sts2 dev logs --tail 20
sts2 dev logs --follow --tail 20
sts2 --json dev logs --limit 50 --target bridge.state
```

Console commands:

```bash
sts2 --json dev console help
sts2 --json dev console draw 3
sts2 --json dev console help draw
sts2 --json dev console die
sts2 --mode dangerous --json dev console achievement ...
sts2 --mode dangerous --json dev console cloud ...
```

`dev console` accepts the first token after `console` as `command` and all remaining tokens as `args`; the JSON payload includes the canonical `line` joined with single spaces. Runtime console rejection exits nonzero with `console_command_rejected` while preserving the console output payload.

Direct creature heal (devtool):

```bash
sts2 --json dev heal --amount 10
sts2 --json dev heal --target creature:2 --amount 5
sts2 --json dev heal --full
```

`dev heal` instantly heals any creature host-side through the game's own heal command — a live-rescue devtool, NOT a legal player action (it never appears in `availableActions`, and `sts2 act` is unaffected). `--target` takes a `creature:<combatId>` id exactly as `sts2 state` shows for players, allies, and enemies; omitting it targets the acting seat's own player creature. `--amount` clamps to max HP; `--full` sets current HP to max. Healing a dead creature revives it (the game's heal semantics). Unlike the game's networked `heal` console command (allies only, queued to the next play phase), `dev heal` applies immediately even during the enemy turn. Multiplayer caveat: host-direct mutation can desync real remote network clients; couch co-op's single-real-player setup is fine.

Wait for state:

```bash
sts2 dev wait-for run.currentRoom.scene --equals rooms/combat_room
sts2 dev wait-for run.players[id=p1].combat.energy --gte 3 --timeout-ms 5000 --interval-ms 100 --rpc-timeout-ms 1000
```

Assert current state:

```bash
sts2 dev assert run.currentRoom.scene --equals rooms/combat_room
sts2 dev assert run.players[id=p1].combat.energy --gte 1
sts2 dev assert run.currentRoom.combat.combatState.enemies[*].id --equals e_1
sts2 dev assert run.players[id=p1].combat.hand.cards[id=c_1].id --equals c_1
sts2 dev assert actions[label*=Turn].id --source actions --equals action:combat-room:end-turn
sts2 dev assert actions[id~=^action:combat-room:].kind --source actions --exists
sts2 dev assert actions[args.eventOptionId=event-room:gain-gold:0].kind --source actions --equals select-event-option
sts2 dev assert actions[=action:combat-room:end-turn].kind --source actions --equals end-turn
sts2 dev assert run.currentRoom.combat.combatState.enemies[0].modelId --contains WORM
sts2 dev assert rootScene --regex '^run|screens/main_menu$'
```

The query shape is intentionally small:

- dotted object paths such as `run.currentRoom.scene`
- numeric indexes such as `run.currentRoom.combat.combatState.enemies[0].currentHp`
- array wildcards such as `run.currentRoom.combat.combatState.enemies[*].id`
- array filter selectors such as `run.players[id=p1].combat.hand.cards[id=c_1].id`, `actions[label*=Turn].id`, `actions[id~=^action:combat-room:].kind`, `actions[args.eventOptionId=event-room:gain-gold:0].kind`, or `actions[=action:combat-room:end-turn].kind`
- stable by-id array filters such as `characterSelect.lobby.players[id=p:100].isReady`
- selector operators are limited to `=`, `!=`, `*=`, `~=`, `>`, `>=`, `<`, and `<=`
- `--source actions` (or YAML `source: actions`) queries the resolved `spirectl.state-actions/v0` document — the same document `sts2 state actions` returns — instead of the plain state snapshot; this is where `actions[]` items with `id`/`kind`/`label`/`args`/`ownerPlayerId`/`sourcePath` live

Wildcard and array-filter paths collect every matching leaf value into the `actual` array. Predicates succeed when any collected value matches, and `--exists` / `--not-exists` treat an empty collected result set as absent.

Supported predicates are:

- `--equals`
- `--contains`
- `--regex`
- `--gt`
- `--gte`
- `--lt`
- `--lte`
- `--exists`
- `--not-exists`

`state` applies a default bounded live bridge RPC timeout and accepts `--rpc-timeout-ms` for one-shot overrides. `dev wait-for` returns a structured timeout error with the last observed value, and each poll's underlying `state` RPC is bounded by `--rpc-timeout-ms` and the remaining wait budget. YAML `dev.wait-for` steps accept the same value as `rpcTimeoutMs`. `dev assert` returns a structured failure payload when the predicate does not match. Missing-path failures now also include the resolved prefix, missing segment or index, and nearby keys or array bounds when available. These commands keep reliable nonzero exit codes for automation.

## External Runner Contract

The current Playwright/Node foundation builds directly on the existing CLI contract rather than adding a second protocol.

For the wrapper-supported command set:

- `sts2 --json game info`
- `sts2 --json state`
- `sts2 --json dev logs ...`
- `sts2 --json dev assert ...`
- `sts2 --json dev wait-for ...`
- `sts2 --json act ...`
- `sts2 --json test run ...`

the contract is:

- successful command results are structured JSON on `stdout` with exit code `0`
- bridge/runtime/assert/wait failures remain structured JSON on `stdout` with nonzero exit codes
- `dev assert` false conditions and `dev wait-for` timeouts are expected automation failures, not special human-only output cases
- process-level failures before the CLI contract starts, such as missing binaries or shell launch problems, may still surface on `stderr`

The Node helper under `npm-wrapper/` therefore:

- always appends `--json`
- parses `stdout` only
- preserves raw `stdout` and `stderr`
- throws a rich `Sts2CliError` on nonzero exit instead of translating CLI failures into a new JS-specific schema
- exposes `testRun({ path | inline | scenario })`, serializing JS `scenario` objects back through `sts2 --json test run --inline ...`

The repo-local MCP adapter extends that same contract rather than replacing it.

- `sts2-mcp` loads `sts2 --json inspect ai-tools` once at startup.
- each MCP tool maps back to the corresponding CLI command(s) listed in `mapsTo`
- successful tool calls return the original CLI JSON payload unchanged as `structuredContent`
- expected CLI failures return `isError: true` with `{ exitCode, argv, stderr, payload }`
- adapter-only failures use small synthetic codes such as `cli_spawn_failed` and `cli_protocol_error`
- local stdio MCP is the default safe adapter path; Streamable HTTP network MCP is opt-in service posture with explicit bind/auth/acknowledgement requirements and operator-managed TLS when it leaves loopback

Recommended agent workflow:

1. call `game_info`
2. call `state` and `inspect_actions`
3. call `act` only after reading current choices and `availableActions`
4. use `logs`, `wait_for`, and `assert` for script-friendly control flow
5. use `code_locate` before deeper static inspection tools
6. use `test_run` with a checked-in `.sts2.yaml` or `.sts2.json` scenario path for reusable workflows, `--inline` YAML/JSON for one-off generated runs, or the npm wrapper's `testRun({ scenario })` helper when you are already in Node

## Test Runner

Run one `.sts2.yaml` or `.sts2.json` scenario file, a directory of scenarios, or one inline YAML/JSON body:

```bash
sts2 --config tests/sts2.mock.yaml test run tests/scenarios/smoke-main-menu.sts2.yaml
sts2 --config tests/sts2.mock.yaml --json test run tests/scenarios/smoke-main-menu.sts2.yaml
sts2 --json test run tests/scenarios/smoke-main-menu-live.sts2.yaml
sts2 --json test run ./tmp/smoke-main-menu.sts2.json
sts2 --json test run --inline '{name: smoke-inline, steps: [game.info]}'
sts2 --config tests/sts2.mock.yaml --json test run --artifacts-dir ./tmp/sts2-artifacts tests/scenarios/smoke-main-menu.sts2.yaml
sts2 --config tests/sts2.mock.yaml --json test run --failure-artifacts never tests/scenarios/smoke-main-menu.sts2.yaml
```

The checked-in smoke scenarios intentionally split mock versus live coverage:

- `tests/scenarios/smoke-main-menu.sts2.yaml` stays on explicit mock main-menu transport via `--config tests/sts2.mock.yaml` and remains the lightweight baseline example.
- `tests/scenarios/smoke-main-menu-live.sts2.yaml` is the explicit live validation path; it loads the authored main-menu fixture against the live bridge, waits for the main menu (`rootScene`), drives `menu:start-run` through the `choose` fallback-compatibility action (main-menu's "start run" has no native `state actions` surfacer), then validates the first downstream stable screen.

M7 supports this practical step subset:

- `act.play-card`
- `act.use-potion`
- `act.claim-reward`
- `act.skip-rewards`
- `act.select-card`
- `act.skip-card-selection`
- `act.select-bundle`
- `act.buy-card`
- `act.buy-relic`
- `act.buy-potion`
- `act.remove-card`
- `act.leave-shop`
- `act.close-shop-inventory`
- `act.rest`
- `act.smith`
- `act.use-rest-site-option`
- `act.proceed-rest-site`
- `act.open-chest`
- `act.take-relic`
- `act.proceed-treasure-room`
- `act.back-from-map`
- `act.select-event-option`
- `act.open-event-shop`
- `act.use-crystal-sphere-control`
- `act.proceed-event`
- `game.info`
- `dev.logs`
- `dev.assert`
- `dev.wait-for`
- `dev.screenshot`
- `act.choose`
- `act.select-map-node`
- `act.end-turn`
- `act.ready`
- `act.unready`
- `act.select-character`

Canonical YAML step IDs should match CLI namespaces:

```yaml
name: smoke-main-menu
description: Lightweight baseline smoke scenario for explicit mock main-menu transport rather than a live bridge session.
steps:
  - game.info
  - dev.assert:
      path: rootScene
      equals: screens/main_menu
  - act.select-event-option:
      eventOption: event-room:gain-gold:0
  - act.select-map-node:
      node: map-node:3:1
  - act.back-from-map
  - act.select-character:
      character: silent
  - act.ready
```

Compatibility aliases are intentionally small:

- underscore and hyphen variants normalize to the same canonical ID
- `assert.query` is accepted as an alias for `dev.assert`

Current limitations are explicit:

- `game.deploy` now runs through the same in-process lifecycle helper as the direct CLI command, resolving `path` relative to the scenario file for file-backed scenarios and relative to `cwd` for inline/object scenarios; `test.profiles.<name>.preflight.deploy` runs the same deploy helper once before scenario execution and resolves `path` relative to the config directory
- `dev.load-fixture` and `dev.fixture.load` runner steps execute the shipped authored fixture subset through the canonical fixture-load helper; artifacts continue to use canonical step id `dev.load-fixture` and include the same `recipeReport` / `bridgeValidation` JSON as the direct command
- `dev.load-scenario` loads a `spirectl.scenario/v0` sparse artifact before later workflow steps, resolving `path` relative to the scenario file for file-backed workflows and relative to `cwd` for inline/object workflows
- `dev.console` executes a structured in-game console command using normal mode by default; it accepts only `command` and optional `args`, writes successful output to `steps/NNN-dev-console.json`, and requires dangerous mode for `achievement`, `cloud`, or `unlock`
- remote `test run` is now also available through `sts2 service serve`, but the service still reuses the same runner semantics, artifacts, and helper path rather than inventing a second runner contract
- the old `state: { expect: ... }` mini-DSL is rejected; use `dev.assert` or `dev.wait-for`

Directory runs recurse through `*.sts2.yaml` and `*.sts2.json` files, sort them by relative path, continue after scenario failures, and stop each individual scenario at its first failing step. Inline runs behave like a single discovered scenario with `path: "<inline>"` in the structured report.

`test run` now always creates a persisted run directory under `<artifacts-dir>/test-runs/run-<unix-ms>/`. The artifact root defaults to `config.artifacts.dir`, and `--artifacts-dir` overrides it for a single invocation.

Artifact persistence is intentionally small and inspectable:

- `summary.json`: the full run report
- `scenarios/<NNN>-<slug>/result.json`: the full per-scenario report for every passed, failed, or invalid scenario
- `scenarios/<NNN>-<slug>/steps/<NNN>-game-deploy.json`: persisted output for successful runner deploy steps
- `scenarios/<NNN>-<slug>/steps/<NNN>-dev-console.json`: persisted output for successful console steps
- `scenarios/<NNN>-<slug>/steps/<NNN>-dev-load-fixture.json`: persisted output for successful fixture-load steps, including `recipeReport.recipeName`, field reports, degraded multiplayer reports, and bridge validation details
- `scenarios/<NNN>-<slug>/steps/<NNN>-dev-load-scenario.json`: persisted output for successful scenario-load steps
- `scenarios/<NNN>-<slug>/steps/<NNN>-dev-log-health.json`: persisted output for successful log-health steps
- `scenarios/<NNN>-<slug>/steps/<NNN>-dev-diagnostics.json`: persisted output for successful diagnostics steps
- `scenarios/<NNN>-<slug>/steps/<NNN>-dev-screenshot.png`: default output path for successful screenshot steps that do not set an explicit `output`
- `scenarios/<NNN>-<slug>/failure/summary.json`: failing step metadata when a scenario fails and failure artifacts are enabled
- `scenarios/<NNN>-<slug>/failure/evidence/diagnostics.json`: stable failure-time diagnostics payload
- `scenarios/<NNN>-<slug>/failure/evidence/game-info.json`: best-effort game/transport snapshot when diagnostics captured it
- `scenarios/<NNN>-<slug>/failure/evidence/state.json`: best-effort state capture when diagnostics captured it
- `scenarios/<NNN>-<slug>/failure/evidence/logs.json`: best-effort recent log capture when diagnostics captured it
- `scenarios/<NNN>-<slug>/failure/evidence/inspect-actions.json`: best-effort action-availability capture when diagnostics captured it
- `scenarios/<NNN>-<slug>/failure/evidence/scene-tree.json`: best-effort runtime scene tree when diagnostics captured it
- `scenarios/<NNN>-<slug>/failure/evidence/runtime.png`: best-effort screenshot when diagnostics captured it

Failure artifact capture defaults to `--failure-artifacts on-failure`. Use `--failure-artifacts never` when you only want the persisted `summary.json` and per-scenario `result.json` files.

### Runner Output Contract

`test run` always returns a scenario summary envelope once the CLI command itself starts:

- `status`: `passed`, `failed`, or `invalid`
- scenario counts, step counts, elapsed time, and `exitCode`
- top-level `artifacts` with `rootDir`, `summaryPath`, `failureArtifacts`, and best-effort `errors[]`
- `scenarios[]` with `path`, `name`, `status`, `elapsedMs`, executed `steps[]`, scenario `artifacts` including step artifact metadata, and optional `failure`
- `steps[]` with `index`, `requestedId`, `canonicalId`, normalized `input`, and either `output` or structured `error`

Exit code policy is:

- `0` when every scenario passes
- `2` for discovery, parse, or validation failures before execution
- otherwise the highest failing step exit code, preserving the existing `3` / `4` / `5` CLI meanings

Artifact collection is best-effort and reporting-only:

- a scenario failure still reports its original failing step and exit code even if artifact capture partially fails
- scenario-level `artifacts.errors[]` records collection or write problems such as failed log capture
- human-readable output now includes the failing step, `result.json` path, and persisted run `summary.json` path

## Lifecycle And Dev Commands

```bash
sts2 game install-bridge
sts2 game install-bridge --no-build --force
sts2 game install-bridge --restart --wait-quiescent-ms 8000
sts2 game launch
sts2 game launch --no-detach-session
sts2 game launch --wait-quiescent-ms 8000
sts2 game launch -- --headless -fastmp host_standard
sts2 --json game mods settings
sts2 --json game mods settings --settings-file ~/.local/share/SlayTheSpire2/steam/123456789/settings.save
sts2 --json game mods active
sts2 game attach
sts2 game attach --timeout-ms 5000 --rpc-timeout-ms 1000
sts2 game bridge-health
sts2 --json game bridge-health --verbose
sts2 --json game bridge-health --check-source
sts2 game bridge-health --repair-stale-endpoint
sts2 game close
sts2 game close --timeout-ms 30000 --interval-ms 250
sts2 game kill
sts2 game deploy ./mods/MyMod --build --restart --verify
sts2 game deploy ./mods/MyMod --build --allow-stale-build
sts2 game deploy ./mods/MyMod --build --restart --wait-quiescent-ms 8000 -- --prerender-spines
sts2 --json --progress game deploy ./mods/MyMod --build --restart --verify
sts2 --json dev logs --source game-stdio
sts2 --json dev visual-preflight --encounter kaiser_crab_boss --print-only
sts2 --json dev visual-preflight --repair-stale-endpoint --attach
sts2 --json dev visual-preflight --launch --timeout-ms 30000 --interval-ms 250
sts2 dev fixture load --path fixtures/basic-map.sts2.fixture.yaml
sts2 dev fixture load --path fixtures/basic-load-run-lobby.sts2.fixture.yaml
sts2 dev fixture load --path fixtures/basic-event-room-options.sts2.fixture.yaml
sts2 dev fixture load --path fixtures/basic-shop-inventory.sts2.fixture.yaml
sts2 dev fixture load --path fixtures/basic-card-selection.sts2.fixture.yaml
sts2 dev fixture load --path fixtures/basic-card-overlay.sts2.fixture.yaml
sts2 dev fixture load --path fixtures/basic-passive-card-overlay.sts2.fixture.yaml
sts2 dev fixture load --path fixtures/multiplayer-ownership-local-only-degraded.sts2.fixture.yaml
sts2 dev fixture load --path fixtures/basic-rewards.sts2.fixture.yaml
sts2 dev fixture load --path fixtures/rest-site.sts2.fixture.yaml
sts2 dev screenshot --rpc-timeout-ms 5000 --output ./.sts2/artifacts/combat.png
```

`game install-bridge`, `game launch`, `game attach`, `game bridge-health`, `game mods settings`, `game mods active`, `dev visual-preflight`, `game close`, `game kill`, and `game deploy` are real CLI workflows. `game bridge-health` is read-only by default: it reports compact endpoint, connection, bridge version, duplicate-mod status, structured status codes such as `endpoint_missing`, `endpoint_refused_or_stale`, `rpc_timeout`, `deployed_version_mismatch`, `live_version_mismatch`, `game_version_mismatch`, and `reachable_current`, plus safe next commands. Use `--verbose` for the full diagnostic payload, and keep `--non-mutating` only as a compatibility no-op. Source freshness is a cheap sentinel stat by default (`bridge.sourceScanMode: "sentinel"`), because the command is polled: it stats `bridge-mod/Directory.Build.props`, `bridge-mod/src/Spirectl.BridgeMod/BridgeRuntime.cs` and `proto/spirectl/v0/runtime.proto` instead of walking every bridge and proto source file. `--check-source` (implied by `--verbose`) does the full walk and is the authoritative answer for `stale_live_host`: an edit to a bridge source file outside the sentinels is only seen in that mode. The repository root is found by walking up from the working directory and then from the binary's own directory, so a `sts2` built in one checkout reports the freshness of the checkout you are standing in. Add `--repair-stale-endpoint` only when you explicitly want stale local endpoint cleanup; no cleanup runs in default mode. The remaining lifecycle, mod, fixture, screenshot, and diagnostics commands keep the same structured output contracts and normal-mode behavior unless their command help explicitly requires dangerous mode. `dev screenshot` and live `dev screenshot-diff` accept `--rpc-timeout-ms <ms>` and return the existing structured `bridge_rpc_timeout` error if live screenshot capture exceeds that bound.

### Game Build Binding

The bridge binds game members that were renamed or reshaped between Slay the Spire 2 builds, so a bridge
payload is valid for one build and not another. A payload in the wrong install *starts*: it logs a few soft
`not found` lines and then throws `MissingMethodException` the first time it walks a lobby. Steam updating the
game under a correctly-installed bridge is exactly that, so the identity is bound at both ends.

`bridge-mod/Sts2GameApi.props` is the one table mapping an install's `release_info.json` `version` to an
**STS2 API lane** (`v107`, `v111`). MSBuild reads it to choose which `src/Spirectl.Sts2/GameApi/<lane>/`
sources compile, the CouchCoop repo imports it, and `cli/build.rs` compiles it into the CLI —
`scripts/sts2-api-lanes.sh` prints the lane list for shell callers. There is no second copy to keep in sync.

`game install-bridge` stamps what it built into `spirectlbridge.json`'s `buildIdentity`:

- `sts2ApiLane` — the lane that compiled.
- `builtAgainstGame` — `{identitySource, version, mainAssemblyHash, referencePackageVersion}`.

Those two claims are not the same strength, and the difference is deliberate. A **source** build compiles
against a real install, so it names that install's `version` and `main_assembly_hash`
(`identitySource: "install-release-info"`). A **released** payload compiles against the locked,
declaration-only STS2 reference SDK, which has no `release_info.json` and no game hash at all: it can claim
its lane and the reference package version it was built from, and nothing else
(`identitySource: "reference-sdk"`, with `version` and `mainAssemblyHash` null). No hash is invented for it,
so `null` there means "this payload cannot claim one" — never "matches anything". An install whose
`release_info.json` is unreadable yields `identitySource: "unknown"` and claims nothing.

The same three fields ride the handshake (`liveBridge.buildIdentity.sts2ApiLane`,
`.builtAgainstGameVersion`, `.builtAgainstMainAssemblyHash`), so the *loaded* bridge can be checked as well as
the deployed one. `game bridge-health` compares every claim both sides actually make against the install's own
`release_info.json` and reports the result under `compatibility.gameBuild` — `status` of `match`, `mismatch`
or `unknown`, the install identity, both bridge claims, and a per-field `mismatches` list of
`{source, field, expected, found}`. The compact output carries `bridge.gameBuildStatus` always and the full
comparison only when it mismatched. A mismatch is `game_version_mismatch` at exit 4; it outranks
`stale_live_host` and `deployed_version_mismatch`, because those two mean "rebuild for newer bridge source"
while this one means the bridge is bound to a build that is not on disk.

Released payloads are per lane end to end. `scripts/package-bridge-release.sh --lane <lane>` writes
`spirectlbridge-v<version>-<lane>.zip` plus its sidecar manifest,
`scripts/verify-bridge-release.sh --lane <lane>` checks the name and the manifest's own lane claim agree, and
`install-bridge`'s cache is `<artifacts>/live-bridge/release-cache/<version>/<lane>/`. The lane is resolved
from the install before a payload is selected, so `--no-build` installs the payload matching this install or
refuses, and a best-effort payload is never installed:

- `bridge_release_lane_unresolved` — the install's build cannot be identified, or its version has no lane.
- `bridge_release_lane_unreleasable` — the lane is supported but no release covers it (see below).
- `bridge_release_cache_missing` — naming the lane and the expected archive, when no payload for it is cached.
- `bridge_release_lane_mismatch` — the archive's own manifest claims a different lane than its name.

Not every supported lane is a *releasable* one, and `Sts2GameApiReleasableLanes` in the props file says which
are. A release compiles against the pinned reference SDK, which declares one game build; a lane whose sources
need a type that package does not declare cannot be packaged at all. Such a lane is fully supported from a
source checkout — `game install-bridge` detects it from the install and builds against the real assemblies —
it just has no published archive. `scripts/sts2-api-lanes.sh --releasable` prints the subset, and packaging,
the release manifest and the `--no-build` refusal all read it.

### Deploy Build Output Contract

`game deploy --build` tells the build command exactly where the deploy will read
from, then checks that the build wrote there:

- The build runs with `SPIRECTL_DEPLOY_OUTPUT_DIR` (absolute), `SPIRECTL_DEPLOY_PROJECT_ROOT`, and `SPIRECTL_DEPLOY_MOD_NAME` in its environment. A literal `{deployOutputDir}` token in any `game.deployBuildCommand` element is substituted with the same absolute path, so a build that takes its output directory as an argument needs only one spelling of it.
- Afterwards that directory is scanned. Missing is `deploy_build_output_missing` (exit 4); a newest file older than the build start (2 s slack) is `deploy_build_output_stale` (exit 4). Both payloads carry `expectedOutputDir`, `projectRoot`, and the resolved `buildCommand`. Without the check, a build that wrote somewhere else silently redeployed stale content and still reported success.
- `--allow-stale-build` downgrades the stale case to `build.freshness.stale: true` for genuinely no-op incremental builds.
- `build.freshness` reports `{outputDir, newestFile, newestFileUnixMs, fileCount, buildStartedUnixMs, fresh}`.

Trailing `-- <args>` on `game deploy` are forwarded to the game executable by the
`--restart` launch, the same way `game launch -- <args>` works.

### Quiescence Waits

`game launch`, `game deploy`, and `game install-bridge` accept
`--wait-quiescent-ms <ms>` (0 = off), `--quiescent-stable-samples <n>` (default
3), and `--require-quiescent`. A reachable bridge only means the mod is up; the
boot flow keeps pushing screens and overlays for seconds afterwards, so a caller
that immediately drives the UI otherwise has to sleep blindly. The wait reuses
the `dev wait-for-transitions` implementation and is non-fatal by default: on
timeout the command still exits 0 and reports `quiescence.{requested, quiescent:
false, timedOut, reason, budgetMs, requiredStableSamples, elapsedMs, status}`.
`--require-quiescent` turns that into `quiescence_timeout` (exit 4). The
`quiescence` key is omitted entirely when no budget is requested.
`scripts/build-fixture.sh` passes `--wait-quiescent-ms ${BUILD_FIXTURE_QUIESCENT_MS:-8000}`.

### Captured Game Output

`game launch` always captures the launched game's stdout/stderr (the engine
writes its startup and crash diagnostics there, and they used to be discarded
unless a launch wrapper was configured). Files are written per run as
`game-launch-<unixms>-<pid>.{stdout,stderr}.log` under
`<instances.dir>/<name>/logs` when an instance is active and
`<artifacts.dir>/game-launch` otherwise, with `game.stdout.log` /
`game.stderr.log` symlinks pointing at the newest run and older runs pruned
beyond ten per stream. Paths are reported as `launch.stdio` (with tails on the
launch error payloads) and as `launchStdio` on `game info`; `dev logs --source
<bridge|game-stdio|both>` (default `bridge`) reads them. `launch.wrapperLogs`
keeps its old meaning and stays null when no wrapper is configured.

### install-bridge Arguments

All optional; bare `game install-bridge` builds from a source checkout, or uses
the version-matched release bridge payload when invoked from a distributed CLI.
`--no-build` never invokes .NET: it uses a verified cached release payload, or
an existing source publish when a checkout is available. The staged
payload is hashed (sha256 over every staged file except the manifest) and stored
as `buildIdentity.contentHash`; when it equals the installed manifest's hash the
copy is skipped and the result reports `installed: false, reason:
"content_unchanged"`. `--force` copies anyway. The hash short-circuits the copy
only, never the build.

In practice the skip fires for repeated `--no-build` installs, not for repeated
builds: `bridge-mod/Directory.Build.props` stamps `SpirectlBridgeBuiltAtUtc` with
the wall clock and bakes it into every assembly, so two builds of identical
sources produce different bytes and therefore a different hash. See
`docs/known-gaps.md`. `--restart`, `--timeout-ms`, `--interval-ms`,
`--rpc-timeout-ms`, and the quiescence flags are also accepted.

The canonical live bridge path is now:

```bash
sts2 game install-bridge
sts2 game deploy ./mods/MyMod --build --restart --verify
sts2 game launch
sts2 game attach
```

For live encounter validation preflight (Kaiser representative flow):

```bash
sts2 game bridge-health
sts2 --json dev visual-preflight --encounter kaiser_crab_boss --print-only
scripts/validate.sh m78-live-encounter-artifacts --encounter kaiser_crab_boss --json
```

## Static Inspection

Static inspection now has two complementary lanes:

- managed code navigation for types, methods, references, inheritance, and metadata-first decompile output with explicit ILSpy escalation
- static Godot scene/resource navigation for supported text, binary, and packed assets, node hierarchies, resource links, and attached script metadata

For managed code commands, the CLI resolves `toolchain.sharedCacheDir` from the active config and passes it to the .NET helper automatically. The helper may reuse metadata catalog files from that shared cache root, with declarations-only entries kept separate from reference-backed entries. Cache reuse is transparent: missing, stale, corrupt, incompatible, or unreadable entries are ignored and rebuilt from the selected assemblies/mod roots, and command JSON payloads do not gain cache diagnostics or otherwise change shape because a cache was hit or missed. Cache files are generated artifacts that can be ignored or deleted safely; command correctness depends on the configured search roots, not cache availability.

Managed code inspection selects game/product assemblies by default and skips common framework or third-party dependency DLLs such as `System.*`, `Microsoft.*`, `GodotSharp.dll`, `0Harmony.dll`, `MonoMod.*`, `SmartFormat*`, `Sentry.dll`, and `Steamworks.NET.dll`. This keeps normal `locate`, `describe`, `refs`, `derived`, `hook-*`, and metadata `decompile` workflows focused on STS2 and mod code. Pass `--include-dependencies` only when you explicitly need to inspect those dependency DLLs. `--include-dependencies` does not imply `--include-mods`; use both flags when you need mod assemblies and broad dependency visibility.

The current static scene/resource coverage is intentionally explicit:

- parses text `.tscn`, `.escn`, and `.tres`
- parses supported standalone binary `.scn` / `.res`
- reads supported unencrypted `.pck` entries in-place and returns `containerPath` instead of unpacking to disk
- keeps static `code scene-*` separate from live `dev scene-*`
- keeps best-effort notes in the JSON payload instead of pretending completeness

Example commands:

```bash
# managed code
sts2 code locate type CombatScreen
sts2 code locate method DrawCard --include-mods
sts2 code locate type HarmonyLib.Patch --include-dependencies
sts2 code describe type MegaCrit.Sts2.CardState
sts2 code refs type MegaCrit.Sts2.CardState
sts2 code refs symbol CanPlay --include-mods
sts2 code derived type MegaCrit.Sts2.BaseCard
sts2 code hooks OnDeckChanged --assemblies-dir ./assemblies --resources-dir ./game-project
sts2 code hook-info "MegaCrit.Sts2.ScreenHooks::OnDeckChanged(MegaCrit.Sts2.DeckState)" --assemblies-dir ./assemblies
sts2 code describe method "MegaCrit.Sts2.CardState::CanPlay()"
sts2 code decompile type MegaCrit.Sts2.CardState
sts2 code decompile method "MegaCrit.Sts2.CardState::CanPlay()" --full

# does a built mod still bind to a game build?
sts2 code verify-references ./mods/example/Example.dll --assemblies-dir ./assemblies
sts2 code verify-references ./mods/example/Example.dll,./mods/example/Example.Bridge.dll --assemblies-dir ./beta-assemblies --control-assemblies-dir ./assemblies

# static scenes and resources
sts2 code scene-search HandPanel --resources-dir ./game-project
sts2 code scene-search StatusConfig --resources-dir ./game-project --assemblies-dir ./assemblies
sts2 code scene-tree res://ui/CombatScreen.tscn --resources-dir ./game-project
sts2 code scene-node res://ui/shared/HandPanel.tscn /HandPanel/ConfirmButton --resources-dir ./game-project
```

`code locate` supports `type`, `method`, and `symbol`.

- `type`: search type names, full names, and namespaces
- `method`: search method names, declaring types, and signatures
- `symbol`: search across both types and methods

`code describe` and `code decompile` support `type` and `method` only, and both require an exact symbol resolution. Stable IDs such as `method:<assembly>:...` and `type:<assembly>:...` let `code describe` target the named assembly when it is present in the selected roots. Short exact method names first use a compact metadata pass to find candidate declaring assemblies; when the match is unique, the helper fully loads only that declaring assembly. `code decompile` stays metadata-derived by default; pass `--full` only when you want the embedded ILSpy backend for an exact match.

`code refs` supports:

- `type`: exact type IDs or exact full type names
- `method`: exact method IDs or exact declaring-type signatures
- `symbol`: name-based fallback across indexed references when exact semantic resolution is not available

`code derived` currently supports `type` only and returns direct `extends`, `implements`, and `interface-inherits` relationships.

`code hooks` is the paginated discovery-oriented catalog for likely managed hook targets and script callbacks. It returns stable method ids, advisory `hookForms`, reasons, reference counts, facets, and optional `scriptPath` / `scenePaths` hints when assemblies and resources expose enough static metadata. Omit the query to browse the installed catalog, and use `--limit`, `--offset`, `--source`, `--assembly`, `--form`, `--has-script`, and `--sort` to narrow large result sets.

`code hook-info` resolves one exact managed method id or exact lookup signature into a hook-ready projection with ordered parameters, override/interface links, advisory hook forms, and suggested next commands.

`code verify-references` answers the reverse question from the rest of `code`: instead of inspecting one game symbol you already suspect, it takes built consumer assemblies and asks which of the game types and members *they* bind to are missing or reshaped in a given assemblies directory. It reads the consumers' own `TypeRef`/`MemberRef` tables, keeps the bindings whose resolution scope is `sts2`, `GodotSharp`, or `0Harmony`, and resolves each one against `--assemblies-dir`. Two properties are the reason it exists: it works on a shipped binary with no source and no matching reference package, and it does not stop at the first broken project the way a cross-version build does.

Pass `--control-assemblies-dir` with the game build the consumers were compiled against. That turns on the fourth and most valuable bucket — members that resolve on *both* sides with a different rendered signature, which is how an added parameter or a dropped return value shows up — and it makes the whole run trustworthy: if the control cannot resolve a binding either, the finding is about the probe rather than about the game, so the run reports `status: "control-dirty"` and fails without letting you read the candidate buckets as a verdict. Without a control, only unresolved types and members are reported.

Attribution is per consumer assembly, not per call site. Once a break is named, `code refs` and `code describe` are the commands that take it to a line.

`code scene-search` returns `scene`, `node`, and `resource` matches from static Godot text, binary, and packed assets.

`code scene-tree` resolves one exact scene by:

- exact scene ID such as `scene:game:res://ui/CombatScreen.tscn`
- exact `res://...` path
- exact relative path
- exact scene UID when present

`code scene-node` resolves one exact node inside an exact scene. Node paths are normalized, root-inclusive paths such as `/CombatScreen/HandPanel` or `/HandPanel/ConfirmButton`.

## Asset Export

```bash
sts2 assets extract hand --resources-dir ./game-project
sts2 --json assets extract packed_icon --execution offline --resources-dir ./game-project
sts2 --json assets extract anim --execution live --format png --resources-dir ./game-project --game-path ~/games/sts2
sts2 assets extract model://characters/ironclad/visuals --execution live
sts2 --json assets extract model://characters/ironclad/visuals --execution live --format png
sts2 --json assets extract res://scenes/vfx/block_spark_vfx.tscn --execution live --format png
sts2 --json assets extract res://scenes/backgrounds/overgrowth/overgrowth_background.tscn --execution live
sts2 --json assets extract composed://combat-background/overgrowth/image --execution live --format png
sts2 --json assets explain composed://combat-background/overgrowth/image --execution live
sts2 --json assets explain composed://encounters/kaiser_crab_boss/scene-package --execution live
sts2 --json assets explain composed://encounters/knowledge_demon_boss/scene-package --execution live
sts2 --json assets extract composed://encounters/kaiser_crab_boss/background/image --execution live --format png
sts2 --json assets extract composed://encounters/kaiser_crab_boss/visual-state/rocket-charge-up/overlay/image --execution live --format png
sts2 --json assets extract composed://encounters/kaiser_crab_boss/visual-part/rocket/state/rocket-charge-up/image --execution live --format png
sts2 --json assets extract composed://encounters/knowledge_demon_boss/visual-part/burn-fire/state/heavy-attack-burnt/image --execution live --format png
sts2 --json assets extract-batch --manifest ./asset-manifest.json --execution auto
sts2 --json dev visual-preflight --encounter kaiser_crab_boss --print-only
scripts/validate.sh m78-live-encounter-artifacts --encounter kaiser_crab_boss --json
```

`assets extract` stays narrower than `code scene-*`, but it now supports both offline raw export and live preview output. Extraction has two separate goals: source-byte export for readable files and Godot-rendered preview export for resources that need runtime interpretation.

- it searches the resolved resource root and optional mod roots using the same config and `--include-mods` precedence rules as static scene/resource commands
- it exports artifacts to `<artifacts-dir>/assets/<source-root>/...`, mirroring the source-relative path inside each resolved root so resource and mod matches do not overwrite each other
- `--execution offline` uses only configured resource/mod roots and packed entries; `--execution live` uses only the bridge/live Godot path; `--execution auto` exports readable configured resources offline first and escalates to live IPC only when needed
- `auto` is the default and only triggers bridge side effects when a matched asset cannot be exported offline and `transport.kind=ipc` allows lifecycle fallback
- generic `assets extract` does not silently search `.sts2/toolchain/recovered-project`; if a caller intentionally wants recovered files, pass that directory as `--resources-dir`
- when `auto` needs a live bridge and no compatible session is already attached, the CLI reuses the existing `game launch` repair/install flow before retrying asset extraction
- `--format` accepts `auto` by default, plus explicit `png` and `webp`; `auto` preserves offline raster bytes in their source format and uses PNG for live-only previews
- offline raster export still covers directly decodable image files from extracted trees and packed `.pck` entries
- offline font export preserves raw `.ttf`, `.otf`, `.woff`, and `.woff2` bytes from extracted trees and packed `.pck` entries as `artifactKind: "font"` with browser font content types; font assets only support `--format auto`
- offline source export now preserves readable `.tscn` / `.scn` / `.tres` / `.res` bytes from filesystem roots and packed `.pck` entries as `artifactKind: "source"` without flattening them into preview images
- helper-backed discovery is authoritative for both filesystem and packed matches, so one local match no longer suppresses unrelated packed scene/resource results in the same batch
- `model://characters/<id>/visuals` is a virtual live query for model-backed battlefield character visuals. It does not search for or invent a packed `res://scenes/creature_visuals/<character>.tscn` resource, because the battlefield figure can be composed by STS2 runtime/model APIs even when no such scene exists.
- character model keys use public model properties: `icon` renders `IconTexture`, `iconOutline` renders `IconOutlineTexture`, `characterSelectIcon` / `characterSelectLockedIcon` render the public select textures, `mapMarker` renders `MapMarker`, and `energyCounter` / `merchantAnim` / `restSiteAnim` resolve the public path properties. `models characters` also reports real companion paths such as `iconPath`, `energyCounterPath`, `characterSelectBgPath`, and `mapMarkerPath` when authored resources are discoverable.
- card model keys include `image` and `overlay`; potion model keys include `icon` and `outline`; act model keys include `backgroundScene`, `restSiteBackground`, `mapTopBg`, `mapMidBg`, `mapBotBg`, and `chestSpine`.
- relic model keys include `icon`, `iconOutline`, and `bigIcon`; outline and big-icon extraction render the public `RelicModel` textures, and `models relics` reports real companion paths such as `iconPath`, `iconOutlinePath`, and `bigIconPath` when available.
- `model://monsters/<id>/visuals` resolves through `ModelDb.Monsters` to the monster model visual scene and renders that scene through the live extractor.
- `model://events/<id>/backgroundScene`, `model://events/<id>/backgroundSpineStill`, `model://events/<id>/initialPortrait`, `model://events/<id>/vfx`, and ancient event map/run-history variants resolve explicit event model asset properties. `backgroundSpineStill` renders the event background scene's `SpineSprite` as a flattened still (mirroring `model://characters/<id>/characterSelectBgSpineStill`) so the presentation renderer can mount the real background scene and paint the creature over its static cave/vignette layers. There is intentionally no merged `model://events/<id>/background` fallback; use the property-specific key or a direct `res://...` resource.
- character-select backgrounds use `model://characters/<id>/characterSelectBg` when resolving through character models, or exact discoverable scene resources such as `res://scenes/screens/char_select/char_select_bg_defect.tscn` and `res://scenes/screens/char_select/char_select_bg_random_character.tscn`; `character:<id>:select-background:image` is not a supported virtual key.
- a consumer's layout may include rectangles and text without a matching asset provider key. Placeholder-only UI chrome is intentionally omitted instead of exporting successful placeholder/NOPE images.
- representative base-game combat asset keys are now either `model://`, `composed://`, or direct `res://...` resources. Status icons use direct paths such as `res://images/atlases/card_atlas.sprites/status/slimed.tres`, `res://images/atlases/card_atlas.sprites/status/dazed.tres`, and `res://images/atlases/card_atlas.sprites/status/wound.tres`; special combat backgrounds use `composed://combat-background/hive/image` and `composed://combat-background/kaiser_crab_boss/image`; VFX stills use direct paths such as `res://scenes/vfx/block_spark_vfx.tscn`, `res://scenes/vfx/vfx_slime_impact.tscn`, and `res://animations/vfx/vfx_flying_slash/vfx_flying_slash_skeleton_data_resource.tres`.
- exact combat background root scene paths, for example `res://scenes/backgrounds/overgrowth/overgrowth_background.tscn`, remain literal live scene renders for debugging authored template scenes; use the generic alias `composed://combat-background/<id>/image` for composed combat background exports. Alias renders deterministically discover sibling layer scenes from `scenes/backgrounds/<id>/layers`, inject the first sorted variant for each authored root placeholder, disable particle simulation for stable still-image output, render the composed output at full viewport combat-background framing without active-screen dependence, use `renderMode: "flattened-combat-background-composed"`, and preserve source metadata for `scenes/backgrounds/<id>/<id>_background.tscn`
- `assets explain composed://combat-background/<id>/image --execution live` reuses that composed combat-background planner without writing artifacts. It reports the authored root scene, placeholders, discovered layer groups, selected layer paths, texture refs, authored and rendered bounds, transparent-pixel ratio, render notes, warnings, and best-effort active-scene observations. M71 supports only live combat-background aliases; `auto` and `offline` are rejected so callers do not mistake explain for offline extraction.
- `assets explain composed://encounters/<id>/scene-package --execution live` reports provisional encounter-level package metadata without writing artifacts. The response uses `explanationKind: "encounter-scene-package"` and includes `schemaVersion`, `encounterId`, viewport, camera scale/offset with runtime provenance, background source/render query, logical actors, visual parts, states, transitions, exact render-target queries, selector diagnostics, bounds diagnostics, render target decisions, and notices. The checked-in catalog includes the S90 Kaiser Crab/Ovicopter set plus Knowledge Demon Boss metadata with a `burn-fire` VFX visual part and `encounter-visual-vfx-live-only` notice. Unknown encounters must surface unsupported notices or failures rather than inferred packages.
- encounter render-target extraction is explicit and live-only: `composed://encounters/<id>/background/image`, `composed://encounters/<id>/visual-state/<state-id>/overlay/image`, and `composed://encounters/<id>/visual-part/<part-id>/state/<state-id>/image`. Background renders use encounter camera framing; overlay/part renders are transparent viewport-coordinate rasters with selector/bounds diagnostics and artifact checks for nonblank, transparency, framing, and isolation where available. Kaiser-style background/overlay/part targets must either be distinguishable by selector/bounds/artifact-check evidence or preserve structured isolation-failure diagnostics; a full-scene leak is not a successful isolated artifact. Failed live encounter renders may include optional `renderDiagnostics` or `error.details.diagnostic` payloads with request/render target ids, root node path/type, resolved selectors, hidden and kept part ids, viewport size, frame position/scale, `_Ready` and Spine preview hook status, capture timing notes, alpha evidence, and RGB evidence including whether nonzero RGB existed before alpha normalization.
- `scene-subtree://<res-scene>?node=<sceneRelativePath>` renders ONLY the addressed subtree of a packed scene, detached from a never-tree-entered instantiation so no `_Ready`/`_EnterTree` script runs during the detach. `node` is required and case-preserving; `node=.` addresses the scene's own root, which a scene whose root IS the thing to render (a single `GPUParticles2D` effect) has no other way to name. Four optional POSING knobs follow, because the node an EFFECT still wants is usually not renderable exactly as authored — the authored value is the resting state the game tweens away from at runtime:
  - `rect=<x>,<y>,<w>,<h>` captures that NODE-LOCAL rect, one unit per output pixel, at the node's identity scale, with the addressed Control's anchors neutralised. A consumer's placement box is then literally the numbers the request asked for. Omitted, the lane keeps its default framing (the caller's probed frame, or identity, ½Δ-centered into the requested viewport).
  - `shaderParam.<name>=<float>` overrides a uniform on the addressed node's `ShaderMaterial`. A material that is not `resource_local_to_scene` is duplicated before it is written, so an override can never reach the running game's copy.
  - `modulate=<r>,<g>,<b>,<a>` pins the addressed node's modulate. Unclamped, like Godot's own.
  - `particles=live` keeps emitters simulating (and restarts them, so an authored `preprocess` replays into a settled field) instead of the lane's default silence-and-hide.
  - `backdrop=black` captures over opaque black instead of transparency, which is the only faithful way to still an ADDITIVE effect. STS2 authors additive art as fully-opaque sprites that are black where nothing should show; over transparency those black areas become opaque black quads, and whether the result can be re-composited then depends on whether the engine's readback un-premultiplied. Over black the capture IS `black + Σ(contribution)`, which is exactly what an additive compositor adds, and the alpha channel stops carrying meaning. The blank-capture refusal follows: with a backdrop, "nothing rendered" is an all-black image rather than an all-transparent one, and the lane tests for that instead.
  Overrides and the node placement are re-applied immediately before the capture as well as after attach, so a tween a script started on tree entry cannot move the still between warmup and `ForceDraw`. Unlike the VFX preview mode this lane never trims bounds and never normalises alpha, so its pixels are safe to composite: `sts2 --json assets extract "scene-subtree://res://scenes/vfx/uncommon_glow_vfx.tscn?node=.&rect=-256,-256,512,512&particles=live&backdrop=black" --execution live --format png`.
- `dev visual-preflight --print-only` is the explicit S82/S90/S107 live connectivity helper. It remains print-only friendly, does not mutate game files unless the caller opts into launch/attach/endpoint repair, and now emits catalog guidance for checked-in encounters: the `assets explain` command, manifest-ready `assets extract-batch` command, render-target queries, and the `m78-live-encounter-artifacts` validation helper.
- `scripts/validate.sh m78-live-encounter-artifacts --encounter kaiser_crab_boss --json` is the opt-in live artifact path. It writes a repo-local manifest under `.sts2/artifacts/encounters/<encounter>/manifest.json`, runs `assets extract-batch`, and reports generated output and artifact-check summaries. With `--json`, it preserves child extraction diagnostics, including bridge `renderDiagnostics` and `error.details.diagnostic` evidence, instead of reducing reachable-host render failures to a generic validator error. `environment_blocked` or `unavailable` means live host infrastructure was not reachable; a reachable live host that produces blank, misframed, non-isolated, or selector-missing artifacts is a regression.
- Asset commands are read-only against installed game files and never commit official STS2 assets to the repo; extraction writes only caller-selected artifact paths.
- encounter camera transforms apply only to `composed://encounters/*` queries. `composed://combat-background/<id>/image` remains the generic background alias and does not inherit encounter-specific camera scale or offset.
- character-select portraits remain separate texture/model assets and are not reported as battlefield visuals
- live extraction now supports `Texture2D`, `AtlasTexture` card-art previews through `renderMode: "flattened-atlas-texture"` using a CPU crop or warmed-up SubViewport fallback when imported atlas pixels are not CPU-readable, simple texture-backed scenes through `renderMode: "flattened-scene-texture"`, literal authored combat background root scenes through `renderMode: "flattened-combat-background-scene"`, composed combat background aliases through `renderMode: "flattened-combat-background-composed"`, encounter background/overlay/part renders through `flattened-encounter-background`, `flattened-encounter-visual-overlay`, and `flattened-encounter-visual-part`, `AnimatedTexture`, `SpriteFrames`, `StyleBoxTexture`, `PackedScene`, Spine-backed monster first-frame previews framed from authored scene bounds through `renderMode: "flattened-spine-first-frame"`, standalone Spine skeleton-data resource previews through `renderMode: "flattened-spine-skeleton-resource-preview"`, transparent-but-visible additive VFX previews through `renderMode: "flattened-vfx-transparent-preview"`, sampled `Theme` previews, representative `TileSet` previews, and sampled 2D-safe `CanvasItemMaterial` / `ShaderMaterial` previews
- live font/raw-byte extraction uses Godot `FileAccess`, direct `.pck` reads, and font `.import` / `.fontdata` metadata before reporting unavailable browser font bytes; live image, scene, theme, material, and animation previews use `ResourceLoader` plus type-specific preview renderers
- model-backed battlefield character visuals render with `renderMode: "flattened-character-model-battlefield"` and `assetKind: "character-visual"` when the live bridge resolves the character through `ModelDb.AllCharacters`
- timeline-capable live resources emit `artifactKind: "timeline"` with one manifest JSON plus sibling frame files instead of pretending a single frame is the whole asset
- unsupported live previews now report the actual Godot resource type and the missing deterministic rendering context instead of a generic spec-only note
- unknown virtual character ids fail nonzero with a structured `runtime_failure` detail field `character_id`; unsupported character variants fail with detail field `character_variant`
- `--execution offline` for virtual character visuals fails nonzero with `virtual_asset_live_required`, because model-backed battlefield figures require a live bridge
- live scene previews that produce only fully transparent pixels now fail explicitly instead of silently exporting a blank raster artifact
- failed live previews remove any stale deterministic raster or timeline artifact for that source path, so old transparent files are not mistaken for the latest run
- standalone skeleton-data resources such as `*_skel_data.tres` can be rendered directly in live mode as deterministic preview rasters, but the response notes any synthetic skin, animation, scale, material, or bounds defaults; composed scenes that reference the same skeleton remain the preferred path for exact in-game authored visuals
- unrelated non-asset files are ignored even if their paths happen to match the query
- some resource kinds still remain preview-limited when the live host cannot construct a deterministic rendering context

The structured payload includes:

- `status`
- `query`
- `format`
- `executionMode`
- `outputDir`
- `matchCount`
- `exportCount`
- `exports[]`
- exported records now add `artifactKind`, `storageKind`, `extractionMs`, optional `containerPath`, optional `resourceType`, and when relevant `actualFormat`, `renderMode`, `width`, and `height`
- font exports report `artifactKind: "font"`, preserve original bytes, and use content types such as `font/ttf`, `font/otf`, `font/woff`, or `font/woff2`
- timeline exports also add `framesDir`, `frameCount`, and `durationMs`, while `outputPath` points to the timeline manifest JSON

`assets explain` has a separate structured payload:

- top-level `command`, `status`, `query`, `executionMode`, `sourceRoot`, `sourcePath`, `assetKind`, `storageKind`, and `resourceType`
- `explanation.rootScene` for the authored background root and load source
- `explanation.placeholders[]`, `layerGroups[]`, and `selectedLayers[]` for deterministic layer discovery and selection
- `explanation.bounds` for viewport, final composed bounds, visible painted bounds, and transparent-pixel ratio
- `explanation.render` for render mode, warmup frames, transparent-trim settings, and render notes
- `explanation.activeScene` for best-effort comparison against the currently active runtime scene
- `explanation.warnings[]` for missing layers, failed layer loads, unsupported placeholders, empty or transparent-heavy output, suspicious bounds, and active-scene differences
- for encounter scene packages, `explanation.viewport`, `camera`, `background`, `logicalActors`, `visualParts`, `states`, `transitions`, `renderTargets`, and `notices`

Representative encounter explain shape:

```json
{
  "command": "explain",
  "status": "ok",
  "query": "composed://encounters/kaiser_crab_boss/scene-package",
  "assetKind": "encounter-scene-package",
  "explanationKind": "encounter-scene-package",
  "explanation": {
    "schemaVersion": "0",
    "encounterId": "kaiser_crab_boss",
    "camera": {
      "scale": 0.75,
      "offset": { "x": 0.0, "y": 35.0 },
      "source": "EncounterModel.GetCameraScaling/GetCameraOffset"
    },
    "renderTargets": [
      { "query": "composed://encounters/kaiser_crab_boss/background/image" },
      { "query": "composed://encounters/kaiser_crab_boss/visual-state/rocket-charge-up/overlay/image" },
      { "query": "composed://encounters/kaiser_crab_boss/visual-part/rocket/state/rocket-charge-up/image" }
    ],
    "notices": []
  }
}
```

Each export entry includes:

- `sourceId`
- `sourcePath`
- `sourceRoot`
- `assetKind`
- `storageKind`
- `containerPath`
- `resourceType`
- `status`
- `executionMode`
- `extractionMs`
- `artifactKind`
- `actualFormat`
- `outputPath`
- `notes`

### Batch Asset Export

`assets extract-batch` reads a generic JSON manifest and runs the same resolver/exporter behavior as `assets extract` for each request:

```bash
sts2 --json assets extract-batch --manifest ./asset-manifest.json --output ./.sts2/artifacts/mock-assets --format png
sts2 --json assets extract-batch --manifest ./asset-manifest.json --execution offline --resources-dir ./game-project
sts2 --json assets extract-batch --manifest ./asset-manifest.json --format auto --dry-run
sts2 --json assets extract-batch --manifest ./encounter-assets.json --output ./.sts2/artifacts/encounters --execution live --format png
```

Manifest version 0 is intentionally small:

```json
{
  "version": 0,
  "assets": [
    {
      "id": "star_icon",
      "query": "res://images/packed/sprite_fonts/star_icon.png",
      "execution": "offline",
      "metadata": {
        "consumerPath": "ui/star_icon.png"
      }
    },
    {
      "id": "ironclad_battlefield",
      "query": "model://characters/ironclad/visuals",
      "execution": "live",
      "format": "png"
    },
    {
      "id": "kaiser_rocket_overlay",
      "query": "composed://encounters/kaiser_crab_boss/visual-state/rocket-charge-up/overlay/image",
      "execution": "live",
      "format": "png"
    },
    {
      "id": "knowledge_demon_burn_fire",
      "query": "composed://encounters/knowledge_demon_boss/visual-part/burn-fire/state/heavy-attack-burnt/image",
      "execution": "live",
      "format": "png"
    }
  ]
}
```

Each request writes under `<output>/<sanitized-id>/...` and then uses the same source-root/source-path artifact layout as `assets extract`. Request metadata is opaque JSON carried through to the response; `sts2` does not interpret it or build downstream-specific indexes.

The batch payload includes aggregate `status: "ok" | "partial" | "failed"`, a `dryRun` marker, counts for successful, failed, skipped, and exported records, and `results[]` entries with request `id`, `query`, `status`, `metadata`, `exports[]`, and structured `error` when relevant. By default the command continues after failed requests and exits nonzero for `partial` or `failed`; `--fail-fast` stops after the first failed or no-match request and marks later requests as `skipped`. `--dry-run` validates the batch request surface and output settings without resolving search roots, launching the game, creating output directories, or writing artifacts.

For live encounter extraction failures, batch results preserve bridge-provided render diagnostics where available. Consumers should look for `exports[].renderDiagnostics[]` when an export record exists and `results[].error.details.diagnostic` when capture failed before an artifact was written. These diagnostics are additive and may include selector, frame, hook, timing, alpha, and RGB evidence for transparent, blank, misframed, non-isolated, or otherwise invalid background/overlay/part renders.

Batch responses also include timing diagnostics. Root `timing.indexMs` measures the one-time offline asset index build for the command invocation, `timing.resolveMs` is summed per-request matching against that index, `timing.liveSetupMs` is lifecycle launch/repair setup time for live fallback, and `timing.exportMs` is the summed final export/write stage. Each `results[]` entry repeats `resolveMs`, `liveSetupMs`, and `exportMs` for that request. Existing `exports[].extractionMs` remains the per-export final export/write timing and does not include shared indexing.

Generic downstream-adapter pattern:

```bash
sts2 --json assets extract-batch --manifest ./asset-manifest.json --output ./.sts2/artifacts/assets > ./.sts2/artifacts/assets-result.json
node ./scripts/build-project-specific-index.js ./.sts2/artifacts/assets-result.json
```

The adapter script owns the project-specific index and bundle layout. The `sts2` batch result owns only generic extraction records, output paths, provenance, dimensions, notes, and carried caller metadata.

### Search Roots

Managed code commands accept:

- `--assemblies-dir <path>`
- `--mods-dir <path>`
- `--game-path <path>`
- `--include-mods`
- `--limit <n>` for `locate`, `refs`, and `derived`

`code verify-references` is the exception: it resolves game bindings straight out of one directory, so it takes only `--assemblies-dir`, `--game-path`, and its own `--control-assemblies-dir`. Mod roots and dependency visibility do not apply, because the assemblies it resolves against are named by the consumers' own references.

Static scene/resource commands accept:

- `--resources-dir <path>`
- `--assemblies-dir <path>` for best-effort script enrichment
- `--mods-dir <path>`
- `--game-path <path>`
- `--include-mods`
- `--limit <n>` for `scene-search`

Managed code resolution precedence is:

1. command flags
2. `game.assembliesDir` from the active config file
3. first `data_sts2_*` directory under `game.path` from the active config file

Static scene/resource resolution precedence is:

1. command flags
2. `game.resourcesDir` from the active config file
3. `game.path` from the active config file

For scene commands, assembly lookup is optional and only used to enrich attached scripts or global resource classes with follow-up type IDs. If no assemblies are available, results still return raw script/resource paths plus coverage notes.

`--include-mods` adds entries from `modsDir` or `<game.path>/mods`. For managed-code commands this means extra assemblies. For scene commands it means recursive scene/resource scanning under mod roots plus optional mod assembly enrichment.

When you have authoritative local install roots, keep them in `sts2.local.yaml` instead of checking personal paths into `sts2.config.yaml` or pointing the CLI at copied sibling `libs/` folders by default.

The managed metadata catalog cache uses the resolved `toolchain.sharedCacheDir`, which defaults outside the repository on normal user machines. If you point `toolchain.sharedCacheDir` at a repo-local path for tests or diagnostics, keep that path ignored. The helper writes entries below `metadata-catalog/declarations/` and `metadata-catalog/references/` and uses short-lived dot-prefixed `.tmp` files for atomic replacement.

### Output Contract

`locate` returns exit code `0` for both matches and no-match. The structured payload includes:

- `status`: `ok` or `no-match`
- `searchRoots`
- `matchCount`
- `truncated`
- `matches`

Each locate match includes:

- `id`
- `kind`
- `displayName`
- `fullName`
- `namespace`
- `assembly`
- `assemblyPath`
- `source`
- `matchKind`
- `score`
- `signature` for methods

Follow-up IDs are stable for the current installed assemblies:

- type: `type:<assembly>:<full-type-name>`
- method: `method:<assembly>:<declaring-type>::<method>(<param-types>)`

`describe`, `decompile`, `refs type`, `refs method`, and `derived type` return nonzero structured errors for `not_found` and `ambiguous_query`.

`verify-references` reports a verdict rather than a match list, so its exit code carries meaning:

- `0` with `status: "ok"` when every game binding still resolves unchanged
- `3` with `status: "broken"` when the candidate build broke or reshaped at least one binding
- `2` with `status: "control-dirty"` when `--control-assemblies-dir` itself left a binding unresolved, which voids the run
- `2` for the usual usage errors, and `3` with `not_found` for a missing consumer assembly or assemblies directory

The payload includes `consumers`, `gameAssemblies`, `assembliesDir`, `controlAssembliesDir`, `typeReferenceCount`, `memberReferenceCount`, `breakCount`, `notes`, an optional `control` block with its own `status` / `unresolvedCount`, and the four buckets:

- `missingTypes`: a referenced game type that is gone
- `missingMembersWithMissingOwner`: a referenced member whose owner type is gone
- `missingMembers`: a referenced member missing from a type that survived
- `changedSignatures`: a member that resolves on both sides, with `controlSignature` and `candidateSignature` showing the two shapes

Every row names the `assembly`, the `type`, and the `consumers` it came from; member rows add `member`, `memberKind`, and `parameterCount` for methods. `changedSignatures` is empty unless a control build was supplied.

`scene-search` also returns exit code `0` for both matches and `no-match`. The structured payload includes:

- `status`
- `searchRoots`
- `notes`
- `matchCount`
- `truncated`
- `matches`

Each scene/resource match includes stable identity plus follow-up fields where available:

- `id`
- `kind`: `scene`, `node`, or `resource`
- `sceneId`
- `scenePath`
- `sceneUid`
- `storageKind`
- `containerPath`
- `resourcePath`
- `resourceType`
- `nodePath`
- `source`
- `attachedScriptPath`
- `attachedScriptType`
- `attachedScriptTypeId`

`scene-tree` returns one exact scene envelope with:

- `sceneId`
- `scenePath`
- `sceneUid`
- `storageKind`
- `containerPath`
- `notes`
- `nodes[]`

Each `nodes[]` entry uses a flat preorder layout and includes:

- `id`
- `name`
- `nodeType`
- `nodePath`
- `parentNodeId`
- `parentNodePath`
- `depth`
- `childCount`
- `source`
- `storageKind`
- `containerPath`
- optional `instanceSceneId` and `instanceScenePath`
- optional `attachedScriptPath`, `attachedScriptType`, and `attachedScriptTypeId`

`scene-node` returns one exact node envelope with:

- scene identity plus node identity fields such as `sceneId`, `scenePath`, `storageKind`, `containerPath`, `nodeId`, `nodeName`, `nodeType`, and `nodePath`
- `children[]` for immediate child nodes
- `resourceRefs[]` with `property`, `targetKind`, `resourceId`, `resourceType`, `resourcePath`, optional referenced-resource `storageKind` / `containerPath`, and optional follow-up script/type fields
- `nodeRefs[]` with `property`, `rawPath`, `targetNodeId`, `targetNodePath`, and `resolved`

Exact scene and node resolution failures return structured nonzero errors. When script enrichment cannot be resolved, the command keeps the raw script/resource paths and the `notes[]` field explains that enrichment is partial.

### Navigation Surfaces

`describe type` now returns richer member navigation data:

- `baseType` plus `baseTypeId`
- `interfaces` plus `interfaceIds`
- declared `fields`, `properties`, `events`, `methods`, and `nestedTypes`
- `memberCounts`
- method `id` values for direct `describe`, `refs`, and `decompile` follow-up

Compiler-generated property backing fields and accessor methods stay hidden from the default member lists so the output is closer to what a mod author or AI usually wants to inspect.

`describe method` now also returns `declaringTypeId`.

`refs` returns:

- `referenceCount`
- `truncated`
- `references[]`

Each reference includes the referencing container ID, target symbol metadata, `referenceKind`, `via`, and `precision`.

`refs symbol` is intentionally less precise:

- exit code stays `0` for both matches and `no-match`
- every returned reference reports `precision: name`
- `matchedText` shows which target name/signature text matched the query

`derived` returns:

- `derivedCount`
- `truncated`
- `derivedTypes[]`

Each derived result includes the stable type ID plus `relationKind`.

`decompile` now exposes `backend: "metadata" | "ilspy"` alongside `text` and `memberSummaries[]`. The default backend stays metadata-derived; `--full` switches exact type/method matches to embedded ILSpy text while keeping `memberSummaries[]` keyed by method ID. Each summary still exposes `calls[]`, `fieldReads[]`, `fieldWrites[]`, and `typeRefs[]` gathered from indexed metadata and IL tokens.

### Matching Heuristics

`locate` ranks:

1. exact matches on full names, exact IDs, and exact member names
2. prefix matches
3. substring matches across names, full names, declaring types, and namespaces

Results expose both `matchKind` and `score` so downstream tools can see why a match ranked where it did.

### Recommended AI Workflow

Use `sts2 --json inspect reference-topics` or [Modding Reference](./modding-reference.md) when you want the checked-in staged modding workflows and their boundaries.

For AI-assisted modding against managed code, prefer:

1. `sts2 --json code locate ...` when you still need a starting type or method family
2. `sts2 --json code hooks ...`
3. choose a returned stable method `id`
4. `sts2 --json code hook-info <id>`
5. `sts2 --json code describe method <id>`
6. `sts2 --json code refs method <id>` only when you need call-site context
7. `sts2 --json code decompile method <id>` only when the metadata view is not enough
8. retry with `--full` only for exact matches that need source-grade detail

`decompile` is intentionally explicit and deeper. Its default `text` field is metadata-derived C#-like output with grouped members, body-summary comments, and synthetic method bodies. `--full` switches to embedded ILSpy output without changing the surrounding structured envelope.

For UI and screen structure, prefer:

1. `sts2 --json code scene-search ...`
2. choose a `sceneId` or `node` match
3. `sts2 --json code scene-tree <scene>` or `sts2 --json code scene-node <scene> <node-path>`
4. if `attachedScriptTypeId` is present, follow up with `sts2 --json code hooks ...` or `sts2 --json code describe type <attachedScriptTypeId>`
5. use `sts2 --json code hook-info ...`, `code refs ...`, or `code derived ...` when you need code-level behavior behind the scene node

This keeps scene/resource inspection complementary to the existing code-navigation flow instead of replacing it.

## State Shape

The default state envelope now uses the typed bridge contract:

```json
{
  "schemaVersion": "spirectl/v0",
  "gameVersion": "unknown",
  "bridgeVersion": "spirectl-bridge/0.1.0",
  "transportKind": "mock",
  "attachmentState": "stubbed",
  "source": "stub",
  "provisional": true,
  "screen": { "type": "main-menu" },
  "resolvedPerspective": { "scope": "local", "playerId": null, "usesDefault": true },
  "menu": { "menuId": "main-menu", "title": "Main Menu" },
  "map": null,
  "eventRoom": null,
  "treasureRoom": null,
  "relicSelection": null,
  "restSite": null,
  "shop": null,
  "rewards": null,
  "cardSelection": null,
  "simpleCardSelection": null,
  "deckCardSelection": null,
  "bundleSelection": null,
  "lobby": null,
  "run": null,
  "combat": null,
  "choices": [],
  "availableActions": [
    {
      "id": "action:menu:start-run",
      "kind": "choose",
      "arguments": {
        "cardId": null,
        "characterId": null,
        "choiceId": "menu:start-run",
        "mapNodeId": null,
        "playerId": null,
        "targetId": null
      }
    }
  ],
  "notices": [],
  "debug": null
}
```

The default config points to the `main-menu` mock scenario. Setting `transport.mockScenario` to `combat`, `map`, `event-room`, `treasure-room`, `rest-site`, `shop`, `rewards`, `card-selection`, `lobby`, or `lobby-ready` switches the stub response. `state --perspective <local|omniscient> --player-id <id>` requests an explicit multiplayer-aware perspective, and the live/mock state surface now covers combat cards/targets, event/treasure-room/rest-site/shop/reward/card-selection choices, map node choices, multiplayer lobby player/character data, presentation-grade combat fields, and normal-output `notices[]`.

Default state is observable-first. Do not expect hidden scene tree nodes, private RNG queues, or remote-owned details that are not visible from the selected perspective; use `dev scene tree` / `dev scene node` for debug internals and read `notices[]` before treating a missing field as unsupported. The typed `eventRoom` section of `state` carries the event-room payload for validating visible event text such as Ancient rich labels. For overlays, prefer typed `cardOverlay` before compatibility `choices[]`: passive overlays keep the underlying screen actionable, blocking overlays take precedence, and unsupported overlay families report notices.

Representative combat presentation fields are additive and camelCase:

```json
{
  "screen": {
    "id": "combat",
    "source": "mock:combat",
    "rawType": "mock-combat",
    "className": null
  },
  "run": {
    "actLabel": "Act 1",
    "floorLabel": "Floor 3",
    "encounterId": "encounter:jaw-worm",
    "players": [
      {
        "id": "p1",
        "gold": 99,
        "relics": [{ "id": "relic:p1:burning-blood", "counterLabel": "combats" }],
        "masterDeck": { "id": "master-deck:p1", "cardsObservable": true }
      }
    ]
  },
  "combat": {
    "drawPile": { "id": "draw:p1", "count": 1, "cardsObservable": true, "orderObservable": false },
    "hand": [
      {
        "id": "c_1",
        "modelId": "strike_r",
        "description": "Land 6 harm.",
        "type": "attack",
        "targetIds": ["e_1"],
        "assetRefs": [{ "kind": "image", "key": "model://cards/strike_r/image" }]
      }
    ],
    "potions": [{ "id": "potion:p1:0:fire-potion", "description": "Land 20 harm.", "requiresTarget": true }],
    "enemies": [
      {
        "id": "e_1",
        "runtimeEntityId": "entity:jaw-worm:1",
        "modelId": "jaw-worm",
        "intents": [{ "type": "attack", "label": "Gnash", "targetIds": ["p1"] }],
        "visual": { "encounterSlotId": "slot:front", "screenSide": "right" }
      }
    ]
  },
  "notices": [
    { "code": "pile-cards-partial", "path": "combat.discardPile.cards", "severity": "partial", "source": "MockBridgeService" }
  ]
}
```

Asset references are generic keys, not URLs. Consumers should use `notices[]` to understand partial presentation data instead of assuming empty arrays mean a screen is fully covered.

`state actions` is resolved natively in `cli/src/state_actions.rs` from `state`
(no CEL, no `actions.json`); `cli/src/state_actions/testdata/` holds the goldens.

`state actions` says which seat it answered for. `perspective` carries `playerId` (the seat the
returned actions belong to, as the state envelope reported it back), `requestedPlayerId` (the
`--player-id` that was asked for, or `null`), `scope` (`local` or `omniscient`), `usesDefault`, and
`seatCount`. When no `--player-id` is given on a game with more than one seat, `warnings[]` gains a
`perspective_defaulted_multi_seat` entry: on a multi-seat game an empty `actions[]` for the seat the
bridge happened to pick is indistinguishable from "this seat has nothing to do", and that ambiguity
used to be completely silent. Warnings are informational — the exit code is unchanged.

For M4-era semantic action work and its later follow-ups, `availableActions` is intentionally more honest than earlier scaffolding: combat advertises executable `play-card`, `use-potion`, and `end-turn` combinations; S86 non-combat screens advertise executable typed intent verbs for event, treasure/relic, rest-site, shop, reward/card, and map workflows; and target/node discovery still remains visible through `choices[]`. Semantic `act` subcommands accept optional `--player-id <id>` to preserve a multiplayer action requester through the bridge contract; unsupported remote-player actions still fail through normal legality checks. Dangerous raw input remains outside `availableActions`, and does not accept `--player-id` because it is viewport-scoped.

## Transport Config

`transport.kind` accepts:

- `mock`: executable stub bridge transport
- `ipc`: executable local IPC bridge transport when the STS2 host adapter is running; use `transport.ipcPath` on Unix-like hosts and `transport.pipeName` on Windows
- `tcp`: executable standalone framed-protobuf bridge transport over loopback TCP; defaults to `127.0.0.1:51173`

## Platform Support

The CLI builds and runs on Linux, Windows, and macOS. Where a capability needs the host's process
table or user-data layout, the coverage differs:

| Capability | Linux | Windows | macOS |
| --- | --- | --- | --- |
| Local IPC transport | Unix socket (`transport.ipcPath`) | named pipe (`transport.pipeName`) | Unix socket |
| Loopback TCP transport, state, actions, assets, static inspection | yes | yes | yes |
| `game launch` (incl. detached session) | `setsid` | detached process group | `setsid` |
| `game close` / `game kill` / `game deploy --restart` | `/proc` + signals | `Get-CimInstance Win32_Process` (falling back to `tasklist`) + `taskkill` | needs `game.stopCommand` |
| Local Godot/STS2 log diagnostics | `$XDG_DATA_HOME` | `%APPDATA%` | `~/Library/Application Support` |
| Mod load-order (`game mods settings`, `game.modLoadout`) | yes | yes | yes |
| `--instance` isolated builds (`--isolated-build`) | yes | no (needs symlinks) | yes |

Notes:

- Windows process matching prefers PowerShell CIM because it exposes each process's full path and
  command line. When PowerShell is unavailable the CLI falls back to `tasklist` and matches on image
  name alone; those matches are reported with `matchedProcesses[].matchKind: "image-name"` instead
  of `"exe"`, so a coarse match is visible in the JSON. A same-named game from another install could
  match in that mode.
- The integration test suite is Unix-only (it drives the CLI against a Unix-socket bridge stub). The
  Windows build is covered by the crate's unit tests plus a cross-target compile check; see
  [testing.md](./testing.md).

Future-facing endpoint fields already exist in config:

- `game.assembliesDir`
- `game.resourcesDir`
- `game.modsDir`
- `game.modLoadout.enabled`
- `game.modLoadout.settingsFile`
- `game.modLoadout.temporary`
- `game.modLoadout.restoreAfterNoBridgeMs`
- `game.launchWrapper`
- `game.launchExecutable`
- `game.launchArgs`
- `game.launchWorkingDir`
- `game.launchEnv`
- `game.assetLoadGuard`
- `game.disableBackgroundThrottle`
- `game.stopCommand`
- `game.deployBuildCommand`
- `game.deployOutputSubdir`
- `transport.ipcPath`
- `transport.pipeName`
- `transport.tcpAddress`
- `transport.rpcTimeoutMs`

Notes:

- If `transport.ipcPath` is unset, the CLI uses `SPIRECTL_BRIDGE_SOCKET_PATH` when set, otherwise it defaults to the nearest repo root's `.sts2/ipc/spirectl-bridge.sock`.
- If `game.path` is `auto`, the CLI searches Steam library metadata on the current host OS for Slay the Spire 2. On WSL, it also checks Windows Steam roots under `/mnt/<drive>/...`.
- If the default config stack auto-detects a valid STS2 install, commands cache the resolved `game.path` into `sts2.local.yaml`; commands that resolve or use assemblies also cache missing `game.assembliesDir`.
- If configured `game.path` points at an existing directory that is not a valid STS2 install root, the CLI treats it as invalid and falls back to autodiscovery when possible.
- If `game.assembliesDir` is unset, live bridge tooling derives it from the first `data_sts2_*` directory under the resolved game path.
- For `sts2 code ...`, `game.assembliesDir` may also point at any assembly directory with managed `.dll` files.
- If `game.resourcesDir` is unset, `sts2 code scene-*` uses the resolved game path as the static resource root.
- `game.resourcesDir` should point at an accessible Godot resource tree or extracted install root containing supported `.tscn`, `.escn`, `.tres`, `.scn`, `.res`, and `.pck` inputs.
- If `game.modsDir` is unset, live bridge tooling defaults it to `<resolved game path>/mods`.
- If `game.modLoadout.enabled` is unset, `game launch` leaves STS2 mod settings untouched. Set it to `[]` to launch with STS2's native `nomods` arg, or set specific mod ids to temporarily enable only those discovered mods through `settings.save`.
- With `game.modLoadout.temporary: true`, `game launch` restores the previous `settings.save` immediately after launch attachment and schedules a second restore after the launched process exits, because STS2 may save its in-memory mod list during shutdown.
- `game.modLoadout.settingsFile: auto` discovers exactly one mod-capable STS2 `settings.save` under `$XDG_DATA_HOME/SlayTheSpire2` or `~/.local/share/SlayTheSpire2`; pass `game mods settings --settings-file <path>` or configure this field when multiple Steam profiles have mod settings.
- `game.launchWrapper` is an optional local command prefix for display/session wrappers such as `xvfb-run -a` or `gamescope --`; `game.launchExecutable` remains the actual game binary used for lifecycle process targeting. Prefer array form (`launchWrapper: [gamescope, --]`) because it preserves exact argv boundaries, but `launchWrapper: "xvfb-run -a"` is accepted as shell-word shorthand. In agent-host launch mode (`STS2_AGENT_HOST_LAUNCH=1`), the CLI passes this wrapper as broker metadata and spawns the STS2 executable directly so container `xvfb-run` remains available for non-STS2 tools.
- For wrapper-backed launches, `game launch` captures wrapper stdout/stderr to temporary log files and includes log paths in launch JSON plus bounded log tails on launch errors. On Linux, `game close` and `game kill` also track matching wrapper processes whose command line includes the configured game executable, so quick relaunches wait for display/session wrappers such as gamescope to tear down before a new launch.
- If a wrapper-backed `game launch` times out or the wrapper child exits before bridge attachment, the CLI performs one cleanup pass, waits 8 seconds for wrapper/display teardown, and retries the launch once. Retry metadata is reported under `launch.retry` on success or `error.launchRetry` / `error.firstFailure` if the retry also fails.
- `game.launchArgs` may also use array form or shell-word shorthand. Prefer `launchArgs: [--headless, -fastmp, host_standard]`; `launchArgs: "--headless -fastmp host_standard"` is equivalent. Quote arguments that contain spaces, such as `launchArgs: "--profile '/path with spaces/profile.json'"`.
- `game.assetLoadGuard` (default `false`) is a temporary auto-player workaround. When `true`, `game launch` sets `SPIRECTL_BRIDGE_ASSET_LOAD_GUARD=1` so the bridge mod installs its asset-load crash guards, which prefix the game's texture and forced-Ancient-event loads to return empty placeholders. Those placeholders render as a purple missing-texture grid, so leave it `false` for normal/interactive launches. Only enable it for the auto-player, whose software-rendered (`xvfb`) launch still segfaults on game-side texture loads; it exists only until that crash is root-caused. Setting `SPIRECTL_BRIDGE_ASSET_LOAD_GUARD` directly via `game.launchEnv` works too.
- `game.disableBackgroundThrottle` (default `false`) keeps a launched instance usable as an automation target while its window sits in the background. Two things otherwise break it. The game lowers its own frame limit on window focus-out, halving every main-thread cadence the bridge depends on. Worse, the engine draws every iteration and takes a swapchain image with an unbounded wait, so on a compositor that stops releasing an invisible surface's buffers the whole main thread parks inside that acquire (0% CPU in `poll()`, zero protocol traffic) until the window is visible again — bridge RPCs time out, live state/scene streaming stops, and queued actions replay in a burst on un-hide. When `true`, `game launch` (and the relaunch inside `game restart` / `game deploy --restart`) sets `SPIRECTL_BRIDGE_DISABLE_BACKGROUND_THROTTLE=1`, and the bridge mod skips the background frame cap and pins vertical sync ON for the session. Pinning vsync ON is the counter-intuitive part and it is the actual cure: the engine only stops drawing a hidden surface once it considers that surface suspended, and it can reach that state either from a compositor that advertises the toplevel as suspended (not sent by every compositor generation) or from its legacy emulated-vsync timeout path, which it enables only when vertical sync is on and the compositor exposes no fifo protocol. With vsync off neither route exists and the instance hangs on every hide. Nothing is persisted — the saved graphics setting is untouched — and the frame limit still comes from the game's own setting. Prefer it for long-lived developer/agent instances and leave it `false` for normal play. Per-launch equivalent: `game launch --disable-background-throttle`. Individual levers can be turned off for bisection with `SPIRECTL_BRIDGE_BACKGROUND_FPS_UNCAP=0` / `SPIRECTL_BRIDGE_BACKGROUND_VSYNC_ON=0`, and setting `SPIRECTL_BRIDGE_DISABLE_BACKGROUND_THROTTLE` directly via `game.launchEnv` works too. Headless instances are unaffected (no window to background).
- If `transport.pipeName` is unset on Windows, the CLI defaults it to `spirectl-bridge`.
- If `transport.tcpAddress` is unset, the CLI defaults it to `127.0.0.1:51173` and rejects non-loopback addresses.
- If `transport.rpcTimeoutMs` is set, direct live bridge calls fail with `bridge_rpc_timeout` when a local transport connection is accepted but the RPC response does not complete before that timeout. `state` and `dev wait-for` also apply a default bounded RPC timeout when no config value is set, and lifecycle commands expose this as `--rpc-timeout-ms`.
- Machine-specific STS2 paths should live in `sts2.local.yaml`, not in committed docs or examples.

The published npm wrapper contains JavaScript only. It prefers an explicit binary,
`STS2_BINARY_PATH`, or a separately installed `sts2` on `PATH`; otherwise it
downloads the matching Linux x64 or Windows x64 raw binary from the public GitHub
Release, verifies its release-manifest SHA-256, and caches it. Set
`STS2_CACHE_DIR` to relocate that cache. Verify the publish payload with:

```bash
cd npm-wrapper && npm pack --dry-run --json
```

Example local live config (`sts2.local.yaml`):

```yaml
game:
  path: /path/to/SlayTheSpire2
  assembliesDir: /path/to/SlayTheSpire2/data_sts2_<platform>
  resourcesDir: /path/to/SlayTheSpire2
  modsDir: /path/to/SlayTheSpire2/mods
  modLoadout:
    enabled:
      - spirectlbridge
      - BaseLib
      - godotexplorer
    settingsFile: auto
    temporary: true
    restoreAfterNoBridgeMs: 5000
  # Optional Linux display isolation for live rendering tests:
  # launchWrapper: [xvfb-run, -a]
  # launchWrapper: "xvfb-run -a"
  # Temporary auto-player workaround (default false); renders purple placeholder
  # textures, so enable only for the auto-player. See game.assetLoadGuard above.
  # assetLoadGuard: true
  # Keep a long-lived developer instance responsive while its window is hidden
  # (default false). See game.disableBackgroundThrottle above.
  # disableBackgroundThrottle: true
  # launchArgs: "--headless -fastmp host_standard"
  # launchArgs: "--profile '/path with spaces/profile.json'"

transport:
  kind: ipc
  ipcPath: ./.sts2/ipc/spirectl-bridge.sock
  rpcTimeoutMs: 5000
```

## Multi-Instance

`--instance <name>` lets several game instances run in parallel without colliding, so multiple agents can each drive their own game. `SPIRECTL_INSTANCE` and `instances.default` can select the same active instance without repeating the flag in every command; explicit `--instance` / `SPIRECTL_INSTANCE` overrides `instances.default`.

A name deterministically fans out into per-instance state — no shared runtime registry is required to connect, because any later invocation with the same `--instance <name>` re-derives the same socket:

- bridge socket: `<repo>/.sts2/ipc/<name>.sock` (overlaid onto `transport.ipcPath`)
- Godot user-dir: `<instances.dir>/<name>/user` (passed to the game as `--user-dir`, isolating saves, settings, `settings.save` mod load order, shader cache, and logs)
- registry entry: `<instances.dir>/<name>/instance.json` (powers `game instances` and instance-scoped `close`)

`instances.dir` defaults to `./.sts2/instances` and is configurable. `instances.isolatedBuild: true` makes the configured or explicit active instance use isolated-build mode by default, while `--isolated-build` can force isolated mode for one invocation. Instance names must match `[A-Za-z0-9._-]+` (not starting with `.`); `auto` allocates a random `i-XXXXXXXX` name, and `auto`/`all` are reserved.

Example local per-worktree defaults:

```yaml
instances:
  dir: ./.sts2/instances
  default: work-a
  isolatedBuild: true
  symlinkUserDataDirs:
    - my-mod
```

Modes:

- **Shared (default):** all instances share one install and `<install>/mods` with the same bridge build; only the socket and user-dir differ. This is the common case for running the current bridge in parallel.
- **Isolated (`--isolated-build`, Unix only):** the instance also gets its own game mirror under `<instances.dir>/<name>/game` — the big install files are symlinked, the executable is a real hardlink/copy (so `OS.GetExecutablePath()` resolves into the mirror and the game reads the mirror's `mods/`), and `mods/` is real with every shared mod symlinked except `spirectlbridge`, which `install-bridge` deploys as a real per-instance build. Use this to run a *different* bridge build alongside others.

Behavior notes:

- On first launch the per-instance user-dir has no `settings.save`; the CLI seeds it from the shared install's `settings.save` (which already enables the bridge) so the bridge loads. Mod load-order writes target the instance's `settings.save`, never the shared one.
- Seeding copies the shared user-data tree, minus the regenerable dirs it always skips (`shader_cache`, `sentry`, `logs`, `resource-cache`). `instances.symlinkUserDataDirs` names further paths inside `~/.local/share/SlayTheSpire2` that are **symlinked to the shared copy instead of copied**, so a large mod cache is shared by every instance rather than cloned per instance. Entries are relative to that root and may be nested (`my-mod/asset-cache`); absolute paths and `..` are rejected. Unlike the copy pass this runs on every invocation, so adding an entry later still takes effect on an existing instance. A path that already holds a real directory in the instance is left untouched and reported under `modLoadOrder.seededSettings.symlinkedUserDataDirs.notLinkedExistingCopy` (in `game install-bridge` / `game deploy` JSON) — delete that copy yourself to get the symlink on the next run. Linked dirs are shared state: keep them to caches, not per-instance save data.
- `game close`/`game kill` with `--instance` only stop processes whose command line carries that instance's `--user-dir`; isolated instances are additionally scoped by their distinct executable. Without `--instance`, `close` still matches all configured-executable processes — pass `--instance` (or close each by name) when instances are running.
- Process matching starts from the pids the registry recorded for the instance's last launch (`matchKind: "recorded-pid"` in `matchedProcesses`) and falls back to the executable-path scan. That is what lets `close` still find a game launched from a different checkout or through a rebuilt mirror, where the resolved executable path no longer matches. A recorded pid must still prove itself before it is signalled — the instance `--user-dir` on its command line, or an executable matching the resolved or the recorded one — so a recycled pid is not killed.
- When nothing matches, `close` still exits 0 (there was nothing to stop) but reports `stopped: false` and `matchAttempts` `{recordedPids, launchExecutable, recordedLaunchExecutable, instanceUserDir}` instead of an empty success that reads like a stop.
- Multi-instance targets the IPC (Unix socket / Windows named-pipe) transport; `transport.kind: tcp` with `--instance` is rejected.

Examples:

```bash
sts2 game launch --instance a                    # shared build
sts2 game launch --instance b                    # parallel, independent game state
sts2 --instance a game bridge-health             # talks to a's socket only
sts2 --instance b game state                     # b's game state
sts2 game launch                                 # uses instances.default when configured
sts2 game instances                              # list instances + liveness + sockets
sts2 game launch --instance dev --isolated-build # own mods/exe mirror, divergent bridge
sts2 game close --instance a                     # stops only a; b + dev keep running
sts2 game instances prune                        # drop registry entries for stopped instances
```

`game instances` (alias `game instances list`) returns one entry per instance with `name`, `mode`, `socket`, `userDir`, `launchExecutable`, `pid`, `gamePids`, `stdoutPath`, `stderrPath`, `pidLive`, `socketExists`, and `running`. `game instances prune` removes the `<instances.dir>/<name>/` directory for instances that are no longer running.

## Live Workflow

The canonical lifecycle flow is now CLI-native:

- `game install-bridge` builds and stages the bridge artifacts into `<modsDir>/spirectlbridge` without requiring a mod project path, copying the published standalone bridge `.dll` payloads directly from the host project output. It also scans sibling installed mod directories for `Spirectl.Sts2.dll` consumers and, when a Linux `SlayTheSpire2/.../settings.save` contains `mod_settings.mod_list`, moves or inserts `spirectlbridge` before those consumers in that settings load order after writing a `settings.save.spirectl-backup-*` copy. Mod manifests are not changed. JSON output reports `modLoadOrder.dependentModIds` and per-settings-file status under `modLoadOrder.settingsFiles[]`.
- `game deploy` reuses the same bridge packaging/install path, optionally builds and copies a mod project, and can chain restart + verify.
- `game mods settings` reads the saved mod list from a configured, overridden, or auto-discovered `settings.save` and reports `enabledIds` / `disabledIds` for easy copy-paste into `game.modLoadout.enabled`.
- `game mods active` asks the live bridge for `ModManager` state and reports mod ids, names, versions, paths, load states, enabled/active booleans, assemblies, and load errors. It requires `spirectlbridge` to be loaded in the live game.
- `game launch` validates the deployed bridge layout, manifest version, and required bridge files when the effective mod loadout includes `spirectlbridge`; auto-installs or reinstalls the bridge when that layout is missing, stale, or incomplete; applies configured mod loadouts before launch; creates a missing `steam_appid.txt` with the STS2 Steam app id for Steam-managed or Steamworks-backed installs; starts the configured or derived native executable, optionally prefixed by `game.launchWrapper` except in agent-host launch mode where the wrapper is forwarded to the host broker; captures wrapper stdout/stderr when a local wrapper is used; detaches the launched process into a new Unix session by default so short-lived automation shells do not kill the game after attachment; accepts `--no-detach-session` to keep the legacy inherited-session behavior for debugging; appends any `--` passthrough arguments after configured `game.launchArgs`; injects exactly one bridge endpoint env var (`SPIRECTL_BRIDGE_SOCKET_PATH`, `SPIRECTL_BRIDGE_PIPE_NAME`, or `SPIRECTL_BRIDGE_TCP_ADDRESS`) into the spawned process when bridge attachment is expected; waits for attachment with launch-specific timeout/exit failures tied to the active endpoint kind; retries one wrapper-backed attachment timeout after cleanup and an 8-second teardown delay; and returns compact success JSON by default, with the prior full diagnostics payload available through `--verbose`.
- STS2 native `-fastmp` passthrough values are passed unchanged after `--`. In STS2 v0.103.2, known values are `host`, `host_standard`, `host_daily`, `host_custom`, `load`, and `join`; `host_standard`, `host_daily`, and `host_custom` jump directly into hosting those multiplayer run modes.
- `game attach` checks the configured local endpoint and polls `game info` plus `state` over the selected live transport instead of scanning unrelated game logs, while live failures now distinguish Unix-socket bootstrap/socket errors from additive transport misconfiguration and local endpoint connection failures.
- `game close` and `game kill` share the real game executable resolution and exact process matching used by lifecycle commands, even when `game.launchWrapper` is configured. On Linux and Windows they also match wrapper processes whose command line includes that executable, so wrapper teardown is included in lifecycle waits. `game close` is graceful and may use the bridge or `game.stopCommand`; its live bridge close RPC defaults to 10 seconds, including when `game launch` closes an existing game before relaunching. `game kill` is forceful and local-process-only. Neither command writes saves, mutates fixtures, or restores state.
- For machine-specific installs, the default config stack already picks up `sts2.local.yaml`; pass `--config sts2.local.yaml` when you want a command to pin that exact file explicitly.
- `scripts/live-bridge.sh` is now a compatibility helper rather than the canonical workflow.

Current limitations:

- launch/restart still require explicit `game.launchExecutable` when the game root does not expose an unambiguous native executable; when auto-derivation fails, the structured error now includes `configKey`, `searchedCandidates`, and `detectedExecutables`
- macOS requires an explicit `game.stopCommand` for `game deploy --restart`; the structured error names `game.stopCommand` directly when that restart path is unavailable. Linux and Windows stop the game themselves (see [Platform Support](#platform-support))

## Inspect Output

`inspect commands` exposes the command catalog, including:

- command name
- summary
- implementation status
- read-only flag
- example invocations

`inspect examples` returns the same catalog flattened as executable examples. The optional `--command` filter narrows the list to one catalog entry.

`inspect actions` combines bridge handshake metadata with the current state's `availableActions` so humans and AIs can see both supported action kinds and current availability, including structured action arguments such as `playerId`, `cardId`, `targetId`, `rewardId`, `shopItemId`, `restOptionId`, `relicId`, `mapNodeId`, and `eventOptionId`. It also reports `stateContract` guidance for S85/S86 callers: the preferred non-combat and overlay read surface is the active typed section, `cardOverlay` is the typed partial card detail overlay section with observable card/detail, preview, breadcrumb, overlay-policy, validated close/back, and fallback-control metadata when available, blocking/passive/unsupported overlay policy is explicit, compatibility metadata can include `choiceKind`, `intentKind`, `ownerPlayerId`, `perspective`, and `preferredAction`, and notices should preserve `path`, `severity`, `source`, `stability`, and `perspective` when present. The S86 catalog includes typed intent verbs for reward/card, shop, rest-site, treasure/relic, map, and event-room groups. Generic `choose` remains a fallback choose action for currently executable generic, modded, or unmodeled visible controls with no modeled first-party `preferredAction`. For current non-combat screens and overlays, interpret `inspect actions` alongside the active typed state section rather than treating `choices[]` as the primary schema.

When a configured IPC endpoint is stale, missing, refused, or times out before state can be queried, `inspect actions` still returns the same static metadata shape with `screen: null`, `availableActions: []`, and a structured `environment_blocked` notice. The notice details include endpoint kind/address/socket metadata and safe next commands such as `sts2 game bridge-health --repair-stale-endpoint`, `sts2 game attach`, and `sts2 game launch`.

For M4 this means:

- `supportedActions`: the bridge-level action kinds the runtime understands
- `availableActions`: the actions that are actually executable on the current screen right now

## Helper Tool Resolution

`sts2 code ...` resolves the helper tool project by searching upward from the current working directory. For the default helper path, if no current-directory ancestor contains the helper project, the CLI falls back to the `spirectl` source repo that built the binary. A `tools.dotnetToolsPath` value of `dotnet-tools/src/Spirectl.DotnetTools/Spirectl.DotnetTools.csproj` is treated as that same default helper project whether the value comes from built-in defaults or from checked-in config such as `sts2.config.yaml`. The default helper project is built automatically when its DLL is missing or older than helper source/project inputs; otherwise `code` commands invoke the fresh built DLL directly. Custom helper `.csproj` paths intentionally keep the diagnostic `dotnet run` launch path. `sts2 --json toolchain info` reports the selected launch kind, resolved invocation path, availability, and build-needed state so slow helper fallback or rebuild decisions are visible. If the default resolution is not sufficient for your environment, set `tools.dotnetToolsPath` explicitly.
