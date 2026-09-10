import { godotEasingToCss, transitionCss } from "./easing";

export interface PresentationAnimationBinding {
  path: string;
  kind: string;
  durationMs?: number;
  scaleFrom?: number;
  scaleTo?: number;
  alphaFrom?: number;
  alphaTo?: number;
  /** Godot easing direction (`in`/`out`/`in-out`) for the `enter` one-shot. */
  ease?: string;
  /** Godot `trans` family (`expo`/`back`/`cubic`/`sine`/…) for the `enter` one-shot. */
  trans?: string;
  /** `enter` one-shot: start X translate offset (px); the node slides from here to rest (0). */
  offsetX?: number;
  /** `enter` one-shot: start Y translate offset (px); the node slides from here to rest (0). */
  offsetY?: number;
  /** `enter` one-shot: start opacity (e.g. 0 for a fade-in); rests at 1. Covers modulate:a fades. */
  opacityFrom?: number;
  /** Upward travel distance (px) for `floatFade`; the node rises by this much then settles back. */
  risePx?: number;
  /** Bob amplitude (px) for `bob`: peak sine excursion above/below the baseline. */
  amplitudePx?: number;
  /** Bob baseline (px) for `bob`: constant upward offset the sine oscillates around. */
  baselineUpPx?: number;
  /**
   * `rock` amplitude in RADIANS — the peak angular excursion either side of rest. Radians (not degrees) because
   * every producer that names a rock loop speaks Godot's `Control.Rotation` (e.g. `Sts2TopBarFold`'s
   * `DeckRockAmplitudeRad`/`MapRockAmplitudeRad` = 0.12); the keyframes convert once, to literal `deg`.
   */
  amplitudeRad?: number;
  /** Phase delay (ms) for `bob`/`flameFlicker`/`pivotPulse`/`rock`/`glowPulse`: a negative animation-delay seeding the sine phase. */
  delayMs?: number;
  /**
   * `pivotPulse` — the node's PIVOT expressed in the coordinate space its baked transform maps INTO
   * (i.e. `bakedMatrix · pivotLocal`), in px. The pulse composes a `scale:`/`translate:` pair on an element that
   * already carries a baked `matrix()`, and the translate is what keeps the scale anchored at that pivot instead
   * of at the matrix's origin — see the `pivotPulse` branch. Defaults to (0, 0) = scale about the element origin.
   *
   * `rock`/`rotate` — the node's OWN pivot in its own local px (Godot `Control.PivotOffset`), written straight to
   * `transform-origin`. Those kinds spin an element that carries no baked matrix (see their branches), so no
   * paired translate is needed. Omitted entirely → the element's default origin is left untouched.
   */
  pivotX?: number;
  pivotY?: number;
  /** Source/target screen coords (px) for `cardFly`: the ghost flies from (fromX,fromY) to (toX,toY). */
  fromX?: number;
  fromY?: number;
  toX?: number;
  toY?: number;
  loop?: boolean;
}

const STYLE_ID = "spirectl-presentation-animations";
// Second, DYNAMIC stylesheet: the value-keyed LITERAL keyframes (see `ensureLiteralKeyframes`). Kept apart from
// the static sheet above so the static one stays a constant string injected exactly once per document.
const LITERAL_STYLE_ID = "spirectl-presentation-animations-literal";
const PULSE_SCALE_FADE = "spirectl-pulse-scale-fade";
const ROTATE = "spirectl-rotate";
const FLOAT_FADE = "spirectl-float-fade";
const INTENT_BOB = "spirectl-intent-bob";
// Compose variant of the bob for a consumer that BAKES a global transform matrix into `element.style.transform`
// (the CouchCoop scene mirror): it drives the individual `translate:` property instead of the `transform:`
// shorthand, so it COMPOSES with the baked matrix instead of clobbering it. This is valid ONLY for translate — a
// translation is origin-independent, so translating an already-positioned element just shifts it. (There is no
// rotate compose variant: an individual `rotate:` sweeps the matrix's translation offset about the origin and
// ORBITS the node; the mirror spins a self-layer child for the `rotate`/`rock` kinds instead.)
//
// Both variants are LEGACY as of the literalization round: they read the bob's two extrema out of inline custom
// properties, which is exactly what stops Chrome compositing them. They stay for `legacyBobKeyframes`; the default
// path injects the same two frames with the px baked in (see `ensureBobKeyframes`).
const INTENT_BOB_COMPOSE = "spirectl-intent-bob-compose";
// The enemy-intent bob's defaults, named so the literal keyframes and the legacy var() fallbacks can never
// drift: baseline 8px up, ±10px → the pair `translateY(-18px)` ↔ `translateY(2px)`.
const BOB_BASELINE_UP_PX = 8;
const BOB_AMPLITUDE_PX = 10;
const BOB_PERIOD_MS = 2000;
// ROCK: a rotation oscillation about a pivot, ±amplitude, replayed client-side for a loop the producer pinned
// (`RuntimeSceneNodeDelta.PinnedLoopAnim` — today `topBarDeckRock` / `topBarMapRock`, see
// bridge-mod Sts2TopBarFold). Amplitude default 0.12 rad = 6.8755deg (DeckRockAmplitudeRad == MapRockAmplitudeRad);
// period default 1600ms (MapRockPeriodMs, two 0.8s Sine/InOut legs) — every real caller passes the producer's own.
const ROCK = "spirectl-rock";
const ROCK_AMPLITUDE_RAD = 0.12;
const ROCK_PERIOD_MS = 1600;
// GLOW PULSE: the proceed-button glow shimmer (bridge-mod Sts2ProceedGlow). On screen the glow's self-modulate
// alpha sweeps 0.75 → 0.25 → 0.75 on two 0.5s LINEAR legs, and the producer pins that alpha at 0.75
// (`PinnedAlpha == LoopMaxAlpha`), so the client cannot re-animate the alpha itself — it multiplies the pinned
// value by an OPACITY loop. Hence the ratio: 0.25/0.75 = 1/3 → the far endpoint is opacity 0.33333, and opacity 1
// (the pinned alpha, untouched) is the endpoint each cycle starts AND ends at, exactly like the shimmer's first
// leg departing from 0.75. Linear timing, `alternate`, one 500ms leg per iteration.
const GLOW_PULSE = "spirectl-glow-pulse";
const PROCEED_GLOW_MIN_ALPHA = 0.25;
const PROCEED_GLOW_MAX_ALPHA = 0.75;
const GLOW_PULSE_FROM = 1;
const GLOW_PULSE_TO = PROCEED_GLOW_MIN_ALPHA / PROCEED_GLOW_MAX_ALPHA;
const GLOW_PULSE_PERIOD_MS = 1000;
const BRIGHTEN = "spirectl-brighten";
// Tezcatara candle-fire loop (NRestSiteFireVfx): two INDEPENDENT sine tracks — scaleY and skewX. LEGACY variant
// (`legacyFlameKeyframes`): each runs as a separate @property custom-prop animation so their keyframes don't
// clobber a shared `transform:` shorthand (a static `transform` reads both animated props). The full oscillation
// periods; `alternate` plays a half-period per iteration (peak→trough), so the animation `duration` is half of these.
const FLAME_SCALEY = "spirectl-flame-scaley";
const FLAME_SKEW = "spirectl-flame-skew";
const FLAME_SCALEY_PERIOD_MS = 1700;
const FLAME_SKEW_PERIOD_MS = 2600;
// LITERAL flame tracks (default) — same two oscillations with no custom property in sight, so Chrome can run them
// on the compositor: the scaleY leg drives the INDIVIDUAL `scale:` property and the skew leg drives `transform:`.
// Two different properties, so neither keyframe clobbers the other (the reason the @property pair existed), and
// CSS composes them as `translate · rotate · scale · transform` = Skew · ScaleY — the SAME order the old static
// `transform: skewX(...) scaleY(...)` produced, so the visual result is unchanged. Valid only because the flame
// paint layer carries no baked matrix of its own (the legacy path already overwrote `transform` outright).
const FLAME_SCALEY_LITERAL = "spirectl-flame-scaley-lit";
const FLAME_SKEW_LITERAL = "spirectl-flame-skew-lit";
const FLAME_SCALEY_MIN = 0.92;
const FLAME_SCALEY_MAX = 1.08;
const FLAME_SKEW_RAD = 0.1;
// easeInOutSine, as a CSS bezier: paired with a HALF-period duration + `alternate`, one leg is an exact cosine, so
// the two legs join into a true sine (slow at the extrema, fast through the middle) instead of a triangle wave.
const EASE_IN_OUT_SINE = "cubic-bezier(0.37, 0, 0.63, 1)";
// PIVOT PULSE: a sinusoidal UNIFORM scale about a node's own pivot, composed onto an element that already carries a
// baked global `matrix()` (the CouchCoop scene mirror). Like INTENT_BOB_COMPOSE it drives the INDIVIDUAL transform
// properties rather than the `transform:` shorthand — but scale, unlike translate, is NOT origin-independent: CSS
// applies the individual properties OUTSIDE the matrix (`translate · rotate · scale · transform`), so a bare
// `scale: k` multiplies the matrix's translation too and the node SLIDES as it grows. The fix is the paired
// translate: with the element's transform-origin at 0, `scale: k` maps a point to `k·(A·p + t)` while anchoring at
// the pivot requires `u + k·(A·p + t − u)` where `u` is the pivot's position AFTER the matrix — so adding
// `translate: (1 − k)·u` makes the two identical for every k. Both endpoints are precomputed by the caller
// (`--spirectl-pulse-*`), and because the translate is LINEAR in k, interpolating it with the SAME timing function
// keeps the identity exact at every intermediate frame, not just at the extremes.
const PIVOT_PULSE = "spirectl-pivot-pulse";
const CARD_FLY = "spirectl-card-fly";
const CARD_SMOKE = "spirectl-card-smoke";
const ENTER = "spirectl-enter";
const STATE_TRANSITION = "spirectl-state-transition";

