# Capabilities

What `spirectl` can do today, grouped by area. Each line points at the doc with the detail; `sts2 --json inspect commands`,
`inspect actions` and `inspect ai-tools` are the authoritative, machine-readable versions. For what it does not do, see
[known gaps](known-gaps.md).

## Game lifecycle and bridge

- Install the bridge on its own (`sts2 game install-bridge`) or deploy bridge plus mod in one step
  (`sts2 game deploy <mod-project> --build --restart --verify`).
- `sts2 game launch` / `game attach` as separate steps. Launch repairs a missing, stale or incomplete bridge payload
  first, supports a `game.launchWrapper` prefix (for example `xvfb-run -a`), passes extra arguments after `--`, and
  reports structured `launch_*` failures with the config key to fix.
- Bounded graceful shutdown (`sts2 game close`) and an explicit force path (`sts2 game kill`).
- `sts2 game bridge-health` is the read-only preflight: it distinguishes missing, stale, timed-out, version-mismatched
  and wrong-game-build bridges without mutating anything. `sts2 dev visual-preflight` adds lifecycle and artifact checks.
- Source builds select API lanes `v107` (v0.107.1), `v111` (v0.111.0), and `v113` (v0.113.0).
  Released bridge payloads currently cover `v107`; the beta lanes require a source checkout.
- Live transport over Unix-domain sockets, Windows named pipes, or loopback TCP. See [CLI](cli.md).

## Runtime state

- `sts2 state` is a compact, screen-aware agent snapshot: canonical `actions[]`, normalized `scene`, `decision`,
  gameplay facts, `fallbackChoices[]` and structured notices. `state full`, `state screen`, `state actions` and
  `inspect state-schema` cover the expanded and bounded views.
- Supported screens: main menu, combat, map, event room (including Fake Merchant and Crystal Sphere), treasure room and
  relic selection, rest site, shop, rewards, the card-selection family (card, simple, deck and bundle selection), card
  overlays, and the start-run and load-run lobbies.
- Combat state carries presentation-grade data (gold, relics, potions, piles, status effects, card descriptions, enemy
  intents and visuals) as generic, additive fields. Cataloged encounters also expose `combat.encounterVisuals`.
- Multiplayer is first-class: player-scoped choices carry `ownerPlayerId` and perspective metadata, players carry
  `isLocal` / `isHost` / `isRemote`, `--perspective local --player-id <id>` filters a player's view, and wrong-player
  actions fail with structured `wrong-player` / `unsupported-perspective` errors.
- `sts2 state refs` returns a compact model/asset reference index for caches.
- `sts2 models ...` serves the game's model catalogs; localization and asset bytes are separate surfaces.

## Semantic actions

- Typed intent actions for every supported screen: play cards, use potions, end turn, select map nodes, claim or skip
  rewards, select or skip cards and bundles, buy and remove in shops, rest-site options, chests and relics, event
  options, Crystal Sphere controls, staged card-grid confirm/cancel, and lobby ready / character selection. The full
  list, with argument schemas and failure reason codes, is `sts2 --json inspect actions`.
- `sts2 act choose` is an explicit fallback for generic, modded or unmodeled visible controls.
- Raw viewport clicks exist only in dangerous mode (`sts2 --mode dangerous act mouse click`) and stay out of the AI
  catalog.

## Dev automation and debugging

- `dev logs` (cursor-backed, tail and follow), `dev log-health`, `dev wait-for`, `dev assert` and `dev diagnostics`
  bundles over the same typed state. See [debugging](debugging.md).
- `dev screenshot` with viewport presets, `dev screenshot-diff` (live or offline, with optional mask and region
  scopes), and `dev snapshot export|compare` regression bundles.
- `dev fixture load|record|resume|status|clear` for authored screen-entry fixtures, and `dev scenario export|load` for
  shareable sparse scenarios with honest per-field restore reports.
- A live debugger: one controller lease plus read-only observers, pause/resume/step, bounded event replay and follow,
  and `match` / `change` breakpoints.
