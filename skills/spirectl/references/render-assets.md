# Assets And Visual Evidence

Use this reference for asset keys and extraction, screenshot evidence, viewport presets, and visual comparisons.

`spirectl` does not render screens. There is no `presentation` command group, no scene binding catalog, and no
bundled renderer: semantic `state` is the source of truth for game facts and action legality, live Godot scene
state carries the renderable tree, and the asset surface below serves bytes behind opaque keys. Layout, DOM/canvas
output, browser routes, sessions, auth, cache policy, CSS, fallback art policy, and visual thresholds are all
downstream-owned.

## Asset Keys And Extraction

Asset refs are opaque keys, not URLs. They are typed `model://` / `composed://` virtual keys or direct `res://...` resources:

- `model://cards/<model-id>/image`
- `model://relics/<model-id>/icon`
- `model://potions/<model-id>/icon`
- `model://characters/<model-id>/icon`
- `model://characters/<model-id>/characterSelectIcon`
- `model://characters/<model-id>/characterSelectLockedIcon`
- `model://characters/<model-id>/characterSelectBg`
- `model://characters/<model-id>/visuals`
- `model://monsters/<model-id>/visuals`
- `model://events/<event-id>/backgroundScene`
- `model://events/<event-id>/initialPortrait`
- `composed://combat-background/<id>/image`
- `composed://encounters/<encounter-id>/scene-package`
- `composed://encounters/<encounter-id>/background/image`
- `composed://encounters/<encounter-id>/visual-state/<state-id>/overlay/image`
- `composed://encounters/<encounter-id>/visual-part/<part-id>/state/<state-id>/image`
- direct `res://...` keys for fixed UI, status, intent, VFX, font, and authored scene/resource assets

Use extraction for artifacts and explain for diagnostics:

```bash
"${STS2_BIN[@]}" --json assets extract hand --execution offline --resources-dir ./game-project
"${STS2_BIN[@]}" --json assets extract res://fonts/title.woff2 --execution offline --resources-dir ./game-project --format auto
"${STS2_BIN[@]}" --json assets explain composed://combat-background/overgrowth/image --execution live
"${STS2_BIN[@]}" --json assets explain composed://encounters/kaiser_crab_boss/scene-package --execution live
"${STS2_BIN[@]}" --json assets extract-batch --manifest ./asset-manifest.json --output ./.sts2/artifacts/assets --execution auto --format png
```

`assets extract` can write offline filesystem/packed outputs and live virtual renders. `assets explain` writes no artifacts and should be used before changing composed background or encounter package behavior. `assets extract-batch` repeats the same resolver/exporter for manifest requests and preserves per-request notices, provenance, render diagnostics, and partial failures.
Font files (`.ttf`, `.otf`, `.woff`, `.woff2`) and direct Godot font resources export as raw bytes with `artifactKind: "font"` and `font/*` content types when the live/resource provider can resolve reusable browser bytes. Use `--format auto`; raster formats such as `png` or `webp` are usage errors for fonts. Font refs are asset keys, not downstream HTTP route or CSS policy.

Unsupported encounters, missing selectors, transparent-heavy renders, or live-host failures must stay structured. Do not fabricate scene packages or commit official game assets to docs, examples, snapshots, or skill packaging.

## Screenshots And Visual Evidence

Screenshots are evidence, not the primary runtime API:

```bash
"${STS2_BIN[@]}" --json inspect viewport-presets
"${STS2_BIN[@]}" --json dev screenshot --preset desktop-1080p --output ./.sts2/artifacts/runtime.png
"${STS2_BIN[@]}" --json dev screenshot-diff --baseline tests/scenarios/baselines/mock-main-menu.png --bundle-dir ./.sts2/artifacts/diff
"${STS2_BIN[@]}" --json dev screenshot-diff --baseline baseline.png --actual actual.png --bundle-dir ./.sts2/artifacts/offline-diff
```

Use presets or explicit width/height for repeatable capture. `screenshot-diff` supports live capture when `--actual` is omitted and offline image-to-image comparison when `--actual` is supplied. Preserve `comparison.json`, `baseline.png`, `actual.png`, and `diff.png` bundle paths in automation outputs.

Snapshot export/compare is for authored visual contracts:

```bash
"${STS2_BIN[@]}" --json dev snapshot export --spec tests/snapshots/main-menu.sts2.snapshot.yaml --output ./.sts2/artifacts/snapshots
"${STS2_BIN[@]}" --json dev snapshot compare --spec tests/snapshots/main-menu.sts2.snapshot.yaml --baseline tests/snapshots/baselines/main-menu
```
