---
name: spirectl-bridge
description: Work on the .NET bridge mod and host — bridge-mod/, the embedded runtime, the scene-state producers and wire contract, live-host paths, and the validate.sh legs that cover them. Use for C# changes in this repo, for changes to what the browser mirror receives or to producer walk cost, and for anything about deploying the bridge into a game install.
---

# Bridge mod and .NET host

Read [docs/agent-quickstart.md](../docs/agent-quickstart.md) first — it is the context-budgeted entrypoint and it
routes into `docs/maps/*.md`. Load one map, not all of them.

## Commands

```bash
scripts/validate.sh bridge-build                 # dotnet build, serial
scripts/validate.sh bridge-tests                 # RUN THIS LEG ALONE
scripts/validate.sh bridge-live-host-tests       # only when you need live-host-gated coverage
scripts/validate.sh dotnet-format --include <path>
```

Toolchain comes from mise (`rust stable`, `dotnet 10.0.105`, `node 24.12.0`, `pnpm 10.26.0`); prefix with
`mise exec --` when the pinned versions matter. The README's ".NET 9 SDK" line is stale relative to `mise.toml`.

## Things that look green without having run

- **Serial MSBuild is mandatory.** Every dotnet invocation is `-m:1`; parallel builds race here (MSB3030). The
  PreToolUse guard blocks an un-serialized `dotnet build|test`.
- **`bridge-tests` runs alone.** Alongside other validate legs it hits the same race.
- **Live-host tests are compile-gated** behind `RunSts2LiveHostTests=true` / `#if ENABLE_STS2_LIVE_HOST`. Without
  the property they do not run — and a suite that never executed looks exactly like a suite that passed.
- A live bridge is assumed available: prefer validating against a running host over mocking it, and bring one up
  rather than treating its absence as a blocker (`./scripts/build-fixture.sh <fixture>`, then
  `cargo run -p sts2 -- --json game bridge-health`).
- `.githooks/pre-commit` runs `cargo fmt` on staged Rust and **fails the commit** without re-staging for you.

## You have a downstream consumer

`../sts2-couch-coop` embeds this bridge, and the two are **separate assemblies**. Deploying to a game install for
couch-coop QA means both:

```bash
sts2 game install-bridge          # the bridge/CLI-facing copy
# and, in ../sts2-couch-coop:
scripts/build-local-mod.sh        # the embedded copy the running game + browser mirror actually serve
```

`install-bridge` alone does **not** update what the browser mirror sees — a QA leg run after only half a deploy
measures the old code. See `AGENTS.md` → Downstream Consumers.

## Producer walk cost is someone's frame budget

The scene-state producer walk is the dominant term in couch-coop's headless host CPU. Before optimizing,
profile it; after, re-profile and report the envelope rather than a wall-clock impression. A measurement
without its arms, repeats and window stated is not a result.

```bash
scripts/validate.sh producer-walk-profile --log <path>
```

## Contracts you are co-owner of, not sole owner

- The scene-state DTOs in `bridge-mod/src/Spirectl.Sts2/Live/Sts2GodotSceneStateModels.cs` **mirror
  `../godot-scene-web/packages/core/src/index.ts` exactly**. A field added on one side and not the other is a
  silent renderer gap, not a type error — the consumer resolves these through ambient declarations, not built
  types. `Sts2GodotSceneStateProducerTests.cs` asserts the contract; keep it doing so.
- The `perf-report/1` envelope is shared with godot-scene-web's browser harness and with couch-coop's
  measurements. Its shape is documented in `../godot-scene-web/docs/perf-report-contract.md`.
- Protobuf evolves additively. Never reuse a field number.

## Boundaries

Reusable STS2 behavior belongs here; CouchCoop product behavior does not. Observable/screen-aware state, semantic
actions over raw input, stable machine ids over indices, `--json` plus exit codes on major commands, protobuf
evolves additively. Never silently mutate game files.

If local tooling blocked your validation, `scripts/tool-improvement.sh add …` is **required** before claiming
completion, and must be mentioned in your final answer.

## Artifact policy

No decompiled or transcribed game source, no quoted method bodies, no private field/method names, no narrative
reconstruction of a game class's logic — not even in a comment. Resource paths and node names the shipped code
needs are allowed (`bridge-mod/src/Spirectl.Sts2/Data/base-game-encounter-visual-packages.json` is the
precedent). Everything else goes to `.sts2/research/`, at the real checkout path — a worktree gets its own
empty `.sts2/`.
