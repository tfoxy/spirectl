# Modding Reference

Staged CLI-first workflow for common Slay the Spire 2 modding research. This complements the existing static inspection, asset extraction, and runtime evidence surfaces without turning `sts2` into an IDE or adding a live hook-probing layer.

Use `sts2 --json inspect reference-topics` when you want the compact machine-readable catalog of the workflows below. The inspect metadata is maintained in [reference-topics.json](./reference-topics.json); this Markdown file keeps the longer guidance and should not duplicate every catalog field verbatim.

## Managed Hook Discovery

Start from managed metadata and stay staged:

```bash
sts2 code locate type CombatScreen --assemblies-dir ./assemblies
sts2 code hooks OnDeckChanged --assemblies-dir ./assemblies --resources-dir ./game-project
sts2 code hook-info method:Game:Namespace.Type::Method(System.String) --assemblies-dir ./assemblies
sts2 code describe method method:Game:Namespace.Type::Method(System.String) --assemblies-dir ./assemblies
sts2 code refs method method:Game:Namespace.Type::Method(System.String) --assemblies-dir ./assemblies
sts2 code decompile method method:Game:Namespace.Type::Method(System.String) --assemblies-dir ./assemblies --full
```

Guidance:

- `code hooks` is discovery-oriented, fuzzy, and browseable. Omit the query to list the installed static catalog, or use `--limit`, `--offset`, `--source`, `--assembly`, `--form`, `--has-script`, and `--sort` to narrow large result sets.
- returned hook candidates include stable method ids, advisory hook forms, reference counts, reasons, and optional script/scene hints.
- `code hook-info` is exact. Use a stable method id or exact lookup signature from `hooks`.
- keep `code decompile --full` as the last step for exact matches only

## Scene Script To Hook Flow

Start from a scene or node when the visual/UI structure is the clearest entry point:

```bash
sts2 code scene-search CombatScreen --resources-dir ./game-project --assemblies-dir ./assemblies
sts2 code scene-node res://ui/CombatScreen.tscn /CombatScreen --resources-dir ./game-project --assemblies-dir ./assemblies
sts2 code hooks _Ready --assemblies-dir ./assemblies --resources-dir ./game-project
sts2 code hook-info method:Game:Namespace.Type::_Ready() --assemblies-dir ./assemblies
```

Guidance:

- `code scene-*` stays static-only; live runtime trees remain `dev scene tree`, `dev scene node`, `dev scene children`, and `dev scene set-visible`
- when `attachedScriptTypeId` is present, treat it as the bridge from scene structure into managed hook research
- `hooks` can add `scriptPath` and `scenePaths` hints when assemblies and resources expose enough static metadata

## Asset To Script Trace

Use the extraction surface when you start from an image, theme resource, or packed artifact instead of a type name:

```bash
sts2 assets extract hand_icon --resources-dir ./game-project
sts2 code scene-search hand_icon --resources-dir ./game-project --assemblies-dir ./assemblies
sts2 code hooks HandPanel --assemblies-dir ./assemblies --resources-dir ./game-project
```

Guidance:

- keep `assets extract` and `code scene-*` separate: extraction writes artifacts, scene inspection returns structured metadata
- not every asset maps to one owning script, so confirm broader references before editing a hook target

## Runtime Evidence Loop

Use existing runtime/dev surfaces only as confirmatory evidence after static discovery:

```bash
sts2 code hook-info method:Game:Namespace.Type::Method(System.String) --assemblies-dir ./assemblies
sts2 dev diagnostics --bundle-dir ./.sts2/artifacts/modding
sts2 dev scene node /root/CombatScreen/HandPanel
sts2 dev screenshot --output ./.sts2/artifacts/modding/hand-panel.png
```

Guidance:

- M32 does not add live hook probing or runtime patch verification
- use diagnostics, runtime scene inspection, screenshots, and extracted assets to validate research context, not to replace exact signature resolution

## Combat System Deep Dive

Use this topic before changing combat behavior, card effects, powers, enemy turns, or piles. Combat code usually spans model data, async command APIs, current `CombatState`, and the action queue, so start from installed types instead of writing against remembered examples.

Inspect first:

```bash
sts2 code locate type DamageCmd --assemblies-dir ./assemblies
sts2 code locate type CombatState --assemblies-dir ./assemblies
sts2 code locate type CardPileCmd --assemblies-dir ./assemblies
sts2 code refs symbol ActionQueue --assemblies-dir ./assemblies
sts2 code decompile type MegaCrit.Sts2.Cards.Bash --assemblies-dir ./assemblies --full
```

