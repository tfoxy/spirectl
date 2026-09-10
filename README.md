# spirectl

`spirectl` is a source-first automation and inspection toolchain for Slay the Spire 2. It gives mod developers, external tools, and AI-driven workflows structured runtime state, semantic actions, static code and resource inspection, and repeatable development workflows—without reducing automation to brittle raw clicks.

Use it when you are building or debugging a mod, writing an external runner or CI workflow, exploring your local game installation, or giving an agent a safe, machine-readable way to work with the game. It is not a polished end-user mod manager or a one-click packaged product.

## Status

`spirectl` is pre-1.0. The released packages currently use version `0.1.0`, and spirectl-owned contracts use `v0`; APIs, schemas, protocol namespaces, routes, and stored artifacts may change without compatibility notice.

## What It Does

| Area | What you get |
| --- | --- |
| Live runtime | Screen-aware state, stable machine IDs, perspective-aware multiplayer data, and executable semantic actions. |
| Semantic automation | Intent-first actions for combat and supported non-combat screens; dangerous raw mouse input is an explicit fallback. |
| Static inspection | Assembly metadata, references, decompile support, Godot scene/resource search, and staged hook discovery. |
| Assets and models | Structured model/reference data plus offline or live asset resolution and extraction. |
| Development and tests | Game lifecycle control, bridge logs, waits, assertions, fixtures, screenshots, scenarios, and artifacts. |
| Integrations | A thin Node client, stdio MCP adapter, optional automation service, Playwright helpers, and an installable agent skill. |

`--json` is first-class across the command surface. Prefer it for scripts, CI, agents, Playwright, MCP, and external runners.

## Install From Source

