// @spirectl/presentation/render — the render vocabulary an imperative host reuses.
//
// This barrel is deliberately tiny and leaf-only: everything it names lives in a module with no
// further package imports, so a browser host (the CouchCoop scene mirror) can pull the shared
// vocabulary in without dragging the rest of the package behind it.

// Decorative-animation vocabulary (rotate/bob/floatFade/cardFly/…). `applyAnimationBinding` is the
// per-element entry point an imperative renderer uses to reproduce an animator frozen host-side.
export { applyAnimationBinding, ensureAnimationStyles } from "./animations";
export type {
  PresentationAnimationBinding,
  // The per-apply options bag (`compose` + the two legacy-keyframe levers); type-only, but a host
  // that types its own apply wrapper needs it named here.
  PresentationAnimationOptions,
} from "./animations";

// The STS2 custom BBCode tag table (color aliases + effect tags) used when laying out rich text.
export { DEFAULT_BBCODE_TAGS } from "./bbcodeTags";

// The CSS behind the three animated tags that table names, plus the attribute a host writes to honour
// the game's Settings -> Text Effects toggle.
export {
  ensureRichTextEffectStyles,
  RICH_FX_JITTER,
  RICH_FX_SINE,
  RICH_FX_THINKY_DOTS,
  RICH_TEXT_EFFECTS_ATTRIBUTE,
} from "./richTextEffects";

// The "a dragged card is in the play zone" predicate, so a host that owns its own pointer handling
// draws the same line from the pointer Y as the rendered targeting layer does.
export { playZoneThreshold } from "./playZone";
