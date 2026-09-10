# @spirectl/presentation

The shared **STS2 render vocabulary** for browser hosts, as a browser-targeted TypeScript library.
It is deliberately small: a handful of leaf modules that a host reusing STS2 conventions
(decorative animations, the custom BBCode tag table, the play-zone predicate) would otherwise have
to re-derive.

This package does **not** render. It has no catalog, no materializer, no scene resolver, and no Vue
surface. Layout, DOM/canvas output, transport, URLs/fetch/cache, identity, sessions, WebSocket
envelopes, per-viewer state and product UX all belong to the host — today that is
`../../../sts2-couch-coop`, which drives the scene mirror imperatively over `@godot-scene-web/*` and
the live scene state the bridge mod streams.

## Consumption

Consumed via **source aliasing** to this checkout (no build, no publish, no npm link), exactly as
hosts consume `@godot-scene-web/*`:

```ts
// vite.config / tsconfig paths
"@spirectl/presentation/render": ".../spirectl/presentation/web/src/render/index.ts",
"@spirectl/presentation/spine": ".../spirectl/presentation/web/src/spine/index.ts",
```

An edit here is live in the host's dev server immediately, and this checkout left on a branch
changes what every host testing against it sees. Leave it on clean `main`.

## Subpath exports

| Export | Contents |
| --- | --- |
| `/render` | `applyAnimationBinding`, `ensureAnimationStyles`, `PresentationAnimationBinding`, `PresentationAnimationOptions`, `DEFAULT_BBCODE_TAGS`, `playZoneThreshold` |
| `/spine` | DOM-free Spine/geoclip clip parsing + frame sampling: `parseRasterSpineClip` / `sampleRasterSpineClip`, `parseGeoclip` / `sampleGeoclip` / `applyGeoclipVerts`, their scratch/`*Into` variants, and the `RasterSpineClip` / `Geoclip` / `SpinePlacement` / `SpineDiagnostic` types |

There is no `.` root export and no `/runtime`, `/vue`, `/diagnostics`, or `/transient` subpath. Every
module a barrel names has no further package imports, so a host pulls in the vocabulary without
dragging a dependency graph behind it. Keep it that way — a new export must be a leaf, or it does not
belong here.

### What the `/render` symbols are for

- `applyAnimationBinding` / `ensureAnimationStyles` — the per-element entry point an imperative
  renderer uses to reproduce a decorative animation (rotate/bob/floatFade/cardFly/…) that the host
  observed frozen on the game side. `ensureAnimationStyles` installs the keyframes once per document.
- `PresentationAnimationBinding` / `PresentationAnimationOptions` — the binding shape and the
  per-apply options bag, named so a host can type its own apply wrapper.
- `DEFAULT_BBCODE_TAGS` — the STS2 custom BBCode tag table (color aliases plus effect tags) used when
  laying out rich text.
- `playZoneThreshold` — the "a dragged card is in the play zone" predicate, so a host that owns its
  own pointer handling draws the same line from the pointer Y as the rendered targeting layer does.

## Tests

```bash
pnpm test        # vitest
```

`test/animations.test.ts` and `test/playZone.test.ts` cover the `/render` behavioral leaves;
`test/spineSources.test.ts` covers `/spine` clip parsing and sampling.