The reliable install path today is a local checkout. You need Rust and `cargo`; the bridge and helper tools also need the .NET 10 SDK. Node.js 24+ is needed only for the npm wrapper, Node client, or MCP adapter. [`mise`](https://mise.jdx.dev/) is optional but recommended for the pinned toolchain.

```bash
mise install
mise exec -- cargo build -p sts2

# Optional: build the release binary and link it as `sts2` in ~/.local/bin.
scripts/install-cli.sh
```

Without `mise`, run `cargo build -p sts2`. From a checkout, substitute `cargo run -p sts2 --` for `sts2` in the examples below if the binary is not on your `PATH`.

Most game-aware workflows need machine-specific paths. Create ignored local config from the template, then edit it for your installation:

```bash
cp sts2.local.example.yaml sts2.local.yaml
sts2 --json config resolve
```

Keep `sts2.local.yaml` uncommitted. The committed `sts2.config.yaml` is machine-neutral; `config resolve` reports the absolute paths and their provenance.

## Choose Your Path

### Inspect Code, Scenes, Or Resources

Use this path when you want to inspect your legitimately obtained local installation without attaching to a running game. Configure the local paths above, then start with the CLI's own catalog and staged static inspection:

```bash
sts2 inspect commands
sts2 --json code locate type CombatScreen
sts2 --json code scene-search HandPanel
sts2 --json assets catalog --execution offline
```

For more involved code inspection, begin with `code locate` or `code hooks`, follow stable IDs through `code describe`, `code refs`, or `code hook-info`, and use decompilation only when metadata is insufficient. See the [CLI reference](docs/cli.md) and [static-inspection map](docs/maps/static-inspection.md).

### Drive A Live Game Or Iterate On A Mod

Use this path for live state, legal actions, fixture loading, screenshots, or deploy/restart loops:

```bash
sts2 game detect
sts2 game install-bridge
sts2 game launch
sts2 --json game bridge-health

sts2 --json state
sts2 --json state actions
```

Read `state` and `state actions` before mutating the game. Select an action that the live state marks executable and supply its advertised IDs and arguments. For example, `sts2 act end-turn` is appropriate only when that action is currently available.

For deterministic development, use authored YAML fixtures and scenarios:

```bash
sts2 --json dev fixture load --path fixtures/basic-combat.sts2.fixture.yaml
sts2 --json dev wait-for run.currentRoom.scene --equals rooms/combat_room
sts2 --config tests/sts2.mock.yaml --json test run tests/scenarios/smoke-main-menu.sts2.yaml
```

`game install-bridge`, lifecycle commands, fixtures, and actions require an explicitly configured local game workflow. Static inspection and command discovery do not require a running game.

### Integrate An Agent, Node Tool, Or MCP Client

Install the checked-in, CLI-first agent skill into a local project:

```bash
sts2 skill install
# or choose an explicit skill root
sts2 --json skill install --path ./vendor/skills
```

To install the self-contained router directly from GitHub:

```bash
npx skills add tfoxy/spirectl --skill spirectl
```

Agents should begin with self-description, then read state before acting:

```bash
sts2 --json inspect ai-tools
sts2 --json inspect actions
sts2 --json inspect state-schema
sts2 --json state actions
```

The repo also ships the `@spirectl/sts2` Node client and the `sts2-mcp` stdio adapter. Start the local MCP adapter with:

```bash
npm --prefix npm-wrapper exec sts2-mcp
```

The adapter reuses the CLI's AI-tool catalog and preserves its structured results and errors; it is not a separate control plane. See the [AI skill guide](docs/ai-skill.md) and [npm wrapper README](npm-wrapper/README.md) for Node, MCP, and service setup.

## Work Safely

- Prefer structured `state` and `state actions` output over screen coordinates or UI scraping.
- Use semantic `act` commands with the current live action's stable IDs. In multiplayer, preserve ownership and perspective metadata before acting.
- Raw input is deliberately separated behind dangerous mode: `sts2 --mode dangerous act mouse click ...`.
- Keep game paths and mod loadouts in ignored local config. Do not silently mutate game files or shell startup files.
- Static inspection, decompilation, and asset extraction operate on your own legitimately obtained installation. Keep extracted or decompiled outputs local; do not redistribute them.

## Discover The CLI

The CLI is the authoritative command reference and exposes its catalog for both people and tools:

```bash
sts2 --help
sts2 inspect commands
sts2 inspect examples
sts2 inspect actions
sts2 --json inspect ai-tools
```

Major command areas include `state`, `act`, `game`, `dev`, `test`, `code`, `assets`, `models`, `reference`, `service`, `project`, and `skill`. Use command-specific `--help` for flags and examples; use [`docs/cli.md`](docs/cli.md) for the full CLI and output-contract reference.

## Current Boundaries

- The project remains source-first; published packaging and release automation are not the primary install story.
- Live screen and semantic-action coverage is useful but not universal. Treat current executable state and action catalogs as the source of truth for a session.
- Fixtures are recipe-backed and deliberately bounded. Use their structured diagnostics before treating a loaded fixture as a complete native-game reproduction.
- Node and MCP support are intentionally thin wrappers over the CLI, not independently versioned runtime protocols.

## Learn More

- [CLI reference](docs/cli.md)
- [Testing and fixtures](docs/testing.md)
- [AI skill and MCP integration](docs/ai-skill.md)
- [Current shipped status](docs/current-status.md)
- [Architecture](docs/architecture.md)
- [npm wrapper, Node client, and MCP adapter](npm-wrapper/README.md)

## License And Disclaimers

`spirectl` is licensed under the [Apache License 2.0](LICENSE).

`spirectl` is not affiliated with, endorsed by, or sponsored by Mega Crit. Slay the Spire and Slay the Spire 2 are trademarks of Mega Crit. Godot, Spine (Esoteric Software LLC), and FMOD (Firelight Technologies Pty Ltd) are named for identification purposes only; `spirectl` does not ship any of their code or assets. See [NOTICE](NOTICE) for full third-party attribution.

## Acknowledgements

Early exploration for `spirectl` was informed by [sts2-modding-mcp](https://github.com/elliotttate/sts2-modding-mcp), which helped demonstrate the value of AI-assisted Slay the Spire 2 modding workflows. `spirectl` uses a separate CLI-first, contract-first architecture tailored to this repository's goals.
