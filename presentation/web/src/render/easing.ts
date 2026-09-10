// Godot easing → CSS timing-function vocabulary. A LEAF module: it imports nothing, so a host
// that only needs the easing/transition strings (an imperative mirror reproducing a frozen
// animator) can pull it in without dragging the DOM interactivity layer along.

/** The shape every caller passes to `transitionCss`: a catalog-authored duration + easing pair. */
export interface TransitionSpec {
  durationMs?: unknown;
  ease?: unknown;
  trans?: unknown;
}

// CSS `linear()` approximation of Godot ElasticOut: 2^(-10t)·sin((10t − 0.75)·2π/3) + 1,
// sampled at 17 evenly spaced points (cubic-bezier cannot oscillate). The top bar icons'
// 1s un-hover spring-back. Browsers without `linear()` ignore the declaration and snap.
export const ELASTIC_OUT_LINEAR =
  "linear(0, 0.832, 1.364, 1.193, 0.912, 0.889, 1, 1.047, 1.016, 0.986, 0.989, 1.002, 1.006, 1.001, 0.998, 0.999, 1)";

/**
 * Godot easing (`ease` in/out/in-out + `trans` expo/linear/back/elastic/…) → a CSS
 * timing-function string. Shared by `transitionCss` (CSS transitions), the animation
 * appliers (`enter` one-shot / stateChange slide in render/animations.ts), and the focus
 * layer. Godot `trans` families without an exact CSS analog (sine/cubic/quad) fall back to
 * the plain `ease-*` curve for the given direction — the same approximation used before this
 * was factored out.
 */
export function godotEasingToCss(ease?: string, trans?: string): string {
  const dir = ease === "out" ? "out" : ease === "in" ? "in" : "in-out";
  const t = typeof trans === "string" ? trans.toLowerCase() : "";
  if (t === "expo") {
    return dir === "out"
      ? "cubic-bezier(0.19, 1, 0.22, 1)"
      : dir === "in"
        ? "cubic-bezier(0.95, 0.05, 0.795, 0.035)"
        : "cubic-bezier(1, 0, 0, 1)";
  }
  if (t === "linear") return "linear";
  if (t === "back") {
    // Godot Back easing: a single overshoot, expressible as a cubic-bezier.
    return dir === "out"
      ? "cubic-bezier(0.34, 1.56, 0.64, 1)"
      : dir === "in"
        ? "cubic-bezier(0.36, 0, 0.66, -0.56)"
        : "cubic-bezier(0.68, -0.6, 0.32, 1.6)";
  }
  if (t === "elastic") {
    // Only the `out` flavor is used by the catalog (top bar un-hover); in/in-out fall
    // back to the closest single-overshoot bezier rather than an oscillation.
    return dir === "out" ? ELASTIC_OUT_LINEAR : "cubic-bezier(0.68, -0.6, 0.32, 1.6)";
  }
  return dir === "out" ? "ease-out" : dir === "in" ? "ease-in" : "ease-in-out";
}

export function transitionCss(
  transition: TransitionSpec | null | undefined,
  properties: string[] = ["opacity"],
): string {
  const duration =
    typeof transition?.durationMs === "number" && Number.isFinite(transition.durationMs)
      ? Math.max(0, transition.durationMs)
      : 0;
  if (duration <= 0) return "";
  const timing = godotEasingToCss(
    transition?.ease as string | undefined,
    transition?.trans as string | undefined,
  );
  return properties.map((p) => `${p} ${duration}ms ${timing}`).join(", ");
}