Guidance:

- confirm the current command types for damage, block, power application, draw/discard/exhaust movement, energy, and creature actions before using them
- enemy intent, damage calculation, block, HP loss, and after-damage hooks are separate phases; do not patch one phase and assume the whole pipeline changed
- most combat effects are async and should preserve the game's sequencing model rather than blocking on tasks or bypassing the queue
- prefer inspecting existing first-party cards, powers, relics, and monsters with similar behavior before inventing a new control path

Wrong assumptions to avoid:

- `System.Random` or per-frame random instances are acceptable for combat behavior
- current hand indices are stable identifiers for authored behavior
- hidden combat queues, enemy AI history, or transient animation state can be recreated by ordinary fixtures
- a method name found by fuzzy search is safe to patch before `code hook-info` resolves the exact method

Validate with:

```bash
sts2 --json dev fixture load --path fixtures/basic-combat.sts2.fixture.yaml
sts2 --json state
sts2 --json dev diagnostics --bundle-dir ./.sts2/artifacts/modding/combat
sts2 --json test run tests/scenarios/smoke-main-menu.sts2.yaml
```

Use the state and diagnostics output to confirm observable behavior, logs, and exceptions. Do not add unstructured runtime memory edits to force a combat state that fixtures do not support.

## Entity Model Authoring

Use this topic before adding or modifying cards, relics, powers, potions, monsters, encounters, events, modifiers, or similar model-backed content. The agent's first job is to learn the installed model shape: base type, model ID convention, pool or registration path, localization shape, visual resource expectations, and lifecycle hooks.

Inspect first:

```bash
sts2 code locate type CardModel --assemblies-dir ./assemblies
sts2 code locate type RelicModel --assemblies-dir ./assemblies
sts2 code locate type PowerModel --assemblies-dir ./assemblies
sts2 code locate symbol ModelDb --assemblies-dir ./assemblies
sts2 code derived type CardModel --assemblies-dir ./assemblies
sts2 code decompile type MegaCrit.Sts2.Cards.Bash --assemblies-dir ./assemblies --full
```

Guidance:

- copy the shape of a nearby first-party entity before choosing overrides, constructors, IDs, pools, or localization keys
- keep model IDs stable and namespaced to the mod; avoid display names or list indices as identifiers
- cards need cost, type, target, description variables, upgrade behavior, pool membership, and localization considered together
- relics, powers, and potions usually rely on hook timing; inspect examples that trigger at the same phase
- monsters and encounters also need visual resources and act/room registration; code alone is not enough for visible content
- event and reward content should be validated against screen state and logs because unsupported UI assumptions often fail only at runtime

Wrong assumptions to avoid:

- adding a class automatically registers it in the game
- localization can be fixed after runtime validation if the model ID is unstable
- a mod with BaseLib and a raw game-API mod use the same registration attributes or namespaces
- existing instances in a running combat always update when a model class changes

Validate with:

```bash
sts2 game deploy ./mods/MyMod --build --restart --verify
sts2 --json dev logs --tail 200
sts2 --json dev log-health
sts2 --json dev diagnostics --bundle-dir ./.sts2/artifacts/modding/entity
```

For live behavior, load a fixture or scenario that reaches the screen where the entity should appear, then use `state`, screenshots, logs, and assertions rather than raw input.

## BaseLib Authoring

BaseLib is useful community infrastructure, but it is not a `spirectl` dependency and it may not be present in every target mod. Use this topic only after confirming the target project references BaseLib or installed mod assemblies expose BaseLib types.

Inspect first:

```bash
sts2 code locate type CustomCardModel --include-mods --assemblies-dir ./assemblies
sts2 code locate type CustomRelicModel --include-mods --assemblies-dir ./assemblies
sts2 code locate symbol PoolAttribute --include-mods --assemblies-dir ./assemblies
sts2 code refs symbol CustomEnum --include-mods --assemblies-dir ./assemblies
sts2 code refs symbol SpireField --include-mods --assemblies-dir ./assemblies
```

Guidance:

- confirm package version, namespaces, attributes, and examples from the target project before editing code
- BaseLib package identity and C# namespaces are not always the same string; inspect imports in working mods instead of guessing
- custom models usually need pool registration; missing pool metadata often causes content to compile but not appear
- dynamic variables, custom keywords, custom pile types, and config helpers are separate facilities; inspect each before combining them
- use BaseLib helpers when the project already owns that dependency, but do not add a hard dependency only because a reference topic mentions it

