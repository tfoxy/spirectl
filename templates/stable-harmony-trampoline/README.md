# Stable Harmony Trampoline Template

This template is the supported M57 shape for restart-free iteration on developer-owned STS2 mod logic.

## Architecture

- `HotMod.Shell` is the non-reloadable mod shell. It owns Harmony patch methods, long-lived game integration, structured reload diagnostics, and the optional dev file watcher.
- `HotMod.Contracts` is the stable default-context contract assembly. Keep it small and versioned.
- `HotMod.Logic` is the reloadable assembly. It references contracts only and is loaded into a collectible `AssemblyLoadContext`.
- Harmony patches live only in `HotMod.Shell.HarmonyPatches` and delegate through `HotRuntime` trampolines.
- Each reload shadow-copies the logic assembly to `hot-reload/.shadow/generation-<n>/`, validates the manifest, initializes the new generation, swaps atomically, then disposes and unloads the old generation.

## Local Workflow

```bash
dotnet build templates/stable-harmony-trampoline/src/HotMod.Shell/HotMod.Shell.csproj
sts2 game deploy templates/stable-harmony-trampoline/src/HotMod.Shell --build --restart --verify
HOTMOD_HOT_RELOAD=1 dotnet build templates/stable-harmony-trampoline/src/HotMod.Logic/HotMod.Logic.csproj
touch <deployed-mod>/hot-reload/reload.marker
```

By default the shell watches `<shell base directory>/hot-reload/reload.marker` and loads `<shell base directory>/hot-reload/HotMod.Logic.dll`.

Environment overrides:

- `HOTMOD_HOT_RELOAD=1` or `true` enables the marker watcher.
- `HOTMOD_DEV_OVERLAY=1` or `true` enables the dev-only reload status overlay.
- `HOTMOD_LOGIC_PATH` points at the logic DLL.
- `HOTMOD_RELOAD_MARKER` points at the marker file.
- `HOTMOD_ENTRY_TYPE` names the `IHotLogic` implementation when implicit discovery is ambiguous.
- `HOTMOD_SHADOW_ROOT` overrides the shadow-copy directory.

## Optional Authoring Guardrails

Guardrails are advisory and opt-in. Enable them for local builds with:

```bash
dotnet build -p:EnableHotReloadGuardrails=true
```

Generated scaffolds can also start enabled:

```bash
sts2 project scaffold stable-harmony-trampoline --output ./mods/MyHotMod --mod-id my-hot-mod --name "My Hot Mod" --namespace MyHotMod --enable-guardrails
```

The analyzer reports `HRG001`-`HRG006` as warnings by default. Promote selected diagnostics with normal MSBuild configuration, for example:

```xml
<WarningsAsErrors>HRG001;HRG002</WarningsAsErrors>
```

Patch boilerplate can be generated from `[HotPatch]` declarations in the shell project. Generated patches still live in the shell build and delegate through `HotRuntime.Current.DispatchHook(...)`; reloadable logic must not own Harmony patch classes.

The live overlay is dev-only and disabled by default. Set `HOTMOD_DEV_OVERLAY=1` for a local launch to show shell id, active generation, last reload status, duration, unload collection state, and restart-required state. The overlay is informational only; structured logs and `sts2 dev mod-reload status` remain authoritative.

## STS2 Assemblies

The template builds without STS2 installed. For live patch compilation, configure the game assemblies with one of:

- ignored local config such as `sts2.local.yaml`
- `dotnet build -p:Sts2AssembliesDir=/path/to/assemblies`
- `STS2_ASSEMBLIES_DIR=/path/to/assemblies`

When `sts2.dll` and `GodotSharp.dll` exist in that directory, `HotMod.Shell` defines `HOTMOD_STS2` and compiles the real patch example.

## Restart Boundaries

Restart required:

- shell entry point changes
- Harmony target changes
- patch class or signature changes
- incompatible contract changes
- default-context dependency changes
- long-lived Godot resource changes without a reload protocol

Usually reloadable:

- pure decision logic
- calculations
- logging text
- generation-owned state machines
- behavior using stable host services only

Leak-prone:

- event subscriptions
- background tasks
- timers
- Godot nodes
- reflection caches
- static fields
- delegates handed to game or shell singletons

Leak checklist:

- unsubscribe from every event in `Dispose`
- cancel and await background work owned by the generation
- dispose timers and cancellation tokens
- clear static caches or keep them out of `HotMod.Logic`
- never store reloadable delegates in shell singletons without an unregister path
- treat `reload_unload_not_collected` as a real leak until proven otherwise

## Live Smoke

1. Configure `sts2.local.yaml` or `STS2_ASSEMBLIES_DIR`.
2. Build `HotMod.Shell`.
3. Deploy and restart with `sts2 game deploy templates/stable-harmony-trampoline/src/HotMod.Shell --build --restart --verify`.
4. Start or attach to the game.
5. Verify shell initialization in `sts2 dev logs`.
6. Build logic generation 1 and copy `HotMod.Logic.dll` into the deployed shell `hot-reload/` directory.
7. Request reload with `sts2 --json dev mod-reload --project <scaffold-root> --wait`, or touch `hot-reload/reload.marker` when testing the shell fallback directly.
8. Observe a generation 1 `loaded` report.
9. Change the logic log text.
10. Build logic generation 2 and copy it over the deployed `HotMod.Logic.dll`.
11. Request reload again.
12. Observe generation 2 without restarting.
13. Confirm no-leak generations report `previousCollected=true`.
