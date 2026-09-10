# Static Modding

Use this reference for static managed-code inspection, hook discovery, static Godot scene/resource inspection, project recovery, and hot-reload scaffold work.

## Discovery Loop

Start with checked-in workflow topics:

```bash
"${STS2_BIN[@]}" --json inspect reference-topics
```

Then use staged inspection. Prefer metadata before decompile:

```bash
"${STS2_BIN[@]}" --json code locate type CombatScreen
"${STS2_BIN[@]}" --json code describe type MegaCrit.Sts2.CardState
"${STS2_BIN[@]}" --json code refs method "MegaCrit.Sts2.CardState::CanPlay()"
"${STS2_BIN[@]}" --json code derived type MegaCrit.Sts2.BaseCard
"${STS2_BIN[@]}" --json code decompile method "MegaCrit.Sts2.CardState::CanPlay()"
```

Use `code decompile --full` only after exact metadata views are insufficient.

## Hook Discovery

```bash
"${STS2_BIN[@]}" --json code hooks OnDeckChanged
"${STS2_BIN[@]}" --json code hook-info <method-id-or-exact-signature>
```

`hooks` is advisory, fuzzy, and browseable when the query is omitted. Resolve one exact method `id` or lookup signature through `hook-info` before writing docs, patches, or Harmony hook code.

## Static Scenes And Resources

```bash
"${STS2_BIN[@]}" --json code scene-search HandPanel --resources-dir ./game-project
"${STS2_BIN[@]}" --json code scene-tree res://ui/CombatScreen.tscn --resources-dir ./game-project
"${STS2_BIN[@]}" --json code scene-node res://ui/shared/HandPanel.tscn /HandPanel/ConfirmButton --resources-dir ./game-project --assemblies-dir ./assemblies
```

Static scene commands inspect authored text, binary, or packed resources. They are separate from live runtime `dev scene tree` / `dev scene node` / `dev scene children`, and they do not prove current live visibility or legality.

If a static node exposes `attachedScriptTypeId`, pivot to `code describe`, `code hooks`, `code refs`, `code derived`, or `code decompile` only as needed.

## Roots And Ownership

Prefer explicit flags first, then configured roots from active config, then roots derived from `game.path`. Machine-specific roots belong in ignored local config such as `sts2.local.yaml`; copied local `libs/` folders are fallback reference inputs, not the default authority.

Use CLI-owned project helpers for repo-local outputs:

```bash
"${STS2_BIN[@]}" --json toolchain info
"${STS2_BIN[@]}" --json project recover --kind decompile
"${STS2_BIN[@]}" project scaffold stable-harmony-trampoline --output ./mods/MyHotMod --mod-id my-hot-mod --name "My Hot Mod" --namespace MyHotMod
"${STS2_BIN[@]}" --json project profile list
"${STS2_BIN[@]}" --json project profile run install-launch-and-attach
```

`project recover` writes managed outputs under `.sts2/toolchain/`. `project scaffold stable-harmony-trampoline` creates a downstream shell/contract/logic/test project plus local profiles for supported hot-reload workflows.
