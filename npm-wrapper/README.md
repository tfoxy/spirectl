# npm-wrapper

`npm-wrapper/` now ships three thin repo-local entrypoints around the existing CLI:

- the existing `sts2` bin wrapper for shell and `npx`-style usage
- a thin ESM Node helper for Playwright and other Node-based runners
- a thin stdio MCP adapter exposed as `sts2-mcp`

This package stays CLI-first. It shells out to the Rust CLI, forces `--json`, returns parsed JSON on success, and throws or returns rich structured errors without translating CLI domain behavior into a separate JS or MCP control plane.

When `serviceUrl` / `serviceToken` or `STS2_SERVICE_URL` / `STS2_SERVICE_TOKEN` are configured, the wrapper can also target the CLI-owned [automation service](../docs/automation-service.md) for catalog-backed calls while keeping the same public API.

## Binary Resolution

The wrapper resolves the CLI binary in this order:

- explicit `binaryPath` passed to `createSts2Client(...)`
- `STS2_BINARY_PATH`
- a genuine, separately installed `sts2` found on `PATH` (the wrapper's own npm shim is ignored)
- a verified cached release binary
- repo-local build outputs under `target/debug/` then `target/release/`
- the matching public GitHub Release, downloaded on demand

The release launcher supports Linux x64 and Windows x64. It downloads the raw CLI asset for the
same version as `@spirectl/sts2`, verifies its SHA-256 against
`spirectl-v<version>-release-manifest.json`, and atomically installs it in the user cache. Use
`STS2_CACHE_DIR` to select a different cache root. Linux follows `XDG_CACHE_HOME` (or
`~/.cache/spirectl`); Windows uses `%LOCALAPPDATA%\\spirectl`.

The repo-local fallback is:

- `target/debug/sts2`
- `target/release/sts2`

If download is unavailable or the platform is unsupported, both the bin wrapper and the Node helper
return a clear launch error. To run without network access, set `STS2_BINARY_PATH`, install a
separate CLI, or build the local CLI.

The published npm tarball contains JavaScript only; native CLI downloads stay in the GitHub Release.

## Agent Skill

This repo also ships a checked-in skill at `skills/spirectl/SKILL.md` for agent ecosystems that install `SKILL.md` files from GitHub repositories.

The first-party CLI installer for local repos is:

```bash
sts2 skill install
sts2 --json skill install
sts2 --json skill install --path ./vendor/skills
```

By default it installs the full self-contained skill directory under `./.agents/skills/spirectl/` relative to the current working directory, while `--path` lets you override the skill root and still install the same checked-in contents.

Install it with:

```bash
npx skills add <owner>/<repo> --skill spirectl
npx skills add https://github.com/<owner>/<repo>/tree/main/skills/spirectl
```

The skill is CLI-first and resolves the runtime in this order:

```bash
if command -v sts2 >/dev/null 2>&1; then
  sts2 --json inspect ai-tools
elif [ -f Cargo.toml ]; then
  cargo run -q -p sts2 -- --json inspect ai-tools
else
  npx @spirectl/sts2 --json inspect ai-tools
fi
```

It uses the same staged CLI guidance documented elsewhere in this repo: prefer `--json`, start with `inspect ai-tools`, `inspect actions`, `inspect state-schema`, `state actions`, or `inspect reference-topics` depending on the task, and load only the matching skill reference file.

Optional MCP startup remains available through `sts2-mcp` or:

```bash
npx -p @spirectl/sts2 sts2-mcp
```

## Node Helper

Public API:

```js
import { Sts2CliError, createSts2Client } from "@spirectl/sts2";

const sts2 = createSts2Client({
  cwd: "/path/to/spirectl",
  configPath: "/path/to/spirectl/sts2.config.yaml",
  mode: "normal",
  serviceUrl: "http://127.0.0.1:4317",
  serviceToken: "local-dev-token",
});
```

Backend selection:

- local CLI path by default
- service-backed for catalog-backed calls when `serviceUrl` / `serviceToken` are passed
- same service-backed selection through `STS2_SERVICE_URL` / `STS2_SERVICE_TOKEN`

Supported methods:

- `gameDetect()`
- `gameInfo()`
- `toolchainInfo()`
- `gameLaunch(...)`
- `gameAttach(...)`
- `gameDeploy(...)`
- `gameClose(...)`
- `gameKill(...)`
- `assetsExtract(...)`
- `assetsExtractBatch(...)`
- `inspectActions()`
- `inspectAiTools()`
- `inspectViewportPresets()`
- `inspectReferenceTopics()`
- `state(...)`
- `stateActions(...)`
- `logs(...)`
- `logHealth(...)`
- `diagnostics(...)`
- `loadFixture(...)`
- `hotReloadStatus(...)`
- `hotReload(...)`
- `scenarioExport(...)`
- `scenarioLoad(...)`
- `http(...)`
- `httpWait(...)`
- `fetch(...)`
- `websocket(...)`
- `projectProfileList()`
- `projectProfileShow(...)`
- `projectProfileRun(...)`
- `projectHookList()`
- `projectHookShow(...)`
- `projectHookRun(...)`
- `skillInstall(...)`
- `screenshot(...)`
- `screenshotDiff(...)`
- `snapshotExport(...)`
- `snapshotCompare(...)`
- `waitFor(...)`
- `assert(...)`
- `act(...)`
- `codeLocate(...)`
- `codeDescribe(...)`
- `codeRefs(...)`
- `codeDerived(...)`
- `codeDecompile(...)`
- `codeHooks(...)`
- `codeHookInfo(...)`
- `codeSceneSearch(...)`
- `codeSceneTree(...)`
- `codeSceneNode(...)`
- `testRun(...)`
- `testStress(...)`

`state(...)` returns the canonical runtime state envelope (`schemaVersion: spirectl.state/v0`) directly for local binary, automation service, and MCP-backed callers: `rootScene` classifies the screen and everything else hangs off `run` (`run.players[]` with `creature`/`deck`/`relics`/`potions`/`overlays[]`/`combat`, `run.currentRoom`, `run.map`, `run.view`, `run.notices`) or `characterSelect`. The pre-cutover top-level `screen`/`scene`/`combat`/`actions[]`/`choices[]` sections and the `view: "full"|"screen"` selectors no longer exist. For the executable surface call `stateActions(...)` (`sts2 state actions`): `actions[].kind` + `actions[].args` for mutation inputs, `actions[].ownerPlayerId` for multiplayer ownership, and `actions[].sourcePath` to join an action back to the state node it came from. Display text (card/relic descriptions, intent labels) is not on the state — read it from the model catalog. Screenshots, asset extraction, raw scene diagnostics, routes, sessions, auth, cache policy, CSS, and visual thresholds stay on separate CLI/downstream surfaces. Pass `state({ perspective, playerId })` when a runner needs a specific local or omniscient/dev view. Structured `notices[]` metadata is passed through unchanged.

Example:

```js
import { createSts2Client } from "@spirectl/sts2";

const sts2 = createSts2Client({
  cwd: process.cwd(),
});

const detect = await sts2.gameDetect();
const info = await sts2.gameInfo();
const catalog = await sts2.inspectAiTools();
await sts2.gameLaunch({ timeoutMs: 30_000, intervalMs: 250, launchArgs: ["--headless", "-fastmp", "host_standard"] });
await sts2.gameAttach({ timeoutMs: 15_000 });
await sts2.gameClose({ timeoutMs: 15_000, intervalMs: 250 });
await sts2.gameDeploy({
  path: "./mods/MyMod",
  build: true,
  restart: true,
  verify: true,
});
const state = await sts2.state();
const { actions } = await sts2.stateActions();

await sts2.waitFor({
  path: "run.currentRoom.roomType",
  equals: "Monster",
  timeoutMs: 5_000,
  intervalMs: 100,
});

await sts2.assert({
  path: "rootScene",
  equals: "run",
});

const logs = await sts2.logs({
  limit: 20,
  level: "warn",
});

const newerLogs = await sts2.logs({
  afterCursor: logs.nextCursor,
  limit: 20,
});

const extracted = await sts2.assetsExtract({
  query: "hand",
  execution: "offline",
  resourcesDir: "./dotnet-tools/tests/Spirectl.DotnetTools.TestSymbols/Fixtures",
});

const characterVisual = await sts2.assetsExtract({
  query: "character:ironclad:battlefield",
  execution: "live",
});

const batch = await sts2.assetsExtractBatch({
  manifest: "./asset-manifest.json",
  output: "./.sts2/artifacts/mock-assets",
  format: "auto",
});

const locate = await sts2.codeLocate({
  subject: "type",
  query: "DeckController",
});

await sts2.loadFixture({
  path: "fixtures/basic-map.sts2.fixture.yaml",
});

The checked-in fixture catalog now covers `main-menu`, `combat`, `map`, `rewards`, `rest-site`, `event-room`, `treasure-room`, `relic-selection`, `shop`, the card-selection families, and `multiplayer-lobby` including the authored load-run slice through the same wrapper helper.

await sts2.scenarioExport({
  output: "tests/scenarios/repros/shop-anchor.sts2.scenario.yaml",
  includeExact: false,
});

await sts2.scenarioLoad({
  path: "tests/scenarios/repros/shop-anchor.sts2.scenario.yaml",
  restart: true,
  timeoutMs: 30_000,
  allowDegradedLocalMultiplayer: true,
});

Scenario helpers return the unchanged CLI JSON payload, including `restoreSupport` on export and the richer `validation.expectedSummary`, `validation.observedSummary`, `validation.mismatches`, optional `multiplayerRestore`, and optional `bridgeVerification` fields on load.

await sts2.http({
  url: "http://127.0.0.1:3000/health",
  expectStatus: 200,
  query: "json.status",
  equals: "ok",
});

await sts2.httpWait({
  url: "http://127.0.0.1:3000/health",
  expectStatus: 200,
  timeoutMs: 10_000,
  intervalMs: 250,
});

await sts2.fetch({
  source: "http://127.0.0.1:3000/build.json",
  output: "./.sts2/artifacts/build.json",
  query: "json.status",
  equals: "ok",
});

await sts2.websocket({
  url: "ws://127.0.0.1:3001/events",
  sendText: ["ping"],
  expectText: ["pong"],
  timeoutMs: 5_000,
});

const hooks = await sts2.projectHookList();
const hook = await sts2.projectHookShow({ name: "repo-check" });
await sts2.projectHookRun({
  name: "repo-check",
  input: { kind: "smoke" },
});

await sts2.skillInstall({
  path: "./vendor/skills",
});

await sts2.screenshot({
  preset: "desktop-1080p",
  output: ".sts2/artifacts/runtime.png",
});

await sts2.screenshotDiff({
  baseline: "tests/scenarios/baselines/mock-main-menu.png",
  preset: "desktop-1080p",
  bundleDir: ".sts2/artifacts/visual/main-menu",
});

await sts2.snapshotExport({
  spec: "tests/snapshots/main-menu.sts2.snapshot.yaml",
  output: ".sts2/artifacts/snapshots/main-menu",
});

await sts2.snapshotCompare({
  spec: "tests/snapshots/main-menu.sts2.snapshot.yaml",
  baseline: "tests/snapshots/baselines/main-menu",
  bundleDir: ".sts2/artifacts/snapshots/main-menu-compare",
});

const run = await sts2.testRun({
  scenario: {
    name: "smoke-inline-object",
    steps: ["game.info"],
  },
});

const stress = await sts2.testStress({
  path: "tests/scenarios/regression-main-menu.sts2.yaml",
  iterations: 10,
});
```

`testRun(...)` requires exactly one input source:

- `path` for checked-in `.sts2.yaml` / `.sts2.json` files or directories
- `inline` for raw YAML/JSON scenario text
- `scenario` for a JS object/array serialized through the existing CLI `--inline` path

When the client is configured with `serviceUrl`, set `durable: true` to require durable remote-job storage. Local CLI-backed `testRun(...)` ignores `durable`.

Scenarios executed through `testRun(...)` can include `game.deploy`, `dev.console`, `dev.load-fixture`, `dev.fixture.load`, and `dev.load-scenario` steps. Relative deploy/fixture/scenario paths follow the same CLI rules: file-backed scenarios resolve them relative to the scenario file, while inline text and JS object scenarios resolve them relative to the client's `cwd`. Runner artifacts keep `dev.load-fixture` as the canonical fixture-load step id and write console step output as `steps/NNN-dev-console.json`.

`snapshotExport(...)` and `snapshotCompare(...)` are direct wrappers over `sts2 dev snapshot export ...` and `sts2 dev snapshot compare ...`, so they keep the same bounded authored-spec model, normalized JSON payloads, and optional bundle-directory evidence paths as the CLI.

`testStress(...)` is a direct wrapper over `sts2 test stress ...`, reusing the existing scenario runner under bounded iteration or duration limits rather than creating a second JS-native runner stack.

`logs(...)` mirrors the one-shot CLI log surface, including `limit`, `tail`, `level`, `target`, and cursor catch-up through `afterCursor`. Repeated `logs({ afterCursor: previous.nextCursor })` calls let Node callers page or poll for new entries over the same cursor-backed bridge contract that powers CLI `dev logs --tail`.

`gameDetect()` maps to `sts2 game detect`. `gameInfo()` maps to `sts2 game info`. `toolchainInfo()` maps to `sts2 toolchain info`. `gameLaunch(...)` maps to `sts2 game launch [--timeout-ms <ms>] [--interval-ms <ms>] [-- <game-arg> ...]`. `gameAttach(...)` maps to `sts2 game attach [--timeout-ms <ms>] [--interval-ms <ms>]`. `gameDeploy(...)` maps to `sts2 game deploy <path> [--build] [--restart] [--verify] [--timeout-ms <ms>] [--interval-ms <ms>]`. `gameClose(...)` maps to `sts2 game close [--timeout-ms <ms>] [--interval-ms <ms>]`. `gameKill(...)` maps to `sts2 game kill [--timeout-ms <ms>] [--interval-ms <ms>]` on the local CLI path; force kill is not promoted through the MCP/AI tool catalog. `assetsExtract(...)` maps to `sts2 assets extract <query> [--execution <mode>] [--format <fmt>] [--game-path <dir>] [--resources-dir <dir>] [--mods-dir <dir>] [--include-mods]`. `assetsExtractBatch(...)` maps to `sts2 assets extract-batch --manifest <path> [--output <dir>] [--execution <mode>] [--format <fmt>] [--fail-fast] [--game-path <dir>] [--resources-dir <dir>] [--mods-dir <dir>] [--include-mods]`. `inspectViewportPresets({ presetCatalogs? })` maps to `sts2 inspect viewport-presets [--preset-catalog <path> ...]`. `inspectReferenceTopics()` maps to `sts2 inspect reference-topics`. `devConsole({ command, args, mode })` maps to `sts2 dev console <command> [args...]` by default, or dangerous mode for `achievement`, `cloud`, or `unlock`. `loadFixture(...)` is a direct wrapper over `sts2 dev fixture load --path <fixture>`. `hotReloadStatus({ project })` maps to `sts2 dev mod-reload status --project <dir>`. `hotReload({ project, build, wait, timeoutMs, intervalMs })` maps to `sts2 dev mod-reload --project <dir> [--build] [--wait] [--timeout-ms <ms>] [--interval-ms <ms>]`. `scenarioExport(...)` maps to `sts2 dev scenario export --output <scenario> [--include-exact]`. `scenarioLoad(...)` maps to `sts2 dev scenario load --path <scenario> [--restart] [--timeout-ms <ms>] [--interval-ms <ms>] [--allow-degraded-local-multiplayer]`. `http(...)`, `httpWait(...)`, `fetch(...)`, and `websocket(...)` map directly onto the matching `sts2 dev *` probe commands. `projectProfileList()`, `projectProfileShow(...)`, and `projectProfileRun(...)` are direct wrappers over `sts2 project profile *`. `projectHookList()`, `projectHookShow(...)`, and `projectHookRun(...)` are direct wrappers over `sts2 project hook *`. `skillInstall(...)` maps to `sts2 skill install [--path <dir>]`. `screenshot(...)` maps to `sts2 dev screenshot [--preset <name> | --width <px> --height <px>] [--preset-catalog <path> ...] [--output <path>]`. `screenshotDiff(...)` maps to `sts2 dev screenshot-diff --baseline <path> [--actual <path>] [--preset <name> | --width <px> --height <px>] [--preset-catalog <path> ...] [--bundle-dir <dir>] [--max-diff-pixels <n>] [--max-diff-ratio <ratio>]`. `snapshotExport(...)` maps to `sts2 dev snapshot export --spec <file.sts2.snapshot.yaml> --output <dir> [--preset-catalog <path> ...]`. `snapshotCompare(...)` maps to `sts2 dev snapshot compare --spec <file.sts2.snapshot.yaml> --baseline <dir> [--preset-catalog <path> ...] [--bundle-dir <dir>]`. `testStress(...)` maps to `sts2 test stress <path|dir> [--iterations <n> | --duration-ms <n>] [--max-failures <n>] [--cooldown-ms <n>]`. `codeHooks(...)` and `codeHookInfo(...)` map directly onto `sts2 code hooks ...` and `sts2 code hook-info ...`. `diagnostics(...)` also accepts the same optional viewport and `presetCatalogs` inputs so failure bundles can capture repeatable `runtime.png` artifacts through the existing CLI surface. All wrapper helpers return the unchanged CLI JSON payload and preserve structured nonzero-exit failures as `Sts2CliError`.

`screenshot({ rpcTimeoutMs })` and live `screenshotDiff({ rpcTimeoutMs })` forward `--rpc-timeout-ms <ms>` to bound bridge-backed capture.

The wrapper remains intentionally thin:

- it always appends `--json`
- it only parses `stdout`
- it returns the CLI payload untouched on success
- it rejects on non-zero exit code without translating CLI semantics into a new JS protocol

For asset diagnostics, `assetsExplain(...)`, `assetsExtractBatch(...)`, and the MCP tools return the CLI JSON shape unchanged. Encounter selector diagnostics, missing-selector notices, bounds, render target decisions, artifact checks, export notes/notices/provenance, and uncataloged encounter failures remain attached to the same `explanation`, `results[].exports[]`, and `results[].error` fields emitted by `sts2`.

For S90 representative encounter reliability, call `assetsExplain({ query: "encounter:kaiser_crab_boss:scene-package", execution: "live" })`, then batch the cataloged background, overlay, and part render-target queries with `assetsExtractBatch(...)`. The repo helper `scripts/validate.sh m78-live-encounter-artifacts --encounter kaiser_crab_boss --json` is the explicit live artifact path; it writes a manifest and artifacts under `.sts2/artifacts/...`, does not mutate installed game files, and reports `environment_blocked` / `unavailable` when live infrastructure is missing. No official STS2 assets should be committed or packaged by wrapper examples.

`inspectAiTools()` is the Node-facing entrypoint to the same AI-tool catalog consumed by the MCP adapter.

`act(...)` mirrors the CLI action kinds directly, including reward/card, shop, rest-site, treasure/relic, map, event-room, combat, and lobby verbs plus dangerous-mode `mouse-click`. The normal path is: call `state()`, choose one current entry from `actions[]`, pass its kind and compact args to `act(...)`, and preserve the resulting structured payload. Semantic actions accept optional `playerId`, which maps to `sts2 act ... --player-id <id>` and preserves the intended multiplayer requester through bridge legality checks; it does not make unsupported remote-player execution valid by itself. `choose` remains fallback-only for generic, modded, or unmodeled visible controls with no modeled semantic action. Action errors preserve CLI structured payloads, including `actionFailure.reasonCode` values such as `wrong-screen`, `wrong-player`, `not-visible`, `not-enabled`, `missing-hook`, `ambiguous-hook`, `stale-id`, `unsupported-perspective`, and `dangerous-mode-required`; wrapper callers should inspect ownership, local role, action, and `reasonCode` instead of parsing error text. `mouse-click` routes to `sts2 --mode dangerous act mouse click --x <x> --y <y> [--button <button>]`, so callers must create the client with `mode: "dangerous"` when they want raw input fallback behavior.

## Completion

Completion remains CLI-managed and safe-by-default. The wrapper does not mutate shell startup files or add a second install mechanism; it simply exposes the existing CLI command through the packaged or resolved `sts2` binary.

Typical wrapper-facing flows:

```bash
npx @spirectl/sts2 completion bash
npx @spirectl/sts2 completion install bash
npx @spirectl/sts2 completion install bash --path ~/.config/spirectl/completions/sts2.bash
```

Those commands still return the Rust CLI activation metadata and still leave shell-profile edits explicit.

## MCP Adapter

Run the repo-local stdio MCP adapter first for local AI use:

```bash
npm --prefix npm-wrapper exec sts2-mcp
npx -p @spirectl/sts2 sts2-mcp
```

The adapter loads `sts2 --json inspect ai-tools` once at startup, registers that tool catalog over MCP, and maps tool calls back onto the existing wrapper methods and CLI commands.

Network MCP is opt-in and uses Streamable HTTP. Keep loopback as the default:

```bash
npx -p @spirectl/sts2 sts2-mcp --transport http --listen 127.0.0.1:4318
```

Public binds require bearer auth plus acknowledgement:

```bash
STS2_MCP_AUTH_TOKEN='replace-with-a-secret' \
npx -p @spirectl/sts2 sts2-mcp \
  --transport http \
  --listen 0.0.0.0:4318 \
  --auth-token "$STS2_MCP_AUTH_TOKEN" \
  --acknowledge-network-risk
```

To route wrapper or MCP calls through the CLI-owned automation service, set the service backend variables:

```bash
export STS2_SERVICE_URL=http://127.0.0.1:4317
export STS2_SERVICE_TOKEN=local-dev-token
```

Durable remote `test_run` stays on the service runner contract. Start the service with durable storage, submit the run, poll it, then retrieve the artifact index or downloaded files:

```bash
sts2 --json service serve --listen 127.0.0.1:4317 --auth-token local-dev-token --job-store durable

curl -X POST \
  -H 'Authorization: Bearer local-dev-token' \
  -H 'Content-Type: application/json' \
  http://127.0.0.1:4317/v0/test-runs \
  -d '{"inline":"{name: remote-smoke, steps: [game.info]}","durable":true}'

curl -H 'Authorization: Bearer local-dev-token' \
  http://127.0.0.1:4317/v0/test-runs/run-1/artifacts
```

Useful environment variables:

- `STS2_BINARY_PATH`
- `STS2_CONFIG_PATH`
- `STS2_MODE`
- `STS2_CWD`
- `STS2_SERVICE_URL`
- `STS2_SERVICE_TOKEN`
- `STS2_MCP_TRANSPORT`
- `STS2_MCP_LISTEN`
- `STS2_MCP_AUTH_TOKEN`
- `STS2_MCP_ACKNOWLEDGE_NETWORK_RISK`

Tool result contract:

- success: `structuredContent` is the unchanged CLI JSON payload and `content[0].text` contains the same payload as JSON text
- expected CLI failure: `isError: true` with `{ exitCode, argv, stderr, payload }`
- adapter-only failure: `isError: true` with `{ code, message, argv, stdout, stderr }`

The adapter is intentionally small:

- it does not reimplement business logic
- it exposes only the approved standalone AI-tool subset from `inspect ai-tools`, including the promoted replacement set such as `toolchain_info`, `project_profile_*`, `diagnostics`, `console`, `project_hook_*`, `inspect_viewport_presets`, `screenshot_diff`, `snapshot_export`, `snapshot_compare`, `test_stress`, `code_hook_*`, `inspect_reference_topics`, `debug_*`, and `breakpoint_*`
- it excludes dangerous raw input and other commands intentionally omitted from `inspect ai-tools`
- its HTTP transport adds only bearer-token gating and network bind guardrails; TLS, reverse proxy policy, firewalling, and multi-tenant operation are operator responsibilities

Use [docs/mcp-migration.md](../docs/mcp-migration.md) when a downstream repo needs the replacement checklist for deleting a vendored legacy MCP tree.

## Error Handling

Non-zero exits reject with `Sts2CliError`.

```js
import { Sts2CliError } from "@spirectl/sts2";

try {
  await sts2.assert({
    path: "screen.type",
    contains: "combat",
  });
} catch (error) {
  if (error instanceof Sts2CliError) {
    console.error(error.exitCode);
    console.error(error.argv);
    console.error(error.payload);
    console.error(error.stdout);
    console.error(error.stderr);
  }

  throw error;
}
```

`Sts2CliError` includes:

- `exitCode`
- `argv`
- `stdout`
- `stderr`
- `payload`

If `stdout` is valid JSON, `payload` contains the parsed object. If `stdout` is not valid JSON, `payload` is `null` and the raw streams are still preserved.

The MCP adapter preserves the same CLI failure payloads in tool results rather than translating them into a second domain schema, including `actionFailure` wrong-player and unsupported-perspective diagnostics.

## Playwright Fixture

`@spirectl/sts2/playwright` exports `withSts2(...)`, a thin Playwright fixture extender over the same wrapper client.

```ts
import { test as base } from "@playwright/test";
import { withSts2 } from "@spirectl/sts2/playwright";

export const test = withSts2(base, {
  client: {
    cwd: process.cwd(),
    configPath: `${process.cwd()}/sts2.config.yaml`,
    mode: "normal",
  },
  failureArtifacts: {
    enabled: true,
    logs: {
      tail: 50,
    },
  },
});
```

When `failureArtifacts.enabled` is `true`, unexpected Playwright failures trigger best-effort capture through `testInfo.outputPath(...)`:

- `sts2-diagnostics.json`
- `sts2-evidence/`

The fixture does not auto-launch, auto-deploy, or add a second runner stack. It creates the same `createSts2Client(...)` instance you would create manually, then optionally captures failure-time diagnostics through `diagnostics()` with the configured log-health options folded into the same bundle.

`failureArtifacts.screenshot` can forward `preset`, `width`, and `height` into that same diagnostics capture path when you want repeatable failure-time `runtime.png` evidence without inventing a separate Playwright screenshot mechanism.

`hotReloadAndWait(sts2, options)` wraps the approved M57/M60 shell hot-reload flow for Playwright loops that need to build, reload, and assert that the active generation advanced. It uses `hotReloadStatus(...)` before/after and `hotReload({ wait: true, ... })`; it does not deploy/restart shells, edit source files, or attempt arbitrary native mod hot reload.

```ts
import { expect } from "@playwright/test";
import { hotReloadAndWait, withSts2 } from "@spirectl/sts2/playwright";

test("logic reloads", async ({ sts2 }) => {
  const reload = await hotReloadAndWait(sts2, {
    project: "./mods/MyHotMod",
    build: true,
    timeoutMs: 30000,
  });

  expect(reload.generationChanged).toBe(true);
});
```

## Playwright Example

See [examples/playwright-live-smoke.spec.ts](./examples/playwright-live-smoke.spec.ts) for a realistic example that:

1. assumes `scripts/live-bridge.sh deploy` and `scripts/live-bridge.sh verify` already succeeded
2. extends Playwright with `withSts2(...)`
3. drives a generic phone/web UI with Playwright
4. verifies the real STS2 runtime with `state`, `waitFor`, `assert`, and `logs`

The selectors and URLs in that example are intentionally generic because this repo does not yet ship the actual phone/web UI.

## Local Tests

Recommended verification flow:

```bash
mise exec -- cargo test -p sts2
npm --prefix npm-wrapper test
cargo run -p sts2 -- --json inspect ai-tools
```
