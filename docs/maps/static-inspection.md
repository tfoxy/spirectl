# Static Inspection Map

See also: [known gaps](../known-gaps.md), [CLI](../cli.md), [architecture](../architecture.md), [testing](../testing.md).

## Primary files

- `cli/src/lib.rs`
- `cli/src/install_paths.rs`
- `dotnet-tools/src/Spirectl.DotnetTools/ToolCommandDispatcher.cs`
- `dotnet-tools/src/Spirectl.DotnetTools/Inspection/InspectionCommandService.cs`
- `dotnet-tools/src/Spirectl.DotnetTools/Inspection/DecompileExportService.cs`
- `dotnet-tools/src/Spirectl.DotnetTools/Inspection/MetadataCatalog.cs`
- `dotnet-tools/src/Spirectl.DotnetTools/Inspection/ReferenceVerificationService.cs`
- `dotnet-tools/src/Spirectl.DotnetTools/Inspection/GameAssemblySurface.cs`
- `dotnet-tools/src/Spirectl.DotnetTools/Inspection/SceneInspectionCatalog.cs`
- `dotnet-tools/src/Spirectl.DotnetTools/Inspection/GodotScriptCatalog.cs`

## Secondary files

- `dotnet-tools/tests/Spirectl.DotnetTools.Tests/ToolCommandDispatcherTests.cs`
- `cli/tests/code_commands.rs`
- `docs/ai-tools.md`

## Supported now

- Managed metadata-first flow: `code locate`, `code describe`, `code refs`, `code derived`, `code hooks`, `code hook-info`, `code decompile`
- Build-compatibility flow: `code verify-references` walks built consumer assemblies' own `TypeRef`/`MemberRef` tables and resolves every `sts2` / `GodotSharp` / `0Harmony` binding against a candidate assemblies dir, reporting missing types, members whose owner type is gone, members missing from a surviving type, and members whose rendered signature changed. It needs no source and no matching reference package, and with `--control-assemblies-dir` it refuses to report a verdict when the control build cannot resolve its own bindings.
- Managed declaration workflows: `code locate`, `code describe`, and `code derived` use declarations-only metadata for symbol lookup, exact descriptions, and inheritance/interface navigation without requiring IL body reference scanning.
- Reference-backed managed workflows: `code refs`, metadata-summary `code decompile`, `code hooks`, and `code hook-info` require the managed reference graph and may scan IL bodies to recover call/member references and hook intelligence.
- Managed assembly selection is target-focused by default: game/product assemblies and explicitly included mod assemblies are indexed, while common framework and third-party dependency DLLs are excluded unless the caller passes `--include-dependencies`.
- `code describe` is target-aware: stable type/method ids load the named assembly when available, and short exact method names use a compact candidate pass before fully loading a unique declaring assembly.
- Helper-managed metadata catalog caches live under the resolved `toolchain.sharedCacheDir` and are transparent to normal `code` command payloads. Declarations-only cache data uses a small `metadata-catalog/declarations/manifest-*.json` root manifest plus per-assembly entries under `metadata-catalog/declarations/assemblies/`; reference-backed cache entries live separately under `metadata-catalog/references/` so lightweight symbol navigation does not require or reuse IL reference graph cache entries.
- Missing, stale, corrupt, incompatible, or unreadable metadata catalog cache entries are rebuilt from the configured search roots; correctness must never depend on cache availability.
- Helper-owned corpus export flow: `decompile-export` for CLI-managed `project recover --kind decompile`
- Static Godot scene/resource flow: `code scene-search`, `code scene-tree`, `code scene-node`
- CLI-owned modding workflow catalog: `inspect reference-topics` plus `docs/modding-reference.md`
- Internal asset-helper flow for `assets extract`: helper-backed filesystem+packed asset search/read while keeping the public extraction UX in the Rust CLI
- `code scene-*` now covers supported text `.tscn` / `.escn` / `.tres`, standalone binary `.scn` / `.res`, and unencrypted `.pck` entries with additive provenance fields such as `storageKind` and `containerPath`
- `assets extract` now uses the same helper discovery authority for filesystem and packed matches, exports readable scenes/resources as offline `artifactKind: "source"` outputs, and adds extraction provenance such as `storageKind`, `containerPath`, and `resourceType`
- `assets extract-batch` reuses the same helper-backed filesystem/packed discovery and live fallback behavior as `assets extract`, but scopes outputs by manifest request id and returns aggregate/per-request JSON.
- Encounter visual packages are catalog-backed live asset surfaces, not static scene inference. `bridge-mod/src/Spirectl.Sts2/Data/base-game-encounter-visual-packages.json` is the checked-in base-game catalog, with Kaiser Crab as the first explicit mapping. The live host resolves encounter camera scale/offset through installed `EncounterModel.GetCameraScaling()` and `GetCameraOffset()` in `Sts2EncounterCameraResolver`.
- CLI resolves search roots from config/flags, then shells out to the .NET helper.
- For the default helper path, `code` commands invoke the fresh built helper DLL directly and build the default helper project only when the expected DLL is missing or stale.
- Scene/resource inspection can enrich attached scripts when assemblies expose matching Godot attributes.
- Live runtime observation is a separate dev surface through `dev scene tree`, `dev scene node`, `dev scene children`, and `dev scene set-visible`; do not fold it back into `code scene-*`. Runtime `dev scene node --properties` can expose text-bearing label diagnostics for live STS2 nodes, but that remains developer runtime inspection rather than static code scene inspection or default state.

## Source-Of-Truth Roots