Wrong assumptions to avoid:

- `using Alchyr.Sts2.BaseLib.*` is necessarily the correct namespace
- `[Pool]`, `[CustomEnum]`, config attributes, and helper types all live in one namespace
- BaseLib can make arbitrary existing first-party model instances hot-reload in-place
- custom piles or keywords become visible without localization and UI validation

Validate with:

```bash
sts2 game deploy ./mods/MyMod --build --restart --verify
sts2 --json dev logs --tail 200
sts2 --json dev diagnostics --bundle-dir ./.sts2/artifacts/modding/baselib
```

If the first runtime failure is a model registration or missing type error, fix that first. Later errors are often cascading failures from the first invalid custom model.

## Hook Vs Harmony Decision

Use this topic before changing game behavior through patches. The right intervention is usually the narrowest one that matches the behavior: built-in model hook, BaseLib hook, semantic runtime validation, Harmony postfix/prefix, reflection helper, or finally a transpiler.

Inspect first:

```bash
sts2 code hooks "modify damage" --assemblies-dir ./assemblies --resources-dir ./game-project
sts2 code hook-info method:Game:Namespace.Type::Method(System.Int32) --assemblies-dir ./assemblies
sts2 code refs method method:Game:Namespace.Type::Method(System.Int32) --assemblies-dir ./assemblies
sts2 code describe method method:Game:Namespace.Type::Method(System.Int32) --assemblies-dir ./assemblies
sts2 code decompile method method:Game:Namespace.Type::Method(System.Int32) --assemblies-dir ./assemblies --full
```

Guidance:

- prefer model or game hooks when they exist for the timing you need
- use Harmony postfixes for observation or additive behavior after the original method succeeds
- use Harmony prefixes only when you deliberately need to change inputs, short-circuit behavior, or block execution
- use reflection only when a stable public API is missing, and cache resolved members outside hot paths
- use transpilers only when prefix/postfix hooks cannot express the change; they are the most version-sensitive option
- async methods require extra care because patching the wrapper method may not affect the state machine body

Wrong assumptions to avoid:

- a fuzzy hooks match is an exact patch target
- a private field name is stable across game updates
- blocking on async tasks is harmless in combat or room transitions
- a transpiler is easier to maintain than a small postfix plus explicit validation

Validate with:

```bash
sts2 game deploy ./mods/MyMod --build --restart --verify
sts2 --json dev diagnostics --bundle-dir ./.sts2/artifacts/modding/patch
sts2 --json dev logs --tail 200
sts2 --json test run tests/scenarios/smoke-main-menu.sts2.yaml
```

When a patch target changes after a game update, rerun `code hooks` and `code hook-info` instead of editing signatures by memory.

## Resource Loading And Godot UI

Use this topic before adding visual assets, localization files, `.tscn` scenes, or programmatic Godot UI. Keep resource lookup, scene structure, and runtime UI evidence separate so agents can tell whether a failure is packaging, loading, script registration, layout, or game-state timing.

Inspect first:

```bash
sts2 assets extract res://ui/shared/HandPanel.tscn --resources-dir ./game-project
sts2 code scene-search HandPanel --resources-dir ./game-project --assemblies-dir ./assemblies
sts2 code scene-tree res://ui/shared/HandPanel.tscn --resources-dir ./game-project --assemblies-dir ./assemblies
sts2 code scene-node res://ui/shared/HandPanel.tscn /HandPanel --resources-dir ./game-project --assemblies-dir ./assemblies
```

Guidance:

- `res://` paths refer to Godot resources visible to the running game, including mounted game or mod PCKs
- DLL embedded resources are a different mechanism; use them for managed payloads only when the target mod intentionally loads assembly resources
- `dotnet build` only builds a PCK if the mod project wires that into MSBuild targets or scripts; it is not automatic .NET behavior
- if `.tscn` files reference C# scripts from the mod assembly, the mod must register those scripts with Godot during initialization
- programmatic UI should attach to stable scene roots and clean up nodes it owns
- use static `code scene-*` for authored resource structure and `dev scene node` plus screenshots for live layout evidence

Wrong assumptions to avoid:

- copying a PNG next to a DLL makes it available at a `res://` path
- PCK filename, manifest `pck_name`, and resource path prefix can disagree
- static scene structure proves the node exists on the current live screen
- UI controls are safe to mutate from arbitrary threads or before the scene root exists

Validate with:

```bash
sts2 game deploy ./mods/MyMod --build --restart --verify
sts2 --json dev scene node /root --properties --computed-transform
sts2 --json dev screenshot --output ./.sts2/artifacts/modding/ui.png
sts2 --json dev diagnostics --bundle-dir ./.sts2/artifacts/modding/ui
```

If a scene script is not found, inspect assembly loading and script registration before rewriting the scene.

## Multiplayer And Determinism

Use this topic before writing behavior that depends on random choices, save/load, ownership, player identity, or network messages. `spirectl` treats multiplayer as first-class; mod code should not bake in single-player assumptions unless the feature is explicitly local-only.

Inspect first:

```bash
sts2 code locate type RunRngSet --assemblies-dir ./assemblies
sts2 code locate type Rng --assemblies-dir ./assemblies
sts2 code locate symbol INetMessage --assemblies-dir ./assemblies
sts2 code refs symbol ownerPlayerId --assemblies-dir ./assemblies
sts2 --json state --perspective local
sts2 --json inspect actions
```

Guidance:

- use the game's RNG streams or deterministic sub-seeds derived from run state; avoid `System.Random`
- guard random choice helpers against empty lists before rolling
- keep host/client authority explicit; local UI visibility does not imply local authority to mutate remote-owned state
- network messages should follow the installed game's serialization and transfer-mode patterns, not stale examples from older APIs
- save/load behavior matters for mod state; persist only stable data and handle missing or migrated fields
- read `ownerPlayerId`, perspective metadata, and remote-orchestration capability from `state` before assuming an action can execute

Wrong assumptions to avoid:

- one local bridge can simulate independent remote clients
- single-player smoke tests prove multiplayer ownership behavior
- a generated random value can be recomputed later unless the same RNG stream and consumption order are preserved
- gameplay state should be synchronized by mutating both clients independently

Validate with:

```bash
sts2 --json dev fixture load --path fixtures/multiplayer-ownership-local-only-degraded.sts2.fixture.yaml
sts2 --json state --perspective local
sts2 --json inspect actions
sts2 --json dev diagnostics --bundle-dir ./.sts2/artifacts/modding/multiplayer
```

If remote orchestration reports local-only degraded or unsupported, preserve that limitation in tests and docs instead of falling back to raw input.

## Mod Troubleshooting Playbook

Use this topic when a mod fails to build, load, register content, display assets, run hooks, or survive a scenario. Start from the first concrete error and map it to the next inspection command; do not fix later symptoms until the first error is understood.

First pass:

```bash
sts2 game deploy ./mods/MyMod --build --restart --verify
sts2 --json dev logs --tail 200
sts2 --json dev log-health
sts2 --json dev diagnostics --bundle-dir ./.sts2/artifacts/modding/failure
```

Common failures:

- build cannot find game assemblies: check local config and project references before editing source
- type or namespace not found: inspect target dependencies, package versions, and actual namespaces in working code
- dynamic loading or mod manifest errors: inspect the `.csproj`, manifest keys, DLL name, and deployed mod directory
- model does not appear: check registration, pool metadata, model ID, localization, and first runtime exception in logs
- hook signature mismatch: rerun `code hook-info` against installed assemblies
- Godot scene script not found: confirm mod assembly script registration and resource path casing
- PCK not loading: confirm manifest `pck_name`, actual PCK filename, resource prefix, and whether the project build creates a PCK at all
- null reference in hooks: confirm screen/combat context and owner/player availability before reading runtime singletons

Follow-up commands:

```bash
sts2 code locate symbol ModelDb --assemblies-dir ./assemblies
sts2 code hook-info AfterCardPlayed --assemblies-dir ./assemblies
sts2 code scene-search MyMod --resources-dir ./game-project --assemblies-dir ./assemblies
sts2 --json test run tests/scenarios/smoke-main-menu.sts2.yaml
```

Wrong assumptions to avoid:

- the last visible error is the root cause
- a successful build means model registration succeeded
- a deployed DLL means assets or localization were mounted
- a failed live attach should be debugged as mod code before checking bridge health and logs

Validate the fix by rerunning the same deploy/log/diagnostics sequence that exposed the failure, then add a fixture or scenario only after the mod loads cleanly.

## Embeddable Runtime For Shipped Mods

Use the in-process runtime facade when a downstream mod needs shared state/action/asset logic inside the game process without requiring players to install or run `sts2`:

```csharp
using Spirectl.Sts2.Embedding;

ISpirectlRuntime runtime = /* created by the STS2 host adapter or your mod bootstrap */;
var capabilities = runtime.GetCapabilities();
var state = runtime.GetCurrentState(new CurrentStateRequest());
```