/**
 * Per-call knobs for {@link applyAnimationBinding} / {@link applyAnimationBindings}. Every field is optional and
 * additive; the two `legacy*` flags are KILL SWITCHES for this round's compositor fix, so a host can expose them as
 * a query flag (`?bobLit=off`) and get byte-for-byte the pre-literalization keyframes back without a rebuild.
 */
export interface PresentationAnimationOptions {
  /**
   * `bob` only: drive the individual `translate:` property instead of the `transform:` shorthand, so the bob
   * COMPOSES with a baked global `matrix()` the host already wrote into `element.style.transform` (the CouchCoop
   * scene mirror). Valid for translate alone — see {@link INTENT_BOB_COMPOSE}.
   */
  compose?: boolean;
  /**
   * Restore the pre-literalization `bob` keyframes, which read `var()`/`calc()` off two inline custom properties.
   * Chrome can NEVER composite a keyframe containing `var()`/`calc()` (it reports `compositeFailed` with
   * unsupportedProperties), so the default path bakes the resolved px pair into a value-keyed literal rule
   * instead — same visual result, main thread idle. Off by default; only a bisect should turn this on.
   */
  legacyBobKeyframes?: boolean;
  /**
   * Restore the pre-literalization `flameFlicker` tracks, which animate two `@property` registered custom
   * properties read by a static `transform`. Registered custom properties are never compositor-eligible either;
   * the default path animates literal `scale:` + `transform: skewX(...)` keyframes on the same element.
   */
  legacyFlameKeyframes?: boolean;
  /**
   * Restore the pre-literalization `pulseScaleFade` keyframes, which read `var()` off four inline custom
   * properties and so can never run on the compositor. Off by default; only a bisect should turn this on. Same
   * rationale (and same visual result) as {@link legacyBobKeyframes}.
   */
  legacyPulseKeyframes?: boolean;
}

export function applyAnimationBindings(
  root: ParentNode,
  bindings: readonly PresentationAnimationBinding[] | Record<string, unknown> | null | undefined,
  opts?: PresentationAnimationOptions,
): Set<string> {
  ensureAnimationStyles(root);
  const applied = new Set<string>();
  for (const binding of normalizeBindings(bindings)) {
    const el = root.querySelector<HTMLElement>(`[data-godot-path="${cssEscape(binding.path)}"]`);
    if (!el) continue;
    if (applyAnimationBinding(el, binding, opts)) applied.add(binding.path);
  }
  return applied;
}

// ---------------------------------------------------------------------------
// VALUE-KEYED LITERAL KEYFRAMES
//
// A keyframe whose declarations contain `var()` or `calc()` is resolved against the ELEMENT's computed style, so
// Chrome refuses to run it on the compositor and falls back to a main-thread style recalc + paint EVERY FRAME —
// at 90Hz on a phone that is the single most expensive thing an idle screen does. (Traced on the enemy-intent bob:
// `compositeFailed` with unsupportedProperties, on every intent, forever.)
//
// The fix costs nothing visually: the values are known at apply time, so instead of parameterising ONE shared rule
// with custom properties we emit one LITERAL rule per distinct value tuple actually requested. A screen has a
// handful of tuples (all default intents share `translateY(-18px) ↔ translateY(2px)`), so the sheet stays tiny and
// every element that wants the same motion shares the same rule — the keyframes are keyed BY VALUE, which is
// exactly the sharing the custom properties were emulating.
//
// The rules live in their own `<style>` element, one per document (WeakMap-keyed so a detached document can be
// collected), with a Set of already-injected names guarding re-injection. Names are derived from the values
// (`-18` → `n18`, `0.33333` → `0p33333`), so the key IS the tuple and no counter/allocation table is needed.
// ---------------------------------------------------------------------------

interface LiteralKeyframeStore {
  style: HTMLStyleElement;
  names: Set<string>;
}

const literalKeyframeStores = new WeakMap<Document, LiteralKeyframeStore>();

function literalKeyframeStore(doc: Document): LiteralKeyframeStore {
  const existing = literalKeyframeStores.get(doc);
  // `parentNode` check: a host that wiped the head (or a test that re-created the document body) must get a fresh
  // sheet AND a fresh name set, otherwise we would believe rules are injected that no longer exist anywhere.
  if (existing && existing.style.parentNode) return existing;
  const style = doc.createElement("style");
  style.id = LITERAL_STYLE_ID;
  doc.head.appendChild(style);
  const store: LiteralKeyframeStore = { style, names: new Set<string>() };
  literalKeyframeStores.set(doc, store);
  return store;
}

/**
 * Inject `@keyframes <name> { <body> }` into the document's literal sheet once, and return `name`. `name` must
 * already encode the value tuple (see the token helpers), so "already injected" and "same values" are the same
 * test. Appending a text node (rather than rewriting `textContent`) keeps each injection O(1) and leaves the
 * existing rules' text untouched.
 */
function ensureLiteralKeyframes(doc: Document, name: string, body: string): string {
  const store = literalKeyframeStore(doc);
  if (store.names.has(name)) return name;
  store.names.add(name);
  store.style.appendChild(doc.createTextNode(`@keyframes ${name} {\n${body}\n}\n`));
  return name;
}

/** Format a number for a CSS literal: rounded to `decimals` places, no trailing zeros, never `NaN`/`Infinity`. */
function fmtNum(value: number, decimals = 4): string {
  if (!Number.isFinite(value)) return "0";
  const scale = 10 ** decimals;
  return String(Math.round(value * scale) / scale);
}

/** The same number as a CSS IDENT fragment, so the animation-name carries its own value key (`-18` → `n18`). */
function keyToken(value: number, decimals = 4): string {
  return fmtNum(value, decimals).replace("-", "n").replace(".", "p").replace(/[^A-Za-z0-9_-]/g, "_");
}

/**
 * The literal `bob` rule for one resolved (top, bottom) px pair. Two variants — the `transform:` shorthand and the
 * `translate:` compose form (see {@link INTENT_BOB_COMPOSE}) — keyed apart by name prefix, so a document that
 * bobs both ways gets one rule each and nothing shared by accident. Falls back to the legacy shared rule when the
 * element has no document to inject into (a detached node built outside a DOM).
 */
function ensureBobKeyframes(el: HTMLElement, fromY: number, toY: number, compose: boolean): string {
  const doc = el.ownerDocument;
  if (!doc) return compose ? INTENT_BOB_COMPOSE : INTENT_BOB;
  const name = `${compose ? INTENT_BOB_COMPOSE : INTENT_BOB}-${keyToken(fromY)}-${keyToken(toY)}`;
  const from = `${fmtNum(fromY)}px`;
  const to = `${fmtNum(toY)}px`;
  const body = compose
    ? `  from { translate: 0 ${from}; }\n  to { translate: 0 ${to}; }`
    : `  from { transform: translateY(${from}); }\n  to { transform: translateY(${to}); }`;
  return ensureLiteralKeyframes(doc, name, body);
}

/** The literal `rock` rule for one amplitude: `rotate: ∓ampDeg`, keyed by the amplitude alone. */
function ensureRockKeyframes(el: HTMLElement, ampDeg: number): string {
  const doc = el.ownerDocument;
  if (!doc) return ROCK;
  const amp = fmtNum(ampDeg);
  const name = `${ROCK}-${keyToken(ampDeg)}`;
  return ensureLiteralKeyframes(doc, name, `  from { rotate: -${amp}deg; }\n  to { rotate: ${amp}deg; }`);
}

/**
 * The literal `pulseScaleFade` rule for one (scaleFrom, scaleTo, alphaFrom, alphaTo) tuple. Same literalization as
 * the bob/flame/rock/glow rules: a keyframe containing `var()` is never compositor-eligible, so a single
 * always-mounted pulse (the end-turn button's `GlowVfx`, which loops for the WHOLE of an idle combat turn) costs a
 * main-thread style recalc + paint every frame. The values are known at apply time, so emit one literal rule per
 * distinct tuple. `transform:` (the shorthand, as before) + `opacity`, both literal — Chromium composites both.
 */
