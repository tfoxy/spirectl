# Embeddable Runtime

The embeddable runtime exposes in-process state, action, model, and asset APIs for downstream .NET mods. The old catalog-first presentation APIs have been removed: embedded catalog/snapshot calls, the protobuf-backed presentation command family, and bridge-side presentation layout reload are no longer part of the runtime contract.

Use `GetCurrentState(CurrentStateRequest)` for a one-shot current semantic observation, `SubscribeCurrentState(CurrentStateSubscriptionRequest, ...)` / `WatchCurrentStateAsync(CurrentStateSubscriptionRequest, ...)` for the pushed, fingerprint-deduplicated stream of it, `GetModels(ModelCatalogRequestSnapshot)` for immutable model metadata, `GetSpineCatalog(SpineCatalogRequestSnapshot)` for canonical Spine scene/node/animation entries, `ExecuteAction(EmbeddableActionRequest)` for semantic mutations, and `runtime.Assets.GetAsset(...)` / `runtime.Assets.GetAssets(...)` / `GetPresentationAssets(PresentationAssetBatchRequest)` for bytes behind opaque asset keys.

Rendering is downstream-owned. This repo ships no renderer, scene binding catalog, or presentation command group; the embeddable semantic state API is intentionally separate from downstream HTTP routes, browser sessions, auth, cache headers, WebSocket retry policy, CSS, and product DTOs.

## Runtime Shape

Embedded callers should treat current semantic state as the source of truth for observed game values and action availability. State resource references are cache hints over that semantic state: use them to fetch model payloads through `GetModels` and bytes through the asset provider. They are not a browser WebSocket envelope, not a second full state contract, and not a presentation render payload.

`GetCurrentState` is semantic-only. It does not perform presentation rendering, screenshot capture, browser session work, route/cache work, or asset extraction.

> There is no `GetState` alias and no cache-backed latest-state read. The three state members are
> `GetCurrentState`, `SubscribeCurrentState` and `WatchCurrentStateAsync`; `Embedding/RuntimePorts.cs` is the
> authoritative list.

## Focused dependencies and scene controls

Factories still return `ISpirectlRuntime` as a composition convenience. Pass `IRuntimeStateSource` to a state
observer, `ISemanticActionSource` to an action executor, and `ISpirectlAssetProvider` to an asset host. Model and
reference catalogs use separate ports, as do Spine discovery and baking.

Embedders configure scene replay through `runtime.SceneWatchControls` (`IRuntimeSceneWatchControls`):

```csharp
runtime.SceneWatchControls.SetTweenReplayEnabled(true);
runtime.SceneWatchControls.SetCardFlightReplayEnabled(true);
runtime.SceneWatchControls.SetHandTweenReplayEnabled(true);
runtime.SceneWatchControls.SetTrailReplayEnabled(false);
```

These controls retain process-wide scope and existing defaults. Tween replay changes transform and opacity
suppression together. Viewer unanimity and browser settings policy belong to the embedding application.
Check the `scene-watch-controls` capability before applying controls; an explicitly unsupported provider throws
`NotSupportedException`. Do not mutate producer settings directly from a host.

`SpirectlRuntimeFacade` is composed from an internal `SpirectlRuntimeServices` boundary containing only reusable
embedded ports. `BridgeRuntime`, fixture/scenario/debug/lifecycle/transport composition, and their fallbacks live
in `Spirectl.BridgeMod`; STS2/Godot-backed bridge-only implementations live in
`Spirectl.BridgeMod.Sts2Host`. Runtime owners dispose their facade or bridge lifetime to release subscriptions
and scene hooks.

## Compile profiles

`Spirectl.Sts2` compiles as one of two profiles, chosen by the `Sts2Profile` MSBuild property. The default is
`Full`, set inside the project, so the bridge, the CLI, the NuGet package and the tests get everything unless they
ask otherwise.