Guidance:

- package `Spirectl.Sts2.dll` and its managed dependencies for the in-process runtime; the standalone host is a separate transport entrypoint
- resolve STS2 and Godot assemblies from the running game/mod loader context; do not copy game assemblies into downstream packages
- check `GetCapabilities()` for explicit unsupported flags before using action or asset extraction entrypoints
- see [embeddable runtime](./embeddable-runtime.md) for the stable M75 API surface and loader boundaries

## Stable Harmony Trampoline Hot Reload

Use the checked-in template when you want restart-free iteration on logic you own while keeping the native STS2 mod loader's startup-bound behavior explicit:

```bash
sts2 code hooks "combat turn" --assemblies-dir ./assemblies --resources-dir ./game-project
sts2 code hook-info method:Game:Namespace.Type::Method() --assemblies-dir ./assemblies
sts2 project scaffold stable-harmony-trampoline --output ./mods/MyHotMod --mod-id my-hot-mod --name "My Hot Mod" --namespace MyHotMod
cd ./mods/MyHotMod
cp sts2.local.example.yaml sts2.local.yaml
sts2 project profile run my-hot-mod-shell-deploy-restart
sts2 project profile run my-hot-mod-logic-build
sts2 --json dev mod-reload --project . --wait
sts2 --json dev mod-reload status --project .
```

Guidance:

- `HotMod.Shell` owns Harmony patches, long-lived game integration, the marker watcher, structured diagnostics, and the stable `HotRuntime` trampolines
- `HotMod.Contracts` stays stable in the default context and is the only assembly shared by shell and reloadable logic
- `HotMod.Logic` references contracts only, is shadow-copied into `hot-reload/.shadow/generation-<n>/`, and is loaded in a collectible context
- CLI-triggered reload uses `sts2.hot-reload.yaml` and the running shell's M57 protocol; the marker trigger remains available as a shell-owned fallback
- reload validates the manifest, initializes the new logic, swaps atomically, then disposes and unloads the old generation
- hook selection still starts with `code hooks` and `code hook-info`; `code decompile --full` is for exact targets only
- shell entry point changes, Harmony target/signature changes, incompatible contract changes, and default-context dependency changes require a game restart
- pure decision logic, calculations, logging text, generation-owned state machines, and behavior using stable host services are usually reloadable
- event subscriptions, timers, background tasks, Godot nodes, reflection caches, static fields, and delegates handed to shell/game singletons are leak-prone and should clean up in `Dispose`
- M61 adds `sts2 dev mod-reload` and status/diagnostics capture for M57/M60 shell-supported projects; M62 promotes the same bounded reload/status flow through AI/MCP, the npm wrapper, automation service tool calls, the test runner, and the Playwright `hotReloadAndWait` helper
- M63 adds optional authoring guardrails: scaffold with `--enable-guardrails` or build with `-p:EnableHotReloadGuardrails=true` for advisory `HRG001`-`HRG006` analyzer warnings and `[HotPatch]` source generation; set `HOTMOD_DEV_OVERLAY=1` only for local dev launches when an in-game reload status overlay is useful

Live smoke path:

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
sts2 --json dev mod-reload --project . --wait
sts2 --json dev mod-reload status --project .
sts2 --json dev logs --target bridge.hot_reload --tail 20
sts2 --json dev diagnostics --hot-reload-project . --bundle-dir ./.sts2/artifacts/hot-reload
```

Acceptance:

- shell deploy/restart is used for shell, Harmony patch, contract, and default-context dependency changes
- logic-only edits use the logic-build profile plus `dev mod-reload`, without a game restart
- status reports `supported: true`, matching `shellModId`, active generation, and the last reload report
- reload failures preserve the shell report, including restart-required and previous-generation-active details
- diagnostics writes `captures.hotReload` and `hot-reload.json` when `--hot-reload-project` is supplied
- no-leak generations report `previousCollected=true`; treat `reload_unload_not_collected` as a cleanup bug

## Boundaries

- `hooks` and `hook-info` are metadata-first advisory tools, not proof that a specific patching library or load order will succeed
- `inspect reference-topics` is a checked-in catalog, not dynamic documentation indexing
- broad source navigation, IDE-grade reconstruction, and hosted search remain out of scope

Downstream consumers should use the embeddable runtime for semantic state, actions, models, and asset bytes. Rendering is not shipped here: HTTP routes, sessions, auth, cache policy, WebSocket policy, browser rendering, downstream DTO envelopes, CSS, and product UX stay downstream-owned.