function ensurePulseScaleFadeKeyframes(
  el: HTMLElement,
  scaleFrom: number,
  scaleTo: number,
  alphaFrom: number,
  alphaTo: number,
): string {
  const doc = el.ownerDocument;
  if (!doc) return PULSE_SCALE_FADE;
  const name =
    `${PULSE_SCALE_FADE}-${keyToken(scaleFrom, 5)}-${keyToken(scaleTo, 5)}` +
    `-${keyToken(alphaFrom, 5)}-${keyToken(alphaTo, 5)}`;
  return ensureLiteralKeyframes(
    doc,
    name,
    `  from { opacity: ${fmtNum(alphaFrom, 5)}; transform: scale(${fmtNum(scaleFrom, 5)}); }\n` +
      `  to { opacity: ${fmtNum(alphaTo, 5)}; transform: scale(${fmtNum(scaleTo, 5)}); }`,
  );
}

/** The literal `glowPulse` rule for one (start, far) opacity pair. 5 decimals so the 1/3 ratio reads 0.33333. */
function ensureGlowPulseKeyframes(el: HTMLElement, from: number, to: number): string {
  const doc = el.ownerDocument;
  if (!doc) return GLOW_PULSE;
  const name = `${GLOW_PULSE}-${keyToken(from, 5)}-${keyToken(to, 5)}`;
  return ensureLiteralKeyframes(
    doc,
    name,
    `  from { opacity: ${fmtNum(from, 5)}; }\n  to { opacity: ${fmtNum(to, 5)}; }`,
  );
}

/**
 * Write the node's own pivot (Godot `Control.PivotOffset`, local px) to `transform-origin` for the rotation kinds.
 * A binding that carries NEITHER coordinate leaves the element's origin completely untouched — that is what keeps
 * the pre-existing pivot-less `rotate` callers (the energy/star orb layers, which are already pivot-centered)
 * pixel-identical.
 */
function applyPivotOrigin(el: HTMLElement, binding: PresentationAnimationBinding): void {
  if (binding.pivotX === undefined && binding.pivotY === undefined) return;
  el.style.transformOrigin = `${fmtNum(binding.pivotX ?? 0)}px ${fmtNum(binding.pivotY ?? 0)}px`;
}

/**
 * Apply one decorative animation directly to `el` — the per-binding body of {@link applyAnimationBindings},
 * extracted so an imperative renderer that owns its own elements (the CouchCoop scene mirror) can reproduce an
 * animator it froze game-side without a `data-godot-path` DOM scan. Returns true when a known kind was applied.
 * The caller must have injected the keyframes via {@link ensureAnimationStyles}. NOTE the keyframes use the
 * `transform:` shorthand, so `el` must not itself carry a baked transform matrix — apply to a child layer whose
 * transform composes with the parent's. (The exceptions are `bob` with `opts.compose` and `pivotPulse`, which
 * drive the INDIVIDUAL transform properties precisely so they compose with a baked matrix.)
 *
 * WHICH ELEMENT a kind targets is the CALLER's decision, and for the rotation kinds (`rotate`, `rock`) it must be
 * an element with no baked matrix — an individual `rotate:` applied on top of a baked `matrix()` sweeps the
 * matrix's translation about the origin and ORBITS the node instead of spinning it in place. The CouchCoop mirror
 * therefore spins a dedicated self-layer CHILD, whose parent carries the matrix.
 */
