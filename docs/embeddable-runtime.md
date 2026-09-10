# Embeddable Runtime

The embeddable runtime exposes in-process state, action, model, and asset APIs for downstream .NET mods. The old catalog-first presentation APIs have been removed: embedded catalog/snapshot calls, the protobuf-backed presentation command family, and bridge-side presentation layout reload are no longer part of the runtime contract.

Use `GetCurrentState(CurrentStateRequest)` for a one-shot current semantic observation, `SubscribeCurrentState(CurrentStateSubscriptionRequest, ...)` / `WatchCurrentStateAsync(CurrentStateSubscriptionRequest, ...)` for the pushed, fingerprint-deduplicated stream of it, `GetModels(ModelCatalogRequestSnapshot)` for immutable model metadata, `GetSpineCatalog(SpineCatalogRequestSnapshot)` for canonical Spine scene/node/animation entries, `ExecuteAction(EmbeddableActionRequest)` for semantic mutations, and `runtime.Assets.GetAsset(...)` / `runtime.Assets.GetAssets(...)` / `GetPresentationAssets(PresentationAssetBatchRequest)` for bytes behind opaque asset keys.

Rendering is downstream-owned. This repo ships no renderer, scene binding catalog, or presentation command group; the embeddable semantic state API is intentionally separate from downstream HTTP routes, browser sessions, auth, cache headers, WebSocket retry policy, CSS, and product DTOs.

## Runtime Shape

Embedded callers should treat current semantic state as the source of truth for observed game values and action availability. State resource references are cache hints over that semantic state: use them to fetch model payloads through `GetModels` and bytes through the asset provider. They are not a browser WebSocket envelope, not a second full state contract, and not a presentation render payload.

`GetCurrentState` is semantic-only. It does not perform presentation rendering, screenshot capture, browser session work, route/cache work, or asset extraction.

> There is **no** `GetState(EmbeddableStateRequest)` alias and **no** `GetLatestState(EmbeddableLatestStateRequest)`
> cache-backed read. This document promised both for several rounds; neither was ever implemented (the
> latest-state cache remains unbuilt). The three members that exist are `GetCurrentState`,
> `SubscribeCurrentState` and `WatchCurrentStateAsync` — read `Embedding/RuntimePorts.cs`, not this
> paragraph's history, if a name here ever fails to resolve.

## Focused dependencies and scene controls

Factories still return `ISpirectlRuntime` as a composition convenience. Pass `IRuntimeStateSource` to a state
observer, `ISemanticActionSource` to an action executor, and `ISpirectlAssetProvider` to an asset host. Model and
reference catalogs use separate ports, as do Spine discovery and baking. Request/result types are unchanged.
See [the design review](solid-design-review.md) for the full export and consumer inventory.

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

## Current-state subscription pacing

`SubscribeCurrentState` / `WatchCurrentStateAsync` push an event only when the snapshot's semantic fingerprint
changes. What they cost is decided by how often the hub re-walks state to find that out — and that walk runs on
the **game main thread** (the state provider marshals itself there), so a capture nobody needed is frame budget
spent. Three levers pace it, and the defaults are chosen so an idle host is cheap without a change ever arriving
late.

| `CurrentStateSubscriptionRequest` field | Default | Meaning |
| --- | --- | --- |
| `MinCaptureInterval` | 50 ms | **Floor.** The fastest this subscription ever re-walks state. |
| `MaxIdleInterval` | 400 ms | **Ceiling** the interval doubles toward while the fingerprint is unchanged. Clamped into `[MinCaptureInterval, 400 ms]`. |
| `IdleBackoff` | `true` | Set `false` to keep floor pacing forever — exactly the pre-backoff pacing. |

The ramp: each capture whose fingerprint matched the last one doubles the interval (50 → 100 → 200 → 400 → 400…);
the interval snaps back to the floor the moment the fingerprint changes, an accepted action forces a refresh, or
the semantic revision moves. A tick that finds nothing due returns immediately — it does not schedule work.

**The 400 ms ceiling is hard.** `Sts2StateWatchRuntimeSettings.MaxIdleCeilingMs` clamps the env var, the request
field and a live embedder write alike. Backoff is allowed to make an idle host cheap; it is not allowed to make a
change arrive late, and 400 ms is the budget the "never miss a change" guarantee is written against.

`Sts2SemanticStateRevision` is an **accelerator on top of that guarantee, never a gate.** Game-thread hooks the
bridge already installs bump a counter when something they observe has changed; the hub notices on the next tick
and collapses the idle backoff, so a covered change is picked up at the floor interval instead of after the idle
wait. Coverage is partial on purpose — plenty of state moves with no hook attached — and nothing anywhere waits
for a bump. With the wake disabled the hub still finds every change by polling.

Event delivery per subscriber is **sequential**: events reach the callback in emission order. A consumer that
keeps a "latest snapshot" may assign it unconditionally.

### Process levers (`Sts2StateWatchRuntimeSettings`)

Public statics, each seeded from an environment variable at load and writable at run time by an embedder (a
browser settings panel, an A/B harness). Scene replay settings instead use the typed controls above:

| Env var | Default | Static |
| --- | --- | --- |
| `SPIRECTL_STATE_WATCH_IDLE_BACKOFF` | on | `IdleBackoff` — off restores floor pacing for every subscription |
| `SPIRECTL_STATE_WATCH_MAX_IDLE_MS` | `400` | `MaxIdleInterval` — clamped to the 400 ms ceiling |
| `SPIRECTL_STATE_WATCH_REVISION_WAKE` | on | `RevisionWake` |
| `SPIRECTL_STATE_WATCH_PROFILE` | off | `Profile` — accumulates the `state-watch` profile below |

The process lever can only ever turn backoff **off**; a subscription that passed `IdleBackoff = false` is never
overridden into backing off.

### Fingerprints

`CurrentStateWatchEvent.SemanticFingerprint` is opaque and only ever compared for equality. It is now a streaming
XXH64 over the snapshot's UTF-8 JSON (`Embedding/EmbeddableStateFingerprint.cs`) rather than a serialize-to-string
plus SHA-256, and carries an `xxh64:` prefix so a value logged before the change cannot be confused with one after
it. Do not persist it across bridge versions or treat it as a content address.

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

Producer/state-watch profiling and `perf-report/1` are bridge diagnostics owned by `Spirectl.BridgeMod`; they are deliberately absent from `ISpirectlRuntime`. Use the standalone profiling tool and bridge diagnostic services when profiling a live host. Embedders retain their focused state/scene/asset/action ports and must not take a dependency on bridge diagnostic aggregates.

## Capabilities

Capabilities should be checked before relying on optional runtime behavior. The removed legacy presentation capabilities are no longer advertised. Asset-provider capabilities remain available through the runtime asset surface.

`spine-catalog` enumerates canonical `(scene, node, animation)` entries for scene-based SpineSprite nodes. It spans everything reachable via `res://`, including mounted mod content, and tracks the same support flag as `asset-extraction`.

## Boundaries

Keep presentation rendering, screenshots, asset extraction, browser sessions, product DTOs, routes, cache headers, WebSocket retry policy, auth, and fallback UI separate from the current semantic state contract. Downstream products own their HTTP/static/browser implementation and cache policy.
