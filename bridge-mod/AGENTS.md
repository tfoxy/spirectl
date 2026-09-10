# bridge-mod/AGENTS.md

## Purpose

The bridge mod is the authoritative runtime integration layer inside the game.

It is responsible for:

- runtime state extraction
- semantic action execution
- stable machine IDs
- multiplayer-aware perspective handling
- logs, traces, fixtures, and runtime hooks

---

## Rules

### Observable-first default state

Default state should describe what the player can reasonably know from the current screen, plus stable IDs.

### Screen-aware sparse output

Do not dump giant generic state trees when a screen-specific payload is cleaner.

### Semantic actions first

If something can be represented as a stable high-level action, prefer that over raw input.

### Validate action legality

When possible, reject invalid actions before execution with structured errors.

### Multiplayer first

State and actions must not assume a single local player model only.

### Perspective handling

Support perspective-based default views.
Dev/debug paths may expose omniscient views, but this should be explicit.

### Stable IDs

Assign stable IDs for entities for the lifetime of the run where possible.
UI element IDs may be screen-instance-scoped.

### Debug separation

Do not leak debug-only internals into normal state by accident.

### Determinism support

Fixtures, traces, and scenario setup should be implemented with testability in mind.

### Verification before finishing

If you change `bridge-mod/src/` code, or any lifecycle packaging that depends on the live host build, do not stop at unit tests or the fake-`dotnet` CLI lifecycle test.

Use the authoritative local STS2 assemblies root from ignored config or the live install, then run:

- `dotnet publish bridge-mod/src/Spirectl.BridgeMod.Sts2Host/Spirectl.BridgeMod.Sts2Host.csproj --configuration Debug --output ./.sts2/artifacts/live-bridge/host-publish -p:Sts2AssembliesDir=<authoritative sts2 assemblies dir> -p:EnableSts2LiveHost=true`
- `cargo run -p sts2 -- game install-bridge`

Prefer repo-owned validation wrappers over ad-hoc `dotnet test` for bridge checks. Use `scripts/validate.sh bridge-tests --json` for default bridge validation; it serializes MSBuild with `-m:1`, preserves structured output, performs a narrow one-shot retry after cleaning known locked bridge `obj/.../ref/*.dll` outputs, and reports socket-restricted hosts as `environment_blocked`. Use `scripts/validate.sh dotnet-format --include <bridge path> --json` for selected C# formatting. Use `scripts/validate.sh bridge-live-host-tests --filter FullyQualifiedName~MapScreenInspector --json` as the canonical live-host test target when you explicitly need the Godot-backed live-host gate; it sets `RunSts2LiveHostTests=true`, enables live references, and includes `#if ENABLE_STS2_LIVE_HOST` tests together.

`dotnet test bridge-mod/tests/Spirectl.BridgeMod.Tests/Spirectl.BridgeMod.Tests.csproj` intentionally stays on the stable non-live-host path by default, even when local STS2 assemblies are discoverable. If you bypass the wrapper, use `-p:RunSts2LiveHostTests=true` after confirming the authoritative assemblies root resolves correctly.

The checked-in Rust lifecycle test proves the CLI packaging flow, but it stubs `dotnet` and does not prove that the real bridge mod host still compiles against the current game assemblies.

## Tooling feedback loop

If a tool is missing (for example `jq`) or can be improved (including `sts2`) for better validation or analysis, record the request with `scripts/tool-improvement.sh add ...` so `.ai/tool-improvements.md` gets a real timestamp.

---

## Avoid

- exposing arbitrary internal object graphs as public schema
- using positional indices where stable IDs are possible
- multiplayer assumptions hidden inside single-player-shaped APIs
- raw input as the primary interaction path