export function applyAnimationBinding(
  el: HTMLElement,
  binding: PresentationAnimationBinding,
  opts?: PresentationAnimationOptions,
): boolean {
  {
    if (binding.kind === "pulseScaleFade") {
      // A scale-up + fade-out that RESTARTS from its start values every cycle (no `alternate`) — the shape of a
      // looping Godot tween whose parallel legs each snap back to their start. Today's callers are the end-turn
      // button's `GlowVfx` (scale 0.5→0.7 Quart/Out in parallel with alpha 0.4→0, 1.5s) in the stateful catalog
      // view, and the CouchCoop mirror's `endTurnGlow` replay of the same loop — which passes RATIOS (1→1.4, 1→0)
      // because the producer bakes the loop's start values into the streamed transform/colour (bridge-mod
      // `Sts2EndTurnGlowFold`) and the client can only multiply them back up.
      //
      // The defaults ARE that loop's absolute values, so a bare `{kind:"pulseScaleFade"}` still reproduces it.
      const scaleFrom = binding.scaleFrom ?? 0.5;
      const scaleTo = binding.scaleTo ?? 0.7;
      const alphaFrom = binding.alphaFrom ?? 0.4;
      const alphaTo = binding.alphaTo ?? 0;
      // The custom properties are still written (unchanged) so a host/devtools can read the values off the element
      // and so `legacyPulseKeyframes` — which restores the shared var()-driven rule verbatim — has them to read.
      el.style.setProperty("--spirectl-pulse-scale-from", String(scaleFrom));
      el.style.setProperty("--spirectl-pulse-scale-to", String(scaleTo));
      el.style.setProperty("--spirectl-pulse-alpha-from", String(alphaFrom));
      el.style.setProperty("--spirectl-pulse-alpha-to", String(alphaTo));
      const name = opts?.legacyPulseKeyframes
        ? PULSE_SCALE_FADE
        : ensurePulseScaleFadeKeyframes(el, scaleFrom, scaleTo, alphaFrom, alphaTo);
      el.style.animation = `${name} ${Math.max(1, binding.durationMs ?? 1500)}ms ease-out ${-(
        binding.delayMs ?? 0
      )}ms ${binding.loop === false ? "1" : "infinite"}`;
      return true;
    } else if (binding.kind === "rotate") {
      // A ring or icon turning forever at constant speed — e.g. the energy counter's RotationLayers children, or
      // the top bar's settings icon at 1 rad/s. `durationMs` is one full turn.
      //
      // PIVOT: the historical callers spin pivot-CENTERED layers, so the element's default origin is right and is
      // left untouched — those callers are pixel-unchanged. A caller that knows the node's own Godot
      // `PivotOffset` (the top-bar icons do not spin about their centre) passes it as `pivotX`/`pivotY` in local
      // px and we write it straight to `transform-origin`; `rotate:`, like every individual transform property,
      // honours `transform-origin`.
      applyPivotOrigin(el, binding);
      el.style.animation = `${ROTATE} ${Math.max(1, binding.durationMs ?? 10000)}ms linear ${
        binding.loop === false ? "1" : "infinite"
      }`;
      return true;
    } else if (binding.kind === "rock") {
      // A rotation OSCILLATION about the node's pivot: ±`amplitudeRad` radians, `durationMs` the FULL period.
      // Replays a loop the producer pinned out of the wire (`PinnedLoopAnim`) — today the top-bar deck/map icons
      // (`Sts2TopBarFold`: ±0.12 rad; deck period 2π/4 s ≈ 1570.8ms, map 2×0.8s = 1600ms), whose rest rotation the
      // producer folds back to 0 so position/scale keep streaming live.
      //
      // Like the bob, the keyframes hold only the two extrema and one `alternate` iteration is a HALF period; the
      // easeInOutSine timing makes that leg an exact cosine, so the two legs join into the true sine the on-screen
      // rock traces (a ±0.12 rad sine on one button, two Sine-InOut legs on the other). The values are literal
      // `deg` (no `var()`/`calc()`), and
      // the INDIVIDUAL `rotate:` property is used rather than the `transform:` shorthand so the spin composes with
      // whatever transform the host already put on the element — see the function doc for which element that must
      // be (a matrix-free self layer; an individual `rotate:` over a baked matrix orbits).
      const ampDeg = ((binding.amplitudeRad ?? ROCK_AMPLITUDE_RAD) * 180) / Math.PI;
      const name = ensureRockKeyframes(el, ampDeg);
      applyPivotOrigin(el, binding);
      const halfPeriod = Math.max(1, (binding.durationMs ?? ROCK_PERIOD_MS) / 2);
      el.style.animation = `${name} ${halfPeriod}ms ${EASE_IN_OUT_SINE} ${-(binding.delayMs ?? 0)}ms ${
        binding.loop === false ? "1" : "infinite"
      } alternate`;
      return true;
    } else if (binding.kind === "glowPulse") {
      // An OPACITY loop replaying a self-modulate alpha shimmer the producer pinned — today the proceed button's
      // additive outline (bridge-mod `Sts2ProceedGlow`), the single biggest churner on an otherwise still screen.
      //
      // The producer ships the outline's colour with its alpha PINNED at the loop's own resting value
      // (`Sts2ProceedGlow.PinnedAlpha == LoopMaxAlpha == 0.75`), i.e. the value each tween cycle starts and ends
      // at. The client cannot re-animate that alpha (it is baked into the streamed colour), so it MULTIPLIES it
      // with an opacity animation: the far endpoint is the ratio `LoopMinAlpha / LoopMaxAlpha` = 0.25/0.75 = 1/3
      // → 0.33333, and opacity 1 (the pinned alpha, untouched) is where the cycle starts AND ends — which is why
      // `alphaFrom` defaults to 1 rather than to the dim end. Two 0.5s LINEAR legs (`Tween`'s default transition),
      // so one `alternate` iteration is `durationMs`/2 = 500ms and NO easing curve is applied.
      //
      // The caller picks the element: it must be one whose opacity MULTIPLIES the pinned alpha (a wrapper/self
      // layer), never the node whose inline opacity already carries it.
      const from = binding.alphaFrom ?? GLOW_PULSE_FROM;
      const to = binding.alphaTo ?? GLOW_PULSE_TO;
      const name = ensureGlowPulseKeyframes(el, from, to);
      const halfPeriod = Math.max(1, (binding.durationMs ?? GLOW_PULSE_PERIOD_MS) / 2);
      el.style.animation = `${name} ${halfPeriod}ms linear ${-(binding.delayMs ?? 0)}ms ${
        binding.loop === false ? "1" : "infinite"
      } alternate`;
      return true;
    } else if (binding.kind === "brighten") {
      // Periodic brightness pulse — the peek button's `button_pulse.gdshader` glow,
      // which brightens by ~20% at about 1 Hz. A
      // single keyframe ramps `filter: brightness()` up and back; `ease-in-out`
      // alternating gives the sinusoidal feel. `durationMs` is one full pulse.
      el.style.animation = `${BRIGHTEN} ${Math.max(1, (binding.durationMs ?? 1000) / 2)}ms ease-in-out ${
        binding.loop === false ? "1" : "infinite"
      } alternate`;
      return true;
    } else if (binding.kind === "floatFade") {
      // Floating damage number (NDamageNumVfx): it appears above the creature,
      // arcs UP under gravity (initial vel y ≈ −750, gravity +2000), scales
      // 2.5 → 1.0 (out-quad, 1.2s), and fades alpha → 0 (in-quad, 2.0s), then
      // disappears. CSS can't do per-frame physics, so one keyframe approximates
      // the visible result: a fast rise that settles back slightly while
      // shrinking and fading. One-shot (`loop:false` → iteration "1"), and
      // `forwards` so it rests at the faded-out final frame.
      //
      // IDEMPOTENCY (load-bearing): this post-mount apply re-runs on every
      // scene/resource settle, but a one-shot must NOT restart. The repeat node
      // is keyed by the effect's stable `id`, so the element persists across
      // re-walks; if it already carries this animation, skip re-assigning it
      // (re-setting `style.animation` to the same value would restart it in
      // some engines, and the keyed node is the same element). Read the shorthand
      // we set (reliable across CSSOM impls) rather than the parsed `animationName`.
      if (!el.style.animation.includes(FLOAT_FADE)) {
        el.style.setProperty("--spirectl-float-rise", `${binding.risePx ?? 260}px`);
        el.style.setProperty("--spirectl-float-scale-from", String(binding.scaleFrom ?? 2.5));
        el.style.setProperty("--spirectl-float-scale-to", String(binding.scaleTo ?? 1));
        el.style.animation = `${FLOAT_FADE} ${Math.max(1, binding.durationMs ?? 2000)}ms ease-out ${
          binding.loop === false ? "1" : "infinite"
        } forwards`;
      }
      return true;
    } else if (binding.kind === "bob") {
      // Enemy intent bob: the intent holder hovers, oscillating vertically on a sine of
      // time about a baseline that sits `base` px ABOVE its resting spot, i.e.
      // `translateY = -(sin*amp + base)` (CSS negative Y = up). The keyframes hold only
      // the two extrema (peak `-(base+amp)` → trough `amp-base`); the easeInOutSine
      // timing makes ONE leg an exact cosine (easeInOutSine(t) = (1-cos(πt))/2, so
      // peak→trough interpolates to `A·cos(πt)`), and `alternate` mirrors it into a
      // continuous sinusoid — slow at the extremes, fast through the baseline (a real
      // sine, not the constant-velocity triangle wave linear interpolation would give).
      // `durationMs` is the FULL sine period; one alternate iteration is a half-period.
      // A per-intent phase (negative `animation-delay`) seeds the cycle so a
      // multi-attack's icons don't bob in lockstep (no startup stagger).
      //
      // LITERAL KEYFRAMES (default): the two extrema are resolved HERE and injected as a value-keyed literal rule
      // — `translateY(-18px) ↔ translateY(2px)` for the defaults — because a keyframe that reads `var()`/`calc()`
      // is never compositor-eligible and a bobbing intent then costs a main-thread style recalc every frame. The
      // custom properties are still written (unchanged) so a host/devtools can read the values off the element and
      // so `legacyBobKeyframes` — which restores the shared var()-driven rule verbatim — has them to read.
      const base = binding.baselineUpPx ?? BOB_BASELINE_UP_PX;
      const amp = binding.amplitudePx ?? BOB_AMPLITUDE_PX;
      el.style.setProperty("--spirectl-bob-base", `${base}px`);
      el.style.setProperty("--spirectl-bob-amp", `${amp}px`);
      const compose = opts?.compose === true;
      const name = opts?.legacyBobKeyframes
        ? compose
          ? INTENT_BOB_COMPOSE
          : INTENT_BOB
        : ensureBobKeyframes(el, -(base + amp), amp - base, compose);
      const halfPeriod = Math.max(1, (binding.durationMs ?? BOB_PERIOD_MS) / 2);
      el.style.animation = `${name} ${halfPeriod}ms ${EASE_IN_OUT_SINE} ${-(
        binding.delayMs ?? 0
      )}ms ${binding.loop === false ? "1" : "infinite"} alternate`;
      return true;
    } else if (binding.kind === "flameFlicker") {
      // Tezcatara candle fire (NRestSiteFireVfx). On screen the fire ROOT flickers and sways at once — a scale.Y
      // jitter in [0.85,1.05]·base over 0.3-0.5s legs, and a skew of ±0.1rad over 0.8-1.5s legs — both driven by
      // self-chaining tweens the headless host freezes (COUCHCOOP_HEADLESS_FREEZE_DECOR). It is aperiodic but bounded,
      // so reproduce the visible result as two independent sine LOOPS on this quad's paint layer: scaleY ±8%
      // (~1.7s) + skewX ±0.1rad (~2.6s), anchored at the paint-box BOTTOM-CENTER (fire rises from its base).
      //
      // The two periods are separate @property custom-prop animations (`--spirectl-flame-scaley` /
      // `--spirectl-flame-skew`); a single static `transform:` reads both. Driving one `transform:` shorthand
      // animation per period would make the two clobber each other (each keyframe writes the whole `transform`);
      // animating the REGISTERED custom props instead lets both compose into one transform with no conflict.
      // `delayMs` is a per-flame phase (hash of the parent path) applied as a NEGATIVE delay to BOTH tracks, so
      // the 79 flames desync while a single flame's stacked quads (same parent → same delay) stay mutually
      // layered. easeInOutSine timing makes each `alternate` half-leg a true cosine → a real sine, matching the
      // native `sin()` port. No baked matrix lives on this self-layer, so the `transform:` shorthand is safe here.
      //
      // LITERAL KEYFRAMES (default): a registered custom property is no more compositor-eligible than a `var()`
      // keyframe — animating `--spirectl-flame-*` re-runs style + paint for all 79 flames on the main thread every
      // frame. The default path drops the custom props entirely and animates two literal tracks on DIFFERENT
      // properties (individual `scale:` for the scaleY leg, `transform: skewX()` for the sway), which compose in
      // the same order the old static transform did and can both run off the main thread. `legacyFlameKeyframes`
      // restores the @property pair verbatim.
      const phaseMs = -(binding.delayMs ?? 0);
      const iter = binding.loop === false ? "1" : "infinite";
      el.style.transformOrigin = "50% 100%";
      const legacyFlame = opts?.legacyFlameKeyframes === true;
      if (legacyFlame) {
        el.style.transform = "skewX(var(--spirectl-flame-skew, 0rad)) scaleY(var(--spirectl-flame-scaley, 1))";
      }
      const scaleyName = legacyFlame ? FLAME_SCALEY : FLAME_SCALEY_LITERAL;
      const skewName = legacyFlame ? FLAME_SKEW : FLAME_SKEW_LITERAL;
      el.style.animation =
        `${scaleyName} ${FLAME_SCALEY_PERIOD_MS / 2}ms ${EASE_IN_OUT_SINE} ${phaseMs}ms ${iter} alternate, ` +
        `${skewName} ${FLAME_SKEW_PERIOD_MS / 2}ms ${EASE_IN_OUT_SINE} ${phaseMs}ms ${iter} alternate`;
      return true;
    } else if (binding.kind === "pivotPulse") {
      // A game animator that sweeps a node's UNIFORM SCALE as a sine of time, replayed client-side because the
      // producer pinned it to rest to stop it churning the wire (RuntimeSceneNodeDelta.PinnedLoopAnim). Today's
      // caller is the STS2 map, where every TRAVELABLE point breathes — its icon container sweeping a uniform
      // scale over 0.95..1.45 at 4 rad/s — a gameplay affordance, so the replay has to be faithful, not decorative.
      //
      // `scaleFrom`/`scaleTo` are the sweep's extrema and `durationMs` its FULL period; one `alternate` iteration
      // is a half-period, and the easeInOutSine timing makes that half-leg an exact cosine (the same
      // half-period + cubic-bezier(0.37, 0, 0.63, 1) trick the bob and flame loops use), so the two legs join
      // into a true sine rather than a triangle wave. `delayMs` is a per-node phase (negative delay): each point
      // starts at its own random point of the cycle, so pulsing nodes must NOT run in lockstep — one shared
      // keyframe timeline, offset per node.
      //
      // The paired `translate` keeps the scale anchored at the node's own pivot; see PIVOT_PULSE above for why it
      // is needed and why precomputing both endpoints is exact.
      const from = binding.scaleFrom ?? 1;
      const to = binding.scaleTo ?? 1;
      const px = binding.pivotX ?? 0;
      const py = binding.pivotY ?? 0;
      el.style.setProperty("--spirectl-pulse-from", String(from));
      el.style.setProperty("--spirectl-pulse-to", String(to));
      el.style.setProperty("--spirectl-pulse-from-x", `${(1 - from) * px}px`);
      el.style.setProperty("--spirectl-pulse-from-y", `${(1 - from) * py}px`);
      el.style.setProperty("--spirectl-pulse-to-x", `${(1 - to) * px}px`);
      el.style.setProperty("--spirectl-pulse-to-y", `${(1 - to) * py}px`);
      const halfPeriod = Math.max(1, (binding.durationMs ?? 1000) / 2);
      el.style.animation = `${PIVOT_PULSE} ${halfPeriod}ms ${EASE_IN_OUT_SINE} ${-(
        binding.delayMs ?? 0
      )}ms ${binding.loop === false ? "1" : "infinite"} alternate`;
      return true;
    } else if (binding.kind === "cardFly") {
      // A card flying on an arc between two points: hand→discard (NCardFlyVfx), discard→draw on a reshuffle,
      // or draw→hand on a deal. The ghost mounts at the SOURCE (the catalog positions the host there), so we
      // translate by the (to-from) delta with a mid-arc lift and a shrink-toward-the-target, easing out. A
      // positive `delayMs` staggers the whole-pile cascades (shuffle/deal); it's 0 for a single hand exit, and
      // the ghost's rest state equals the 0% keyframe so `forwards` shows no flash during the wait. One-shot +
      // idempotent (the floatFade pattern): keyed by the move id so a re-render never restarts a fly mid-flight.
      if (!el.style.animation.includes(CARD_FLY)) {
        const dx = (binding.toX ?? 0) - (binding.fromX ?? 0);
        const dy = (binding.toY ?? 0) - (binding.fromY ?? 0);
        el.style.setProperty("--spirectl-fly-dx", `${dx}px`);
        el.style.setProperty("--spirectl-fly-dy", `${dy}px`);
        // Arc lift scales with horizontal travel, capped, so short/long flights both read as an arc.
        el.style.setProperty("--spirectl-fly-arc", `${Math.min(160, Math.abs(dx) * 0.25)}px`);
        el.style.setProperty("--spirectl-fly-scale-to", String(binding.scaleTo ?? 0.6));
        el.style.animation = `${CARD_FLY} ${Math.max(1, binding.durationMs ?? 800)}ms cubic-bezier(0.16, 1, 0.3, 1) ${Math.max(
          0,
          binding.delayMs ?? 0,
        )}ms ${binding.loop === false ? "1" : "infinite"} forwards`;
      }
      return true;
    } else if (binding.kind === "cardSmoke") {
      // A card sent to the exhaust pile does NOT fly — it dissolves in place in a smoke poof (NExhaustVfx:
      // GPU particles + fade over ~2s). Approximate as a scale-up + blur + fade-out, one-shot + idempotent.
      if (!el.style.animation.includes(CARD_SMOKE)) {
        el.style.setProperty("--spirectl-smoke-scale-to", String(binding.scaleTo ?? 1.25));
        el.style.animation = `${CARD_SMOKE} ${Math.max(1, binding.durationMs ?? 2000)}ms ease-out ${
          binding.loop === false ? "1" : "infinite"
        } forwards`;
      }
      return true;
    } else if (binding.kind === "enter") {
      // Mount-time one-shot reveal — the end-turn button, the card piles, the energy
      // counter, event and rest-site option buttons, an enemy's intents, …: the node
      // slides in from an off-screen offset and/or
      // fades up, settling at its resting layout position. Plays ONCE when the keyed element
      // first mounts, then releases (no `forwards`) so the node rests at its natural
      // position — the offset/opacity are a transient overlay, not a held transform.
      //
      // IDEMPOTENCY (load-bearing, same rule as floatFade/cardFly): this post-mount apply
      // re-runs on every scene/resource settle, but a one-shot must NOT restart. The element
      // is keyed by its assembled `data-godot-path` so it persists across re-walks; if it
      // already carries this animation, leave it untouched (re-setting the shorthand would
      // restart the reveal). Read the shorthand we set (reliable across CSSOM impls).
      if (!el.style.animation.includes(ENTER)) {
        el.style.setProperty("--spirectl-enter-x", `${binding.offsetX ?? 0}px`);
        el.style.setProperty("--spirectl-enter-y", `${binding.offsetY ?? 0}px`);
        el.style.setProperty("--spirectl-enter-opacity-from", String(binding.opacityFrom ?? 1));
        const timing = godotEasingToCss(binding.ease, binding.trans);
        el.style.animation = `${ENTER} ${Math.max(1, binding.durationMs ?? 500)}ms ${timing} ${
          binding.loop === true ? "infinite" : "1"
        }`;
      }
      return true;
    }
  }
  return false;
}