An embedder that runs the runtime in-process selects `Embedded` from its own project reference
(`AdditionalProperties="Sts2Profile=Embedded;EnableSts2LiveHost=true;..."`). That profile leaves out code the
embedder cannot reach, and does not construct or install it either:

- the legacy state-extractor lane, the full semantic-state provider and subscription hub, their builders,
  projections and state-only screen/overlay inspectors, and the reference-data provider implementation (the
  `Core/Reference` DTOs and the reference port stay, because `ISpirectlRuntime` inherits them). The extractor
  port (`IGameStateExtractor`), its placeholder and scaffold are out too: the profile's `SpirectlRuntimeServices`
  has no extractor slot, and `Sts2EmbeddableRuntimeFactory` has no overload that takes one. So are the snapshot
  types only that port carried (`GameStateSnapshot` and the DTOs only it names): `Core/State/GameStateSnapshot.cs`
  keeps the types some retained file still names, and `GameStateSnapshot.Full.cs` holds the rest, verbatim;
- the host-local seat watchers and the VFX-spawn hook;
- the combat-preview helper, damage and card-upgrade observation hooks and their combat-event hub. The end-turn readiness hook remains
  installed because it changes combat behavior; only its semantic-state revision bump is omitted;
- the fixture-only main-menu and net-host helpers from both game-API lanes. Their manifest requirements are
  already full-profile-only;
- the action bodies no embedded dispatch route reaches. The embedded dispatcher
  (`Profiles/Embedded/Sts2ActionHandler.Dispatch.cs`) routes only the semantic action kinds an embedder sends and
  answers `InvalidAction` for the rest; the `SemanticActionKind` enum itself stays complete, so its wire values do
  not move. Members of a mixed area that no route reaches live in a matching `*.Full.cs` partial (the action
  handler's, and `Sts2ActionCatalog`'s, whose state-side action builders only the legacy lane called);
- the `claim-reward` action body, reward screen inspector, reward capture registry and capture hook. Browser
  clients use the game's input path for reward selection. Full keeps `claim-reward` for CLI and bridge callers;
- the full action-descriptor catalog (`Common/Sts2ActionDescriptorCatalog.cs`), which words every kind the bridge
  and the CLI know. The embedded runtime's `Capabilities.SupportedActions` is the dispatcher's own route table
  (each route carries its descriptor), so it lists exactly the kinds the dispatcher carries out and cannot drift
  from it;
- the game-API manifest requirements that only those omitted lanes read, including the state-only lobby cap,
  saved-run roster, potion permission and damage-preview funnel (the lane manifests mark them with
  `#if !SPIRECTL_PROFILE_EMBEDDED`), so a game update that moves such a member cannot stop an embedder from starting.

The omitted files are listed once, in the "Embedded profile" item group of
`bridge-mod/src/Spirectl.Sts2/Spirectl.Sts2.csproj`, next to the stand-ins under `Profiles/Embedded/`.
`Sts2CompileProfileTests` pins that list, the dispatcher's kinds, the install list and the manifest partition. To
add an embedded action kind, add its route (kind, descriptor and body) to the embedded dispatcher's table and, if
its body sits in a `*.Full.cs` partial, move it back into the main partial. The profile is stamped into the assembly as `SpirectlSts2Profile`
metadata. Compile it with the game assemblies present:
`dotnet build bridge-mod/src/Spirectl.Sts2/Spirectl.Sts2.csproj -p:EnableSts2LiveHost=true -p:Sts2Profile=Embedded`.

The Embedded profile does not expose current semantic state or combat-event subscriptions; the Full profile
continues to expose both.

## Current-state subscription pacing

`Full` profile only.

`SubscribeCurrentState` / `WatchCurrentStateAsync` push an event only when the snapshot's semantic fingerprint
changes. Finding that out means re-walking state on the **game main thread**, so the hub paces the walk: it backs
off while nothing changes and never lets a change arrive later than the idle ceiling.