- `dev console` runs explicit in-game console commands; persisted-file commands require dangerous mode.
- `dev scene tree|node|children|set-visible` for live scene diagnostics, with opt-in properties and computed transforms.
- `dev http`, `dev http-wait`, `dev fetch` and `dev websocket` probes for validating a mod's own endpoints.

## Test runner

- `sts2 test run` executes YAML/JSON scenarios with deploy, fixture, scenario, hot-reload, console, screenshot,
  snapshot and assertion steps, and persists `summary.json` / `result.json` plus failure evidence.
- `sts2 test stress` repeats scenarios with bounded iteration, duration and cooldown controls.
- See [testing](testing.md), [scenario contracts](scenario-contracts.md) and [CLI](cli.md#test-runner).

## Assets and static inspection

- `sts2 assets extract` exports rasters and readable scenes/resources offline, and escalates live-only previews
  (flattened scenes, atlas crops, Spine first frames, composed combat backgrounds, encounter render targets, VFX stills)
  through the bridge. `assets explain` shows the plan without writing artifacts; `assets extract-batch` runs a
  manifest of requests.
- `sts2 code locate|describe|refs|derived|hooks|hook-info|decompile` navigates the game's managed code metadata, and
  `code scene-*` inspects Godot text, binary and packed scenes/resources. `code verify-references` checks a built mod
  against another game build.
- `sts2 project recover` writes decompile and recovered-project corpora under the repo-local toolchain root.

## Mod development

- `sts2 project scaffold stable-harmony-trampoline` creates a stable Harmony shell with a reloadable logic assembly,
  and `sts2 dev mod-reload` drives restart-free reloads for those projects. Optional analyzer guardrails and a dev
  overlay are available. See [modding reference](modding-reference.md).
- `sts2 project profile list|show|run` and `sts2 project hook list|show|run` run checked-in lifecycle profiles and
  repo hooks. `sts2 toolchain info` reports where everything lives.

## Embedding in a .NET mod

- `Spirectl.Sts2.Embedding.ISpirectlRuntime` gives another mod in-process state (`GetCurrentState`), reactive
  subscriptions (`SubscribeCurrentState`, `WatchCurrentStateAsync`), semantic actions, models and assets
  (`runtime.Assets`), with no CLI, service or IPC host required. See [embeddable runtime](embeddable-runtime.md).
- Live Godot scene-state producers publish the scene tree a downstream renderer needs.
- The `@spirectl/presentation` TypeScript package ships two subpaths, `./render` (animation bindings, BBCode tags,
  play-zone threshold) and `./spine` (DOM-free Spine/geoclip clip parsing and frame sampling). It is not a renderer.

## AI and automation surfaces

- `inspect ai-tools` publishes a curated, safe tool catalog over the CLI's JSON envelopes. See [AI tools](ai-tools.md).
- `sts2 service serve` exposes that catalog over HTTP/JSON, with optional network MCP and async remote `test run` jobs
  (memory-only or durable). See [automation service](automation-service.md).
- `npm-wrapper` (`@spirectl/sts2`) is a thin Node client, with a Playwright fixture (`@spirectl/sts2/playwright`) and
  the `sts2-mcp` stdio/Streamable-HTTP MCP adapter. See [MCP migration](mcp-migration.md) for moving off vendored MCP
  trees.
- The repo ships a repo-native self-contained skill pack at `skills/spirectl/`. Install it with
  `npx skills add <owner>/<repo> --skill spirectl` or `sts2 skill install`. See [AI skill](ai-skill.md).

## CLI conveniences

- `--json` and reliable exit codes on every major command; `sts2 inspect commands|examples` describe the surface.
- `sts2 completion` generates Bash/Zsh/Fish/PowerShell completions; `completion install` writes a managed file instead
  of editing shell startup files.
- Optional local usage tracking (`usageTracking.enabled`) records command paths only, never arguments.