export function clearAnimationBindings(root: ParentNode, paths: Iterable<string>, keep: ReadonlySet<string>): void {
  for (const path of paths) {
    if (keep.has(path)) continue;
    const el = root.querySelector<HTMLElement>(`[data-godot-path="${cssEscape(path)}"]`);
    if (!el) continue;
    el.style.animation = "";
    el.style.removeProperty("--spirectl-pulse-scale-from");
    el.style.removeProperty("--spirectl-pulse-scale-to");
    el.style.removeProperty("--spirectl-pulse-alpha-from");
    el.style.removeProperty("--spirectl-pulse-alpha-to");
    el.style.removeProperty("--spirectl-float-rise");
    el.style.removeProperty("--spirectl-float-scale-from");
    el.style.removeProperty("--spirectl-float-scale-to");
    el.style.removeProperty("--spirectl-bob-base");
    el.style.removeProperty("--spirectl-bob-amp");
    el.style.removeProperty("--spirectl-fly-dx");
    el.style.removeProperty("--spirectl-fly-dy");
    el.style.removeProperty("--spirectl-fly-arc");
    el.style.removeProperty("--spirectl-fly-scale-to");
    el.style.removeProperty("--spirectl-smoke-scale-to");
    el.style.removeProperty("--spirectl-enter-x");
    el.style.removeProperty("--spirectl-enter-y");
    el.style.removeProperty("--spirectl-enter-opacity-from");
    el.style.removeProperty("--spirectl-pulse-from");
    el.style.removeProperty("--spirectl-pulse-to");
    el.style.removeProperty("--spirectl-pulse-from-x");
    el.style.removeProperty("--spirectl-pulse-from-y");
    el.style.removeProperty("--spirectl-pulse-to-x");
    el.style.removeProperty("--spirectl-pulse-to-y");
  }
}