| `CurrentStateSubscriptionRequest` field | Default | Meaning |
| --- | --- | --- |
| `MinCaptureInterval` | 50 ms | **Floor.** The fastest this subscription ever re-walks state. |
| `MaxIdleInterval` | 400 ms | **Ceiling** the interval doubles toward while the fingerprint is unchanged. Clamped into `[MinCaptureInterval, 400 ms]`. |
| `IdleBackoff` | `true` | Set `false` to keep floor pacing. |

- Each unchanged capture doubles the interval (50 → 100 → 200 → 400 ms); it snaps back to the floor when the
  fingerprint changes, an accepted action forces a refresh, or the semantic revision moves.
- **The 400 ms ceiling is hard.** `Sts2StateWatchRuntimeSettings.MaxIdleCeilingMs` clamps the env var, the request
  field and a live embedder write alike.
- `Sts2SemanticStateRevision` is an accelerator, never a gate: hooks the bridge already installs bump it, which
  collapses the backoff early. Coverage is partial on purpose; polling alone still finds every change.
- Delivery per subscriber is **sequential**, in emission order, so a consumer keeping a "latest snapshot" may assign
  it unconditionally.

### Process levers (`Sts2StateWatchRuntimeSettings`)

Public statics, each seeded from an environment variable at load and writable at run time by an embedder. Scene
replay settings instead use the typed controls above:

| Env var | Default | Static |
| --- | --- | --- |
| `SPIRECTL_STATE_WATCH_IDLE_BACKOFF` | on | `IdleBackoff` — off restores floor pacing for every subscription |
| `SPIRECTL_STATE_WATCH_MAX_IDLE_MS` | `400` | `MaxIdleInterval` — clamped to the 400 ms ceiling |
| `SPIRECTL_STATE_WATCH_REVISION_WAKE` | on | `RevisionWake` |
| `SPIRECTL_STATE_WATCH_PROFILE` | off | `Profile` — accumulates the bridge's `state-watch` profile |

The process lever can only ever turn backoff **off**; a subscription that passed `IdleBackoff = false` is never
overridden into backing off.

### Fingerprints

`CurrentStateWatchEvent.SemanticFingerprint` is opaque and only ever compared for equality. It is an `xxh64:`-prefixed
streaming hash of the snapshot's UTF-8 JSON (`Embedding/EmbeddableStateFingerprint.cs`). Do not persist it across
bridge versions or treat it as a content address.

## Asset seam: scenes and localization

`runtime.Assets.GetAsset(EmbeddableAssetRequest{ Key, Format })` (and the batch
`runtime.Assets.GetAssets(...)`) is the single canonical consumer asset API. It returns bytes
behind an opaque key. Beyond rendered images it also serves the two inputs a browser renderer
needs directly, so an HTTP host can proxy `/res/<res://…>` straight to the seam without
re-implementing scene/localization serving.

**Combat-background render size + explicit layer selection (additive request fields).**
`EmbeddableAssetRequest` carries three trailing optional fields honored by the combat-background
scene renders only (current); every other render mode ignores them and stays byte-identical:

- `RenderWidth` / `RenderHeight` (`int?`): the requested capture viewport in pixels. When both are
  positive, the literal `res://scenes/backgrounds/<id>/<id>_background.tscn` render and the
  composed `composed://combat-background/<id>/image` alias re-center their runtime BgContainer
  framing (centered, x-offset 23, scale 0.9) on that viewport instead of the live root viewport —
  e.g. `2520x1080` for a widescreen mirror-stage capture. Absent/non-positive = the live root
  viewport size, exactly today's output.
- `CompositionSelector` (`string?`, composed alias only): a comma-separated, ordered list of the
  `res://` layer scene paths to inject (the layer variants actually mounted in the live room; the
  `<id>_bg_NN_*` / `<id>_fg_*` filename convention maps each path to its root placeholder). Absent
  = today's deterministic-first-sorted discovery, byte-identical — that is also the caller's
  fallback. A malformed selector or a selection the render cannot honor (unrecognized layer
  filename, no matching placeholder, competing variants for one placeholder, unloadable scene)
  returns a clean failure result so the caller can fall back; it never crashes and never silently
  renders a different layer set than requested.