- Prefer explicit command flags first.
- Otherwise use configured roots from the active config file. For machine-specific STS2 installs, the repo convention is an ignored `sts2.local.yaml` copied from `sts2.local.example.yaml`.
- If explicit assemblies/resources roots are still unset, derive them from `game.path`.
- Copied assemblies or resources from nearby mod-repo `libs/` folders are fallback-only reference inputs, not the default source of truth for static inspection or live host work.

## Local Toolchain Outputs

- CLI surfaces remain the first choice for structured lookup and JSON output: use `sts2 code ...`, `sts2 assets ...`, and `sts2 --json toolchain info` before broad filesystem browsing.
- `sts2 project recover --kind decompile|recovered-project|all` owns the repo-local outputs under the managed toolchain root. With the default root, `.sts2/toolchain/decompile/sts2` contains decompiled STS2 game sources, while `.sts2/toolchain/recovered-project` contains recovered game resources, including scenes.
- Direct browsing of those directories is appropriate for broad `rg` searches, IDE navigation, and context after CLI inspection identifies likely symbols, scenes, or resources. Treat them as generated recovery outputs, not authoritative install roots.
- `assets extract` does not silently search `.sts2/toolchain/recovered-project`; pass `--resources-dir ./.sts2/toolchain/recovered-project` when intentionally extracting from recovered files.

## Current limits

- Binary parsing is intentionally narrow: it relies on supported `PackedScene._bundled` and resource-property slices, keeps best-effort notes for malformed or unsupported files, and does not fabricate missing structure.
- `code scene-*` remains inspection-only even though `assets extract` can now materialize readable `.pck` scene/resource entries as offline `source` artifacts; keep inspection payloads and extraction artifacts as separate surfaces.
- Helper `asset-search` / `asset-read` now back both packed raster extraction and packed readable scene/resource export, but the public extraction surface still remains `sts2 assets extract`.
- Encounter package support intentionally does not infer arbitrary special visuals from scene trees. Modded/custom encounter catalogs are out of scope for current beyond unsupported notices.
- Act/encounter combat backgrounds are randomized layer stacks, not single images: the game composes a background by picking one layer per `_bg_<key>_` group plus one `_fg_`, seeded per map point. The faithful representation is therefore the whole grouped pool, exposed on `ActModelInfo.combatBackgroundLayers` (and, custom-only, on `EncounterModelInfo` when `hasCustomBackground`). Resolve the root via `model://acts/<id>/backgroundScene` and individual layers via `model://acts/<id>/backgroundLayer/<stem>` (same shape for custom-background encounters under `model://encounters/<id>/...`). The deterministic act sub-assets (`mapTopBg`, `mapMidBg`, `mapBotBg`, `restSiteBackground`, `chestSpine`) resolve 1:1 to their `res://` paths.
- `composed://combat-background/<id>/image` and `composed://encounters/<id>/background/image` are DEPRECATED pre-flattened single-image queries (they pick the alphabetically-first layer per group, which is not what the seeded game shows). Prefer the `model://` keys above; the composed keys remain only until the layer-array combat render is validated.
- `code decompile` stays metadata-based and summary-oriented by default; `--full` is the explicit exact-match ILSpy escape hatch, and broad IDE-style reconstruction remains a non-goal.
- `code hooks` stays advisory and fuzzy; use `code hook-info` before treating a result as an exact hook target.
- `code verify-references` attributes each break to a consumer assembly, not to a call site, and its signature-change bucket exists only when a control build is supplied. It reports what a shipped binary binds to, which is not the same question as what that binary's sources would compile to today.
- Managed declaration loading is intentionally lighter than reference-backed loading: `locate`, `describe`, and `derived` should not depend on IL body scans, while `refs`, metadata decompile summaries, `hooks`, and `hook-info` may depend on those scans.
- Dependency DLL visibility is explicit. Use `--include-dependencies` for broad framework or third-party dependency inspection; it does not imply `--include-mods`, and `--include-mods` does not imply dependency inspection.
- `project recover --kind decompile` now owns a broader helper-generated ILSpy corpus under the managed toolchain root, but that export path is explicit and separate from the interactive `code decompile` surface.
- Static scene/resource inspection remains separate from both managed metadata modes; do not route `code scene-*` behavior through the managed reference graph.
- Search roots must resolve from config or explicit flags before the helper can run.

## If you change static inspection, also update

- CLI command parsing/help/examples and AI-tool schemas in `cli/src/lib.rs`
- CLI-owned reference-topic catalog in `cli/src/reference_topics.rs`
- Search-root resolution rules in `cli/src/install_paths.rs`
- Helper command parsing in `ToolCommandDispatcher.cs`
- Helper command wiring in `InspectionCommandService.cs`
- Helper export orchestration in `Inspection/DecompileExportService.cs`
- Internal asset catalog/read helpers in `Inspection/AssetCatalogService.cs`
- Managed metadata indexing/navigation in `MetadataCatalog.cs`
- Reference verification in `Inspection/ReferenceVerificationService.cs`, with loader-shaped resolution in `Inspection/GameAssemblySurface.cs` and reflection-style signature rendering in `Inspection/SimpleTypeNameProvider.cs`
- Hook ranking/signature shaping in `Inspection/HookIntelligenceCatalog.cs`
- Managed metadata mode selection: keep declarations-only paths for `locate`, `describe`, and `derived`, and reserve reference-backed IL body scanning for `refs`, metadata decompile summaries, `hooks`, and `hook-info`
- Scene/resource parsing in `SceneInspectionCatalog.cs` and script enrichment in `GodotScriptCatalog.cs`
- Tests in `cli/tests/code_commands.rs`, `cli/tests/cli_snapshots.rs`, and `dotnet-tools/tests/Spirectl.DotnetTools.Tests/ToolCommandDispatcherTests.cs`