function normalizeBindings(
  bindings: readonly PresentationAnimationBinding[] | Record<string, unknown> | null | undefined,
): PresentationAnimationBinding[] {
  if (Array.isArray(bindings)) return bindings.filter(isAnimationBinding);
  if (!bindings || typeof bindings !== "object") return [];
  const out: PresentationAnimationBinding[] = [];
  for (const [path, value] of Object.entries(bindings)) {
    if (!value || typeof value !== "object") continue;
    const raw = value as Record<string, unknown>;
    const kind = typeof raw.kind === "string" ? raw.kind : "";
    if (!kind) continue;
    out.push({
      path,
      kind,
      durationMs: numberOrUndefined(raw.durationMs),
      scaleFrom: numberOrUndefined(raw.scaleFrom),
      scaleTo: numberOrUndefined(raw.scaleTo),
      alphaFrom: numberOrUndefined(raw.alphaFrom),
      alphaTo: numberOrUndefined(raw.alphaTo),
      ...(typeof raw.ease === "string" ? { ease: raw.ease } : {}),
      ...(typeof raw.trans === "string" ? { trans: raw.trans } : {}),
      offsetX: numberOrUndefined(raw.offsetX),
      offsetY: numberOrUndefined(raw.offsetY),
      opacityFrom: numberOrUndefined(raw.opacityFrom),
      risePx: numberOrUndefined(raw.risePx),
      amplitudePx: numberOrUndefined(raw.amplitudePx),
      baselineUpPx: numberOrUndefined(raw.baselineUpPx),
      amplitudeRad: numberOrUndefined(raw.amplitudeRad),
      delayMs: numberOrUndefined(raw.delayMs),
      pivotX: numberOrUndefined(raw.pivotX),
      pivotY: numberOrUndefined(raw.pivotY),
      fromX: numberOrUndefined(raw.fromX),
      fromY: numberOrUndefined(raw.fromY),
      toX: numberOrUndefined(raw.toX),
      toY: numberOrUndefined(raw.toY),
      loop: typeof raw.loop === "boolean" ? raw.loop : undefined,
    });
  }
  return out;
}

function isAnimationBinding(value: unknown): value is PresentationAnimationBinding {
  return !!value && typeof value === "object" && typeof (value as PresentationAnimationBinding).path === "string";
}