> **BREAKING:** `ISpirectlRuntime.ExtractAsset(AssetExtractRequestSnapshot)` has been removed
> from the embeddable interface. It was a thin pass-through to the low-level extraction
> primitive that takes a **pre-resolved** `{ SourceRoot, SourcePath, LoadPath, OutputFormat }`
> request — paths an embedding host (which holds opaque keys) cannot produce. Consumers use
> `runtime.Assets.GetAsset(key, format)`, which resolves the key and then runs extraction. The
> low-level primitive (`BridgeRuntime.ExtractAsset`) stays internal to the asset providers and
> the gRPC `ExtractAsset` RPC, both of which legitimately produce resolved requests.

**Scene structure (`GodotSceneState` JSON).** A raw `res://…/foo.tscn` (or `.scn`) returns a
faithful godot-scene-web `GodotSceneState` JSON document for the default/auto/`structure`
formats:

```json
{ "kind": "scene",
  "nodes": [ { "index": 0, "name": "Root", "type": "Control",
               "properties": [ { "name": "visible", "value": true } ] },
             { "index": 1, "name": "Icon", "type": "TextureRect", "parent": ".",
               "instance": { "type": "ExtResource", "path": "res://scenes/icon.tscn" },
               "properties": [ { "name": "position", "value": { "type": "Vector2", "args": [1, 2] } },
                               { "name": "texture",  "value": { "type": "ExtResource", "path": "res://art/icon.png" } } ] } ],
  "connections": [], "extResources": [], "subResources": [], "editableInstances": [],
  "basePath": "res://…/foo.tscn", "diagnostics": [] }
```

Property values use gsw's engine-native encoding: raw scalars, `{ type, args }` for math/engine
Variants (`Vector2`, `Color`, `Rect2`, …), and path-first `{ type:"ExtResource", path }` for
resource refs; parent paths are bare (no `./` prefix), the scene root's parent is omitted. The
payload's `ContentType` is `application/json`.

> **BREAKING:** `.tscn`/`.scn` + `format=auto` previously rendered a **PNG**. Scene structure
> is now the default; request an explicit raster format (`format=png`/`webp`/…) to still render
> the scene to an image. Semantic visual/background/encounter/texture keys (e.g.
> `model://characters/…/visuals`, `composed://…`) are unaffected — they resolve to their own
> render paths before the generic scene branch.

**Localization tables.** A `res://localization/<lang>/<table>.json` key returns the raw table
JSON (read via `Godot.FileAccess`), `ContentType` `application/json`. This is the unmerged,
path-faithful file; the merged base+mod+`eng`-fallback view stays on the protobuf
`InspectPresentationLocalization` RPC.

The scene producer (`Sts2GodotSceneStateProducer`) is intentionally separate from the curated,
semantically-typed `Sts2PresentationResourceSceneInspector` RPC model — that contract is
unchanged.

## Bridge-owned profiling

Producer/state-watch profiling and `perf-report/1` are bridge diagnostics owned by `Spirectl.BridgeMod` and are not part of `ISpirectlRuntime`. Embedders must not depend on bridge diagnostic aggregates; profile a live host with the bridge's diagnostics (`scripts/validate.sh producer-walk-profile`, see [testing](testing.md)).

## Capabilities

Capabilities should be checked before relying on optional runtime behavior. The removed legacy presentation capabilities are no longer advertised. Asset-provider capabilities remain available through the runtime asset surface.

`spine-catalog` enumerates canonical `(scene, node, animation)` entries for scene-based SpineSprite nodes. It spans everything reachable via `res://`, including mounted mod content, and tracks the same support flag as `asset-extraction`.

## Boundaries

Keep presentation rendering, screenshots, asset extraction, browser sessions, product DTOs, routes, cache headers, WebSocket retry policy, auth, and fallback UI separate from the current semantic state contract. Downstream products own their HTTP/static/browser implementation and cache policy.