function numberOrUndefined(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

export function ensureAnimationStyles(root: ParentNode): void {
  const doc = root.nodeType === 9 ? (root as Document) : root.ownerDocument;
  if (!doc || doc.getElementById(STYLE_ID)) return;
  const style = doc.createElement("style");
  style.id = STYLE_ID;
  style.textContent = `
@keyframes ${PULSE_SCALE_FADE} {
  from {
    opacity: var(--spirectl-pulse-alpha-from, 0.4);
    transform: scale(var(--spirectl-pulse-scale-from, 0.5));
  }
  to {
    opacity: var(--spirectl-pulse-alpha-to, 0);
    transform: scale(var(--spirectl-pulse-scale-to, 0.7));
  }
}
@keyframes ${ROTATE} {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}
@keyframes ${FLOAT_FADE} {
  0% {
    opacity: 1;
    transform: translateY(0) scale(var(--spirectl-float-scale-from, 2.5));
  }
  60% {
    opacity: 1;
    transform: translateY(calc(var(--spirectl-float-rise, 260px) * -1)) scale(1.4);
  }
  100% {
    opacity: 0;
    transform: translateY(calc(var(--spirectl-float-rise, 260px) * -0.7)) scale(var(--spirectl-float-scale-to, 1));
  }
}
@keyframes ${INTENT_BOB} {
  from { transform: translateY(calc(-1 * (var(--spirectl-bob-base, 8px) + var(--spirectl-bob-amp, 10px)))); }
  to { transform: translateY(calc(var(--spirectl-bob-amp, 10px) - var(--spirectl-bob-base, 8px))); }
}
@keyframes ${INTENT_BOB_COMPOSE} {
  from { translate: 0 calc(-1 * (var(--spirectl-bob-base, 8px) + var(--spirectl-bob-amp, 10px))); }
  to { translate: 0 calc(var(--spirectl-bob-amp, 10px) - var(--spirectl-bob-base, 8px)); }
}
@keyframes ${BRIGHTEN} {
  from { filter: brightness(1); }
  to { filter: brightness(1.25); }
}
@property --spirectl-flame-scaley {
  syntax: "<number>";
  inherits: false;
  initial-value: 1;
}
@property --spirectl-flame-skew {
  syntax: "<angle>";
  inherits: false;
  initial-value: 0rad;
}
@keyframes ${FLAME_SCALEY} {
  from { --spirectl-flame-scaley: ${FLAME_SCALEY_MIN}; }
  to { --spirectl-flame-scaley: ${FLAME_SCALEY_MAX}; }
}
@keyframes ${FLAME_SKEW} {
  from { --spirectl-flame-skew: -${FLAME_SKEW_RAD}rad; }
  to { --spirectl-flame-skew: ${FLAME_SKEW_RAD}rad; }
}
@keyframes ${FLAME_SCALEY_LITERAL} {
  from { scale: 1 ${FLAME_SCALEY_MIN}; }
  to { scale: 1 ${FLAME_SCALEY_MAX}; }
}
@keyframes ${FLAME_SKEW_LITERAL} {
  from { transform: skewX(-${FLAME_SKEW_RAD}rad); }
  to { transform: skewX(${FLAME_SKEW_RAD}rad); }
}
@keyframes ${PIVOT_PULSE} {
  from {
    scale: var(--spirectl-pulse-from, 1);
    translate: var(--spirectl-pulse-from-x, 0px) var(--spirectl-pulse-from-y, 0px);
  }
  to {
    scale: var(--spirectl-pulse-to, 1);
    translate: var(--spirectl-pulse-to-x, 0px) var(--spirectl-pulse-to-y, 0px);
  }
}
@keyframes ${CARD_FLY} {
  0% { transform: translate(0, 0) scale(var(--spirectl-card-base, 0.8)); opacity: 1; }
  50% {
    transform: translate(calc(var(--spirectl-fly-dx, 0px) * 0.5), calc(var(--spirectl-fly-dy, 0px) * 0.5 - var(--spirectl-fly-arc, 0px))) scale(calc(var(--spirectl-card-base, 0.8) * 0.9));
    opacity: 1;
  }
  100% {
    transform: translate(var(--spirectl-fly-dx, 0px), var(--spirectl-fly-dy, 0px)) scale(calc(var(--spirectl-card-base, 0.8) * var(--spirectl-fly-scale-to, 0.6)));
    opacity: 0;
  }
}
@keyframes ${CARD_SMOKE} {
  0% { transform: scale(var(--spirectl-card-base, 0.8)); opacity: 1; filter: blur(0); }
  100% { transform: scale(calc(var(--spirectl-card-base, 0.8) * var(--spirectl-smoke-scale-to, 1.25))); opacity: 0; filter: blur(6px); }
}
@keyframes ${ENTER} {
  from {
    transform: translate(var(--spirectl-enter-x, 0px), var(--spirectl-enter-y, 0px));
    opacity: var(--spirectl-enter-opacity-from, 1);
  }
  to { transform: translate(0px, 0px); opacity: 1; }
}
@keyframes ${STATE_TRANSITION} {
  from {
    transform: translate(var(--spirectl-st-from-x, 0px), var(--spirectl-st-from-y, 0px));
  }
  to {
    transform: translate(var(--spirectl-st-to-x, 0px), var(--spirectl-st-to-y, 0px));
  }
}`;
  doc.head.appendChild(style);
}

function cssEscape(value: string): string {
  return value.replace(/["\\]/g, "\\$&");
}

/**
 * Static CSS transition declared on a node via catalog `props.transition` and applied
 * ALWAYS to the keyed element (post-mount). Unlike onFocus enter/exit transitions (run
 * imperatively by the interactivity layer), this makes ordinary CEL-driven prop changes
 * — the targeting reticle's modulate→opacity and scale→transform — animate instead of
 * snapping, because the renderer patches those props in place on a persistent element.
 *
 * Two forms: a flat `{ properties, durationMs, ease, trans }` (one timing for every
 * property) or `{ pieces: [{ property, durationMs, ease, trans }, ...] }` (a distinct
 * timing per property, e.g. opacity 200ms while transform runs 500ms ease-out expo).
 * Easing reuses the catalog's `transitionCss` (shared with the focus layer).
 */
export interface PresentationTransitionBinding {
  path: string;
  properties?: string[];
  durationMs?: number;
  ease?: string;
  trans?: string;
  pieces?: Array<{ property: string; durationMs?: number; ease?: string; trans?: string }>;
  /**
   * True for a renderer-SYNTHESIZED default value-change transition (not an explicit
   * catalog `props.transition`). Defaults get FIRST-OBSERVATION suppression: the inline
   * transition is installed only from the second committed paint of a path onward, so
   * the initial screen build / a fresh remount never animates every node from blank
   * (there is no prior value to ease from). Explicit catalog transitions apply
   * immediately (unchanged behavior).
   */
  isDefault?: boolean;
}

// Per-root memory of default-transition paths already seen at least once (first committed
// paint). A default transition is withheld on the first observation of a path and installed
// from the second onward. Keyed by the attach root (dev glue stage vs the Vue view never
// share), purged on path disappearance so a later re-appearance is a fresh first paint.
const defaultTransitionFirstPaint = new WeakMap<ParentNode, Set<string>>();

export function applyTransitionBindings(
  root: ParentNode,
  bindings: readonly PresentationTransitionBinding[] | Record<string, unknown> | null | undefined,
): Set<string> {
  const applied = new Set<string>();
  let seen: Set<string> | undefined;
  for (const binding of normalizeTransitionBindings(bindings)) {
    const el = root.querySelector<HTMLElement>(`[data-godot-path="${cssEscape(binding.path)}"]`);
    if (!el) continue;
    if (binding.isDefault) {
      if (!seen) {
        seen = defaultTransitionFirstPaint.get(root);
        if (!seen) {
          seen = new Set();
          defaultTransitionFirstPaint.set(root, seen);
        }
      }
      // Report the path as managed either way (so clear does not wipe it), but on the
      // FIRST observation record it and skip installing the transition — the node's
      // initial value paints without easing. From the second observation the default is
      // live and subsequent in-place value patches animate.
      applied.add(binding.path);
      if (!seen.has(binding.path)) {
        seen.add(binding.path);
        continue;
      }
    }
    const css = transitionCssFor(binding);
    if (!css) continue;
    el.style.transition = css;
    applied.add(binding.path);
  }
  return applied;
}

export function clearTransitionBindings(root: ParentNode, paths: Iterable<string>, keep: ReadonlySet<string>): void {
  const seen = defaultTransitionFirstPaint.get(root);
  for (const path of paths) {
    if (keep.has(path)) continue;
    // Forget the first-paint memory so a re-appearance is treated as a fresh first paint
    // (no spurious animation on remount), then clear the inline transition.
    if (seen) seen.delete(path);
    const el = root.querySelector<HTMLElement>(`[data-godot-path="${cssEscape(path)}"]`);
    if (!el) continue;
    el.style.transition = "";
  }
}

// ---------------------------------------------------------------------------
// Animation-hint pre-arm (out-of-band tween TIMING hints) — reusable pre-arm pass.
//
// A hint is emitted by the producer the moment the game CREATES a Godot property tween
// (one per animated property). It is NOT a tween to replay — it is a lightweight signal to
// arm a matching CSS transition TIMING (duration + easing) for the addressed node's CSS
// channel, so the imminent in-place prop patch (which rides the same/next state frame) eases
// with the tween's REAL timing instead of the renderer-wide 200ms default. Hints arrive from
// OUTSIDE the walk (a per-frame component input, like `resources`), carry a short client TTL,
// and are broad by design: matching is by `nodePath` SUFFIX so every mounted instance of that
// node pre-arms (precision limit: two scenes mounting the same relative node path both arm;
// harmless — a transition only makes a value change ease, and an unused arm expires).
// ---------------------------------------------------------------------------

export interface PresentationAnimationHint {
  /** Catalog-relative scene the tween addressed (e.g. "combat/health_bar"); advisory only. */
  scene?: string;
  /** Scene-relative node path (e.g. "HealthBar/Fill"); matched by data-godot-path SUFFIX. */
  nodePath: string;
  /** The CSS transition-property channel to arm: "opacity" | "transform" | "filter". */
  channel: string;
  durationMs: number;
  /** Godot EaseType name (or css `in`/`out`/`in-out`). */
  ease?: string;
  /** Godot TransitionType name (`expo`/`back`/…). */
  trans?: string;
}

// Per-root memory of the transition we OVERRODE for each hinted path, so a hint's expiry can
// restore the underlying default/explicit transition. Stores both the prior value (to restore)
// and the exact value we wrote (so we only restore when our override is still in place — if the
// transition pass re-wrote the element with a fresh default this frame, we leave that untouched).
// Keyed by the attach root (dev stage vs the Vue view never share).
const animationHintPrior = new WeakMap<ParentNode, Map<string, { prior: string; applied: string }>>();

function hintPriorMemo(root: ParentNode): Map<string, { prior: string; applied: string }> {
  let memo = animationHintPrior.get(root);
  if (!memo) {
    memo = new Map();
    animationHintPrior.set(root, memo);
  }
  return memo;
}

/**
 * Arm one-shot CSS transition TIMINGS from live animation hints. Runs AFTER the default/explicit
 * transition pass (so a hint wins by overriding just its channel while the node's other channels
 * keep the default). Returns the set of data-godot-paths actually armed (for {@link clearAnimationHintBindings}).
 * Idempotent per frame: re-arming the same hint re-writes the same channel piece.
 */
export function applyAnimationHintBindings(
  root: ParentNode,
  hints: readonly PresentationAnimationHint[] | null | undefined,
): Set<string> {
  const applied = new Set<string>();
  if (!hints || hints.length === 0) return applied;
  const memo = hintPriorMemo(root);
  for (const hint of hints) {
    const nodePath = typeof hint.nodePath === "string" ? hint.nodePath : "";
    const channel = typeof hint.channel === "string" ? hint.channel : "";
    if (!nodePath || !channel) continue;
    const durationMs = typeof hint.durationMs === "number" && Number.isFinite(hint.durationMs) ? hint.durationMs : 0;
    if (durationMs <= 0) continue;
    const timing = godotEasingToCss(hint.ease, hint.trans);
    const piece = `${channel} ${Math.max(1, durationMs)}ms ${timing}`;
    // Broad SUFFIX match: arm every mounted instance of this scene-relative node path (plus the
    // exact-path case for a root-mounted node). A `nodePath` may itself contain "/" segments.
    const escaped = cssEscape(nodePath);
    const els = root.querySelectorAll<HTMLElement>(
      `[data-godot-path$="/${escaped}"], [data-godot-path="${escaped}"]`,
    );
    for (const el of els) {
      const path = el.getAttribute("data-godot-path") ?? "";
      if (!path) continue;
      // Preserve the underlying transition (the default/explicit just applied) and MERGE the
      // hint's channel over it, so untouched channels keep their default timing.
      const current = el.style.transition ?? "";
      const merged = mergeTransitionChannel(current, channel, piece);
      if (!memo.has(path)) {
        // First arm this run for this path: remember what to fall back to on expiry.
        memo.set(path, { prior: current, applied: merged });
      } else {
        memo.get(path)!.applied = merged;
      }
      el.style.transition = merged;
      applied.add(path);
    }
  }
  return applied;
}

/**
 * Restore the underlying transition for paths that were armed last frame but are no longer hinted
 * (their TTL lapsed). Only restores when our override is STILL in place; if the transition pass
 * re-wrote the element with a fresh default this frame, that default is left untouched.
 */
export function clearAnimationHintBindings(root: ParentNode, paths: Iterable<string>, keep: ReadonlySet<string>): void {
  const memo = animationHintPrior.get(root);
  if (!memo) return;
  for (const path of paths) {
    if (keep.has(path)) continue;
    const record = memo.get(path);
    memo.delete(path);
    if (!record) continue;
    const el = root.querySelector<HTMLElement>(`[data-godot-path="${cssEscape(path)}"]`);
    if (!el) continue;
    if ((el.style.transition ?? "") === record.applied) {
      el.style.transition = record.prior;
    }
  }
}

// Merge a single transition channel piece into an existing `transition` shorthand: drop any
// existing piece for `channel`, then append the new one. Pieces are comma-separated; the
// transition-property is the first whitespace token of each piece.
function mergeTransitionChannel(current: string, channel: string, piece: string): string {
  const pieces = current
    .split(",")
    .map((p) => p.trim())
    .filter((p) => p.length > 0 && p.split(/\s+/)[0] !== channel);
  pieces.push(piece);
  return pieces.join(", ");
}

function transitionCssFor(binding: PresentationTransitionBinding): string {
  if (Array.isArray(binding.pieces) && binding.pieces.length > 0) {
    return binding.pieces
      .map((p) => transitionCss({ durationMs: p.durationMs, ease: p.ease, trans: p.trans }, [p.property]))
      .filter((piece) => piece !== "")
      .join(", ");
  }
  const properties =
    Array.isArray(binding.properties) && binding.properties.length > 0 ? binding.properties : ["opacity"];
  return transitionCss({ durationMs: binding.durationMs, ease: binding.ease, trans: binding.trans }, properties);
}

function normalizeTransitionBindings(
  bindings: readonly PresentationTransitionBinding[] | Record<string, unknown> | null | undefined,
): PresentationTransitionBinding[] {
  if (Array.isArray(bindings)) return bindings.filter(isTransitionBinding);
  if (!bindings || typeof bindings !== "object") return [];
  const out: PresentationTransitionBinding[] = [];
  for (const [path, value] of Object.entries(bindings)) {
    if (!value || typeof value !== "object") continue;
    const raw = value as Record<string, unknown>;
    out.push({
      path,
      ...(Array.isArray(raw.properties) ? { properties: raw.properties as string[] } : {}),
      ...(typeof raw.durationMs === "number" ? { durationMs: raw.durationMs } : {}),
      ...(typeof raw.ease === "string" ? { ease: raw.ease } : {}),
      ...(typeof raw.trans === "string" ? { trans: raw.trans } : {}),
      ...(Array.isArray(raw.pieces) ? { pieces: raw.pieces as PresentationTransitionBinding["pieces"] } : {}),
      ...(raw.isDefault === true ? { isDefault: true } : {}),
    });
  }
  return out;
}

function isTransitionBinding(value: unknown): value is PresentationTransitionBinding {
  return !!value && typeof value === "object" && typeof (value as PresentationTransitionBinding).path === "string";
}

// ---------------------------------------------------------------------------
// stateChange transition playback (Vehicle 1) — the node-level `transitions[]`
// array. Unlike `applyTransitionBindings` (a static CSS transition on CEL-driven
// prop changes), this plays a one-shot TRANSLATE SLIDE when a per-entry trigger
// value flips, reproducing the slide-in/slide-out a node plays when it is enabled or
// disabled, and the character-select info panel's re-slide on selection change.
// ---------------------------------------------------------------------------

/** One direction of a stateChange slide: relative translate offsets + Godot timing. */
export interface StateTransitionLeg {
  fromX: number;
  fromY: number;
  toX: number;
  toY: number;
  durationMs: number;
  ease?: string;
  trans?: string;
}

export interface PresentationStateTransitionBinding {
  path: string;
  /** Stable per-entry id (the catalog transition's `id`); one path may carry several. */
  key: string;
  /** The evaluated `trigger.stateKey` (boolean for OnEnable/OnDisable, or a value for re-slide). */
  value: unknown;
  /** Truthiness of `value` — drives OnEnable (false→true) / OnDisable (true→false). */
  active: boolean;
  /** Played on activation (false→true) and on a truthy value CHANGE (string-key re-slide). */
  enter: StateTransitionLeg;
  /** Played on deactivation (true→false); when absent, the enter leg is reversed. */
  exit?: StateTransitionLeg;
}

// Per-root memory of each entry's last observed (value, active), so a flip plays the right
// leg and — crucially — the FIRST observation applies the resting pose WITHOUT animating (no
// spurious slide on initial mount/page load). Keyed by the attach root so distinct mounts
// (dev glue stage vs the Vue view) never share state.
const stateTransitionMemory = new WeakMap<ParentNode, Map<string, { value: unknown; active: boolean }>>();

function stateMemo(root: ParentNode): Map<string, { value: unknown; active: boolean }> {
  let memo = stateTransitionMemory.get(root);
  if (!memo) {
    memo = new Map();
    stateTransitionMemory.set(root, memo);
  }
  return memo;
}

function reverseLeg(leg: StateTransitionLeg): StateTransitionLeg {
  return {
    fromX: leg.toX,
    fromY: leg.toY,
    toX: leg.fromX,
    toY: leg.fromY,
    durationMs: leg.durationMs,
    ...(leg.ease !== undefined ? { ease: leg.ease } : {}),
    ...(leg.trans !== undefined ? { trans: leg.trans } : {}),
  };
}

function playStateSlide(el: HTMLElement, leg: StateTransitionLeg): void {
  el.style.setProperty("--spirectl-st-from-x", `${leg.fromX}px`);
  el.style.setProperty("--spirectl-st-from-y", `${leg.fromY}px`);
  el.style.setProperty("--spirectl-st-to-x", `${leg.toX}px`);
  el.style.setProperty("--spirectl-st-to-y", `${leg.toY}px`);
  const timing = godotEasingToCss(leg.ease, leg.trans);
  // Restart reliably even when the shorthand is byte-identical to a prior play (a Shape-A
  // re-slide reuses the same from→to): reset, force a reflow, then re-assign. No `forwards`
  // — the slide is a TRANSIENT overlay that releases to the node's natural resting transform
  // (rest for enter; the node's own props/visibility park it for exit), so it never fights the
  // node's authored `props.position`/layout at rest.
  el.style.animation = "none";
  void el.offsetWidth;
  el.style.animation = `${STATE_TRANSITION} ${Math.max(1, leg.durationMs)}ms ${timing} 1`;
}

/**
 * Play stateChange slides for the node-level `transitions[]` vehicle. Idempotent per render:
 * only an entry whose trigger value actually FLIPPED since the last call plays; the first
 * observation records the resting pose silently. Returns the set of managed paths (for clear).
 */
export function applyStateTransitionBindings(
  root: ParentNode,
  bindings:
    | readonly PresentationStateTransitionBinding[]
    | Record<string, unknown>
    | null
    | undefined,
): Set<string> {
  ensureAnimationStyles(root);
  const applied = new Set<string>();
  const memo = stateMemo(root);
  for (const binding of normalizeStateTransitionBindings(bindings)) {
    applied.add(binding.path);
    const memoKey = `${binding.path} ${binding.key}`;
    const prev = memo.get(memoKey);
    memo.set(memoKey, { value: binding.value, active: binding.active });
    if (prev === undefined) continue; // first observation: rest without animating
    let leg: StateTransitionLeg | undefined;
    if (!prev.active && binding.active) {
      leg = binding.enter; // OnEnable: slide in from the offset to rest
    } else if (prev.active && !binding.active) {
      leg = binding.exit ?? reverseLeg(binding.enter); // OnDisable: slide out (reverse if no exit)
    } else if (binding.active && !Object.is(prev.value, binding.value)) {
      leg = binding.enter; // truthy value changed (string-key re-slide, e.g. selected character)
    }
    if (!leg) continue;
    const el = root.querySelector<HTMLElement>(`[data-godot-path="${cssEscape(binding.path)}"]`);
    if (!el) continue;
    playStateSlide(el, leg);
  }
  return applied;
}

export function clearStateTransitionBindings(
  root: ParentNode,
  paths: Iterable<string>,
  keep: ReadonlySet<string>,
): void {
  const memo = stateTransitionMemory.get(root);
  for (const path of paths) {
    if (keep.has(path)) continue;
    // Forget the tracked state so a later re-appearance is treated as a fresh first
    // observation (no spurious slide on remount), and clear the inline animation + vars.
    if (memo) {
      for (const memoKey of Array.from(memo.keys())) {
        if (memoKey === path || memoKey.startsWith(`${path} `)) memo.delete(memoKey);
      }
    }
    const el = root.querySelector<HTMLElement>(`[data-godot-path="${cssEscape(path)}"]`);
    if (!el) continue;
    el.style.animation = "";
    el.style.removeProperty("--spirectl-st-from-x");
    el.style.removeProperty("--spirectl-st-from-y");
    el.style.removeProperty("--spirectl-st-to-x");
    el.style.removeProperty("--spirectl-st-to-y");
  }
}

function normalizeStateTransitionBindings(
  bindings:
    | readonly PresentationStateTransitionBinding[]
    | Record<string, unknown>
    | null
    | undefined,
): PresentationStateTransitionBinding[] {
  if (Array.isArray(bindings)) return bindings.filter(isStateTransitionBinding);
  if (!bindings || typeof bindings !== "object") return [];
  // Record form: path -> entry[] (the payload's stateTransitionBindingsByPath shape).
  const out: PresentationStateTransitionBinding[] = [];
  for (const [path, value] of Object.entries(bindings)) {
    if (!Array.isArray(value)) continue;
    for (const entry of value) {
      const normalized = normalizeStateTransitionEntry(path, entry);
      if (normalized) out.push(normalized);
    }
  }
  return out;
}

function normalizeStateTransitionEntry(
  path: string,
  entry: unknown,
): PresentationStateTransitionBinding | null {
  if (!entry || typeof entry !== "object") return null;
  const raw = entry as Record<string, unknown>;
  const key = typeof raw.key === "string" ? raw.key : "";
  const enter = normalizeLeg(raw.enter);
  if (!key || !enter) return null;
  return {
    path,
    key,
    value: raw.value ?? null,
    active: raw.active === true,
    enter,
    ...(normalizeLeg(raw.exit) ? { exit: normalizeLeg(raw.exit)! } : {}),
  };
}

function normalizeLeg(value: unknown): StateTransitionLeg | undefined {
  if (!value || typeof value !== "object") return undefined;
  const raw = value as Record<string, unknown>;
  const num = (v: unknown): number => (typeof v === "number" && Number.isFinite(v) ? v : 0);
  return {
    fromX: num(raw.fromX),
    fromY: num(raw.fromY),
    toX: num(raw.toX),
    toY: num(raw.toY),
    durationMs: typeof raw.durationMs === "number" && Number.isFinite(raw.durationMs) ? raw.durationMs : 300,
    ...(typeof raw.ease === "string" ? { ease: raw.ease } : {}),
    ...(typeof raw.trans === "string" ? { trans: raw.trans } : {}),
  };
}

function isStateTransitionBinding(value: unknown): value is PresentationStateTransitionBinding {
  return (
    !!value &&
    typeof value === "object" &&
    typeof (value as PresentationStateTransitionBinding).path === "string" &&
    typeof (value as PresentationStateTransitionBinding).key === "string" &&
    !!(value as PresentationStateTransitionBinding).enter
  );
}
