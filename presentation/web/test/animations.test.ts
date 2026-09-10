// @vitest-environment happy-dom
//
// Locks the post-mount CSS-animation application, with focus on the one-shot
// `floatFade` damage-number kind: it plays ONCE (iteration "1", `forwards` so it
// rests faded out), is IDEMPOTENT across re-applies (the post-mount effect re-runs on
// every scene/resource settle, but the keyed node must not restart its animation), and
// is cleaned up (style + custom props) when its binding path disappears.
import { describe, expect, it } from "vitest";

import {
  applyAnimationBinding,
  applyAnimationBindings,
  clearAnimationBindings,
  applyTransitionBindings,
  clearTransitionBindings,
  applyStateTransitionBindings,
  clearStateTransitionBindings,
  applyAnimationHintBindings,
  clearAnimationHintBindings,
} from "../src/render/animations";

function nodeRoot(...paths: string[]): HTMLElement {
  const root = document.createElement("div");
  for (const path of paths) {
    const el = document.createElement("div");
    el.setAttribute("data-godot-path", path);
    root.appendChild(el);
  }
  return root;
}

const at = (root: ParentNode, path: string): HTMLElement =>
  root.querySelector<HTMLElement>(`[data-godot-path="${path}"]`)!;

/** The static (constant) keyframe sheet. */
const staticSheet = (): string =>
  document.getElementById("spirectl-presentation-animations")?.textContent ?? "";

/** The dynamic sheet holding the value-keyed LITERAL keyframes (one rule per distinct value tuple). */
const literalSheet = (): string =>
  document.getElementById("spirectl-presentation-animations-literal")?.textContent ?? "";

/**
 * True when `name` appears as a whole animation-name token in the element's `animation` shorthand. A plain
 * `toContain` would also match a longer value-keyed name that merely starts with `name`
 * (`spirectl-intent-bob` vs `spirectl-intent-bob-n18-2`), which is exactly what these tests must tell apart.
 */
const usesAnimation = (el: HTMLElement, name: string): boolean =>
  new RegExp(`(^|[\\s,])${name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}([\\s,]|$)`).test(el.style.animation);

const occurrences = (haystack: string, needle: string): number => haystack.split(needle).length - 1;

describe("floatFade one-shot damage-number animation", () => {
  it("plays once (iteration 1, forwards) and sets the rise/scale custom props", () => {
    const root = nodeRoot("CombatVfxContainer/__rep_fx/fx:1");
    applyAnimationBindings(root, [
      { path: "CombatVfxContainer/__rep_fx/fx:1", kind: "floatFade", durationMs: 2000, risePx: 220, scaleFrom: 2.5, scaleTo: 1, loop: false },
    ]);
    const el = at(root, "CombatVfxContainer/__rep_fx/fx:1");
    expect(el.style.animation).toContain("spirectl-float-fade");
    expect(el.style.animation).toContain("2000ms");
    expect(el.style.animation).toContain("forwards");
    // loop:false → a single iteration (the standalone "1" token).
    expect(el.style.animation.split(/\s+/)).toContain("1");
    expect(el.style.getPropertyValue("--spirectl-float-rise")).toBe("220px");
    expect(el.style.getPropertyValue("--spirectl-float-scale-from")).toBe("2.5");
    expect(el.style.getPropertyValue("--spirectl-float-scale-to")).toBe("1");
  });

  it("is idempotent across re-applies (does NOT restart the one-shot)", () => {
    const root = nodeRoot("fx:1");
    const binding = { path: "fx:1", kind: "floatFade", durationMs: 2000, risePx: 220, loop: false } as const;
    applyAnimationBindings(root, [binding]);
    const el = at(root, "fx:1");
    const firstAnim = el.style.animation;
    expect(firstAnim).toContain("spirectl-float-fade");
    // A scene/resource settle re-runs the post-mount apply on the SAME (keyed) element.
    // Setting `style.animation` again would restart the animation, so the apply must
    // detect the existing one-shot and leave it untouched. We assert the shorthand is
    // byte-identical AND the path stays reported as applied.
    const applied = applyAnimationBindings(root, [binding]);
    expect(el.style.animation).toBe(firstAnim);
    expect(applied.has("fx:1")).toBe(true);
  });

  it("clears the float style + custom props when the path disappears", () => {
    const root = nodeRoot("fx:1");
    const first = applyAnimationBindings(root, [
      { path: "fx:1", kind: "floatFade", durationMs: 2000, risePx: 220, loop: false },
    ]);
    // Next walk: the effect aged out, so its path is no longer animated.
    const second = applyAnimationBindings(root, []);
    clearAnimationBindings(root, first, second);
    const el = at(root, "fx:1");
    expect(el.style.animation).toBe("");
    expect(el.style.getPropertyValue("--spirectl-float-rise")).toBe("");
    expect(el.style.getPropertyValue("--spirectl-float-scale-from")).toBe("");
  });

  it("defaults rise/scale when omitted", () => {
    const root = nodeRoot("fx:1");
    applyAnimationBindings(root, [{ path: "fx:1", kind: "floatFade" }]);
    const el = at(root, "fx:1");
    expect(el.style.getPropertyValue("--spirectl-float-rise")).toBe("260px");
    expect(el.style.getPropertyValue("--spirectl-float-scale-from")).toBe("2.5");
    expect(el.style.animation).toContain("2000ms"); // default duration
  });

  it("still drives the existing pulseScaleFade/rotate kinds", () => {
    const root = nodeRoot("a", "b");
    const applied = applyAnimationBindings(root, [
      { path: "a", kind: "pulseScaleFade", durationMs: 1500 },
      { path: "b", kind: "rotate", durationMs: 1000, loop: false },
    ]);
    expect(applied.has("a")).toBe(true);
    expect(applied.has("b")).toBe(true);
    expect(at(root, "a").style.animation).toContain("spirectl-pulse-scale-fade");
    expect(at(root, "b").style.animation).toContain("spirectl-rotate");
  });
});

describe("intent bob animation", () => {
  it("sets the bob vars + an eased-sine alternating animation with a negative phase delay", () => {
    const root = nodeRoot("IntentHolder");
    const applied = applyAnimationBindings(root, [
      { path: "IntentHolder", kind: "bob", durationMs: 2000, amplitudePx: 10, baselineUpPx: 8, delayMs: 250 },
    ]);
    const el = at(root, "IntentHolder");
    expect(applied.has("IntentHolder")).toBe(true);
    expect(el.style.getPropertyValue("--spirectl-bob-base")).toBe("8px");
    expect(el.style.getPropertyValue("--spirectl-bob-amp")).toBe("10px");
    expect(el.style.animation).toContain("spirectl-intent-bob");
    // Half the full sine period: `alternate` plays peak→trough then back = one cycle.
    expect(el.style.animation).toContain("1000ms");
    // easeInOutSine timing + alternate = a true sine (slow at the extrema, fast through
    // the baseline), not the constant-velocity triangle a `linear` bob would give.
    expect(el.style.animation).toContain("cubic-bezier(0.37, 0, 0.63, 1)");
    expect(el.style.animation).toContain("alternate");
    expect(el.style.animation).toContain("infinite");
    // The per-intent phase is seeded as a NEGATIVE animation-delay so the icon starts
    // mid-cycle (no startup stagger) — delayMs 250 → "-250ms".
    expect(el.style.animation).toContain("-250ms");
  });

  it("defaults amplitude/baseline/duration when omitted", () => {
    const root = nodeRoot("IntentHolder");
    applyAnimationBindings(root, [{ path: "IntentHolder", kind: "bob" }]);
    const el = at(root, "IntentHolder");
    expect(el.style.getPropertyValue("--spirectl-bob-base")).toBe("8px");
    expect(el.style.getPropertyValue("--spirectl-bob-amp")).toBe("10px");
    // Default full period 2000ms → 1000ms half-period iterations.
    expect(el.style.animation).toContain("1000ms");
  });

  it("uses VALUE-KEYED LITERAL keyframes (no var()/calc(), so Chrome can composite them)", () => {
    const root = nodeRoot("IntentHolder");
    applyAnimationBindings(root, [{ path: "IntentHolder", kind: "bob" }]);
    const el = at(root, "IntentHolder");
    // The name carries its own value key: baseline 8 + amplitude 10 → translateY(-18px) ↔ translateY(2px)
    // ("-18" → "n18"). The legacy shared rule is NOT what runs.
    expect(usesAnimation(el, "spirectl-intent-bob-n18-2")).toBe(true);
    expect(usesAnimation(el, "spirectl-intent-bob")).toBe(false);
    const sheet = literalSheet();
    expect(sheet).toContain("@keyframes spirectl-intent-bob-n18-2");
    expect(sheet).toContain("transform: translateY(-18px)");
    expect(sheet).toContain("transform: translateY(2px)");
    // The whole point: a keyframe that reads var()/calc() is never compositor-eligible.
    expect(sheet).not.toContain("var(");
    expect(sheet).not.toContain("calc(");
  });

  it("bakes non-default amplitude/baseline into their own literal rule", () => {
    const root = nodeRoot("IntentHolder");
    applyAnimationBindings(root, [
      { path: "IntentHolder", kind: "bob", amplitudePx: 4, baselineUpPx: 20 },
    ]);
    // top = -(20 + 4) = -24, bottom = 4 - 20 = -16.
    expect(usesAnimation(at(root, "IntentHolder"), "spirectl-intent-bob-n24-n16")).toBe(true);
    expect(literalSheet()).toContain("transform: translateY(-24px)");
    expect(literalSheet()).toContain("transform: translateY(-16px)");
  });

  it("SHARES one rule per value tuple (two same-tuple intents → a single @keyframes)", () => {
    const root = nodeRoot("IntentA", "IntentB", "IntentC");
    applyAnimationBindings(root, [
      { path: "IntentA", kind: "bob", amplitudePx: 12, baselineUpPx: 6 },
      { path: "IntentB", kind: "bob", amplitudePx: 12, baselineUpPx: 6 },
      { path: "IntentC", kind: "bob", amplitudePx: 12, baselineUpPx: 7 },
    ]);
    // Same tuple → the SAME animation-name on both elements, injected exactly once.
    expect(at(root, "IntentA").style.animation).toBe(at(root, "IntentB").style.animation);
    expect(usesAnimation(at(root, "IntentA"), "spirectl-intent-bob-n18-6")).toBe(true);
    expect(occurrences(literalSheet(), "@keyframes spirectl-intent-bob-n18-6")).toBe(1);
    // A different tuple gets its own rule (top -19, bottom 5).
    expect(usesAnimation(at(root, "IntentC"), "spirectl-intent-bob-n19-5")).toBe(true);
    expect(occurrences(literalSheet(), "@keyframes spirectl-intent-bob-n19-5")).toBe(1);
    // Re-applying the same bindings must not duplicate the rules.
    applyAnimationBindings(root, [
      { path: "IntentA", kind: "bob", amplitudePx: 12, baselineUpPx: 6 },
      { path: "IntentB", kind: "bob", amplitudePx: 12, baselineUpPx: 6 },
    ]);
    expect(occurrences(literalSheet(), "@keyframes spirectl-intent-bob-n18-6")).toBe(1);
  });

  it("compose mode literalizes the individual `translate:` keyframes (own rule family)", () => {
    const el = document.createElement("div");
    document.body.appendChild(el);
    applyAnimationBinding(el, { path: "IntentHolder", kind: "bob" }, { compose: true });
    expect(usesAnimation(el, "spirectl-intent-bob-compose-n18-2")).toBe(true);
    const sheet = literalSheet();
    expect(sheet).toContain("@keyframes spirectl-intent-bob-compose-n18-2");
    expect(sheet).toContain("translate: 0 -18px");
    expect(sheet).toContain("translate: 0 2px");
  });

  it("legacyBobKeyframes restores the shared var()/calc() rule (kill switch)", () => {
    const root = nodeRoot("IntentHolder");
    applyAnimationBindings(root, [{ path: "IntentHolder", kind: "bob" }], { legacyBobKeyframes: true });
    const el = at(root, "IntentHolder");
    expect(usesAnimation(el, "spirectl-intent-bob")).toBe(true);
    // The custom properties the legacy rule reads are still written by BOTH paths.
    expect(el.style.getPropertyValue("--spirectl-bob-base")).toBe("8px");
    expect(el.style.getPropertyValue("--spirectl-bob-amp")).toBe("10px");
    expect(staticSheet()).toContain("var(--spirectl-bob-amp");
    // …and the compose variant likewise.
    const composed = document.createElement("div");
    document.body.appendChild(composed);
    applyAnimationBinding(composed, { path: "x", kind: "bob" }, { compose: true, legacyBobKeyframes: true });
    expect(usesAnimation(composed, "spirectl-intent-bob-compose")).toBe(true);
  });

  it("clears the bob vars when the path disappears", () => {
    const root = nodeRoot("IntentHolder");
    const first = applyAnimationBindings(root, [{ path: "IntentHolder", kind: "bob" }]);
    const second = applyAnimationBindings(root, []);
    clearAnimationBindings(root, first, second);
    const el = at(root, "IntentHolder");
    expect(el.style.animation).toBe("");
    expect(el.style.getPropertyValue("--spirectl-bob-base")).toBe("");
    expect(el.style.getPropertyValue("--spirectl-bob-amp")).toBe("");
  });
});

describe("rock rotation oscillation (producer-pinned top-bar icon loops)", () => {
  it("emits literal ±amplitude deg keyframes on the individual `rotate:` property", () => {
    const root = nodeRoot("Control/Icon");
    const applied = applyAnimationBindings(root, [
      // The map-button oscillation: two 0.8s Sine/InOut legs between ∓0.12 rad (Sts2TopBarFold.MapRock*).
      { path: "Control/Icon", kind: "rock", durationMs: 1600, amplitudeRad: 0.12, delayMs: 400 },
    ]);
    const el = at(root, "Control/Icon");
    expect(applied.has("Control/Icon")).toBe(true);
    // 0.12 rad = 6.8755deg; the name carries the amplitude so same-amplitude icons share one rule.
    expect(usesAnimation(el, "spirectl-rock-6p8755")).toBe(true);
    const sheet = literalSheet();
    expect(sheet).toContain("@keyframes spirectl-rock-6p8755");
    expect(sheet).toContain("rotate: -6.8755deg");
    expect(sheet).toContain("rotate: 6.8755deg");
    // The caller passes the FULL period; one `alternate` iteration is the half-period.
    expect(el.style.animation).toContain("800ms");
    expect(el.style.animation).toContain("cubic-bezier(0.37, 0, 0.63, 1)");
    expect(el.style.animation).toContain("alternate");
    expect(el.style.animation).toContain("infinite");
    // Per-icon phase as a negative delay, exactly like the bob.
    expect(el.style.animation).toContain("-400ms");
  });

  it("defaults to 0.12 rad / 1600ms and shares the rule across icons", () => {
    const root = nodeRoot("Deck/Icon", "Map/Icon");
    applyAnimationBindings(root, [
      { path: "Deck/Icon", kind: "rock" },
      { path: "Map/Icon", kind: "rock" },
    ]);
    expect(at(root, "Deck/Icon").style.animation).toContain("800ms");
    expect(usesAnimation(at(root, "Map/Icon"), "spirectl-rock-6p8755")).toBe(true);
    expect(occurrences(literalSheet(), "@keyframes spirectl-rock-6p8755")).toBe(1);
  });

  it("honours pivotX/pivotY as the transform-origin; omitting them leaves the origin alone", () => {
    const root = nodeRoot("Pivoted/Icon", "Bare/Icon");
    applyAnimationBindings(root, [
      { path: "Pivoted/Icon", kind: "rock", pivotX: 32, pivotY: 24 },
      { path: "Bare/Icon", kind: "rock" },
    ]);
    expect(at(root, "Pivoted/Icon").style.transformOrigin).toBe("32px 24px");
    expect(at(root, "Bare/Icon").style.transformOrigin).toBe("");
  });

  it("keys a distinct amplitude to its own rule; loop:false collapses to one iteration", () => {
    const root = nodeRoot("Wide/Icon");
    applyAnimationBindings(root, [
      { path: "Wide/Icon", kind: "rock", amplitudeRad: 0.24, loop: false },
    ]);
    const el = at(root, "Wide/Icon");
    expect(usesAnimation(el, "spirectl-rock-13p751")).toBe(true); // 0.24 rad = 13.751deg
    expect(literalSheet()).toContain("rotate: 13.751deg");
    expect(el.style.animation).not.toContain("infinite");
  });
});

describe("pulseScaleFade (the end-turn button's glow pulse)", () => {
  it("defaults to the animator's own values as ONE literal compositor-eligible rule", () => {
    const root = nodeRoot("Visuals/GlowVfx");
    const applied = applyAnimationBindings(root, [{ path: "Visuals/GlowVfx", kind: "pulseScaleFade" }]);
    const el = at(root, "Visuals/GlowVfx");
    expect(applied.has("Visuals/GlowVfx")).toBe(true);
    // scale 0.5 → 0.7 in parallel with alpha 0.4 → 0 — the loop's own endpoints (bridge-mod Sts2EndTurnGlowFold).
    expect(usesAnimation(el, "spirectl-pulse-scale-fade-0p5-0p7-0p4-0")).toBe(true);
    const sheet = literalSheet();
    expect(sheet).toContain("@keyframes spirectl-pulse-scale-fade-0p5-0p7-0p4-0");
    expect(sheet).toContain("from { opacity: 0.4; transform: scale(0.5); }");
    expect(sheet).toContain("to { opacity: 0; transform: scale(0.7); }");
    // No var()/calc() anywhere in the rule — that is the whole point of literalizing it.
    expect(sheet).not.toContain("var(--spirectl-pulse-scale-from");
    // The loop RESTARTS from its start values every cycle (both tween legs carry `.From()`), so never `alternate`.
    expect(el.style.animation).toContain("1500ms");
    expect(el.style.animation).toContain("infinite");
    expect(el.style.animation).not.toContain("alternate");
    // The custom properties are still written so a host/devtools can read them (and `legacyPulseKeyframes` works).
    expect(el.style.getPropertyValue("--spirectl-pulse-scale-from")).toBe("0.5");
    expect(el.style.getPropertyValue("--spirectl-pulse-alpha-from")).toBe("0.4");
  });

  it("takes the CouchCoop mirror's RATIO endpoints (the producer baked the loop's start values in)", () => {
    const root = nodeRoot("Visuals/GlowVfx");
    applyAnimationBindings(root, [
      {
        path: "Visuals/GlowVfx",
        kind: "pulseScaleFade",
        durationMs: 1500,
        scaleFrom: 1,
        scaleTo: 1.4,
        alphaFrom: 1,
        alphaTo: 0,
        delayMs: 375,
      },
    ]);
    const el = at(root, "Visuals/GlowVfx");
    // 0.7/0.5 = 1.4 and 0/0.4 = 0 — see Sts2EndTurnGlowFold.ReplayScaleTo / ReplayAlphaTo.
    expect(usesAnimation(el, "spirectl-pulse-scale-fade-1-1p4-1-0")).toBe(true);
    expect(literalSheet()).toContain("from { opacity: 1; transform: scale(1); }");
    expect(literalSheet()).toContain("to { opacity: 0; transform: scale(1.4); }");
    // A per-node phase rides as a NEGATIVE delay, like every other loop here.
    expect(el.style.animation).toContain("-375ms");
  });

  it("legacyPulseKeyframes restores the shared var()-driven rule verbatim", () => {
    const root = nodeRoot("Visuals/GlowVfx");
    applyAnimationBindings(root, [{ path: "Visuals/GlowVfx", kind: "pulseScaleFade" }], {
      legacyPulseKeyframes: true,
    });
    const el = at(root, "Visuals/GlowVfx");
    expect(el.style.animation).toContain("spirectl-pulse-scale-fade 1500ms");
    expect(el.style.getPropertyValue("--spirectl-pulse-scale-to")).toBe("0.7");
  });

  it("SHARES one rule per value tuple (two glows with the same endpoints → a single @keyframes)", () => {
    const root = nodeRoot("a", "b");
    applyAnimationBindings(root, [
      { path: "a", kind: "pulseScaleFade", scaleFrom: 1, scaleTo: 1.4, alphaFrom: 1, alphaTo: 0 },
      { path: "b", kind: "pulseScaleFade", scaleFrom: 1, scaleTo: 1.4, alphaFrom: 1, alphaTo: 0 },
    ]);
    expect(occurrences(literalSheet(), "@keyframes spirectl-pulse-scale-fade-1-1p4-1-0")).toBe(1);
  });
});

describe("glowPulse opacity loop (proceed-button glow replay)", () => {
  it("defaults to the Sts2ProceedGlow constants: 1 ↔ 0.33333, 500ms linear alternate", () => {
    const root = nodeRoot("Image/Outline");
    const applied = applyAnimationBindings(root, [{ path: "Image/Outline", kind: "glowPulse" }]);
    const el = at(root, "Image/Outline");
    expect(applied.has("Image/Outline")).toBe(true);
    expect(usesAnimation(el, "spirectl-glow-pulse-1-0p33333")).toBe(true);
    const sheet = literalSheet();
    expect(sheet).toContain("@keyframes spirectl-glow-pulse-1-0p33333");
    // The client MULTIPLIES the producer's pinned alpha (0.75) by this opacity, so the far endpoint is the
    // ratio 0.25/0.75 = 1/3 and the cycle starts/ends at 1 = the pinned value (Sts2ProceedGlow.PinnedAlpha).
    expect(sheet).toContain("from { opacity: 1; }");
    expect(sheet).toContain("to { opacity: 0.33333; }");
    // One 0.5s LINEAR leg per iteration (the tween's two legs, default transition), looping forever.
    expect(el.style.animation).toContain("500ms");
    expect(el.style.animation).toContain("linear");
    expect(el.style.animation).toContain("alternate");
    expect(el.style.animation).toContain("infinite");
    // No easing curve: this loop's legs are linear, unlike every sine loop in this module.
    expect(el.style.animation).not.toContain("cubic-bezier");
  });

  it("accepts period + ratio overrides via durationMs/alphaFrom/alphaTo", () => {
    const root = nodeRoot("Image/Outline");
    applyAnimationBindings(root, [
      { path: "Image/Outline", kind: "glowPulse", durationMs: 2000, alphaFrom: 1, alphaTo: 0.5, delayMs: 250 },
    ]);
    const el = at(root, "Image/Outline");
    expect(usesAnimation(el, "spirectl-glow-pulse-1-0p5")).toBe(true);
    expect(literalSheet()).toContain("to { opacity: 0.5; }");
    expect(el.style.animation).toContain("1000ms"); // full period 2000 → 1000ms half-period legs
    expect(el.style.animation).toContain("-250ms");
  });
});

describe("rotate spin pivot (energy orbs vs. the top-bar settings icon)", () => {
  it("writes pivotX/pivotY to transform-origin when provided", () => {
    const root = nodeRoot("Control/Icon");
    applyAnimationBindings(root, [
      { path: "Control/Icon", kind: "rotate", durationMs: 6283.2, pivotX: 32, pivotY: 32 },
    ]);
    const el = at(root, "Control/Icon");
    expect(el.style.transformOrigin).toBe("32px 32px");
    expect(usesAnimation(el, "spirectl-rotate")).toBe(true);
    expect(el.style.animation).toContain("linear");
  });

  it("leaves pivotless callers pixel-unchanged (no transform-origin written at all)", () => {
    const root = nodeRoot("RotationLayers/Orb");
    applyAnimationBindings(root, [{ path: "RotationLayers/Orb", kind: "rotate", durationMs: 10000 }]);
    const el = at(root, "RotationLayers/Orb");
    expect(el.style.transformOrigin).toBe("");
    expect(el.style.animation).toBe("spirectl-rotate 10000ms linear infinite");
  });

  it("treats a single provided coordinate as (x, 0) / (0, y)", () => {
    const root = nodeRoot("OnlyY");
    applyAnimationBindings(root, [{ path: "OnlyY", kind: "rotate", pivotY: 16 }]);
    expect(at(root, "OnlyY").style.transformOrigin).toBe("0px 16px");
  });
});

describe("flameFlicker candle-fire loop (Tezcatara)", () => {
  it("drives scaleY + skew as two LITERAL compositor-eligible tracks with a per-flame negative phase", () => {
    const root = nodeRoot("Fire/SteppedFireMix");
    const applied = applyAnimationBindings(root, [
      { path: "Fire/SteppedFireMix", kind: "flameFlicker", delayMs: 800 },
    ]);
    const el = at(root, "Fire/SteppedFireMix");
    expect(applied.has("Fire/SteppedFireMix")).toBe(true);
    // Anchored at the paint-box bottom-center (fire rises from its base).
    expect(el.style.transformOrigin).toBe("50% 100%");
    // No static custom-prop transform any more: the two tracks write DIFFERENT properties (individual `scale:`
    // and `transform:`), which is what lets both run without a shared shorthand to clobber — and off the main
    // thread, unlike the @property custom props they replace.
    expect(el.style.transform).toBe("");
    // Two comma-separated tracks: scaleY half-period 850ms + skew half-period 1300ms, both looping + alternate.
    expect(el.style.animation).toContain("spirectl-flame-scaley-lit 850ms");
    expect(el.style.animation).toContain("spirectl-flame-skew-lit 1300ms");
    expect(el.style.animation).toContain("alternate");
    expect(el.style.animation).toContain("infinite");
    // easeInOutSine timing → each alternate half-leg is a true cosine, matching the native sin() port.
    expect(el.style.animation).toContain("cubic-bezier(0.37, 0, 0.63, 1)");
    // The per-flame phase is a NEGATIVE delay on both tracks (delayMs 800 → "-800ms").
    expect(el.style.animation).toContain("-800ms");
    const styles = staticSheet();
    // Same amplitudes as the legacy tracks (±8% scaleY, ±0.1rad skew), now as literal values.
    expect(styles).toContain("@keyframes spirectl-flame-scaley-lit");
    expect(styles).toContain("scale: 1 0.92");
    expect(styles).toContain("scale: 1 1.08");
    expect(styles).toContain("@keyframes spirectl-flame-skew-lit");
    expect(styles).toContain("transform: skewX(-0.1rad)");
    expect(styles).toContain("transform: skewX(0.1rad)");
  });

  it("legacyFlameKeyframes restores the @property custom-prop tracks verbatim", () => {
    const root = nodeRoot("Fire/SteppedFireMix");
    applyAnimationBindings(
      root,
      [{ path: "Fire/SteppedFireMix", kind: "flameFlicker", delayMs: 800 }],
      { legacyFlameKeyframes: true },
    );
    const el = at(root, "Fire/SteppedFireMix");
    expect(el.style.transformOrigin).toBe("50% 100%");
    // A single static transform reads BOTH animated custom props (no keyframe clobbers the shared transform).
    expect(el.style.transform).toContain("skewX(var(--spirectl-flame-skew");
    expect(el.style.transform).toContain("scaleY(var(--spirectl-flame-scaley");
    expect(el.style.animation).toContain("spirectl-flame-scaley 850ms");
    expect(el.style.animation).toContain("spirectl-flame-skew 1300ms");
    const styles = staticSheet();
    expect(styles).toContain("@keyframes spirectl-flame-scaley");
    expect(styles).toContain("@keyframes spirectl-flame-skew");
    // The custom props are @property-registered so they interpolate (and the static transform can read them).
    expect(styles).toContain("@property --spirectl-flame-scaley");
    expect(styles).toContain("@property --spirectl-flame-skew");
  });

  it("loop:false collapses both tracks to a single iteration; omitted delay → 0ms phase", () => {
    const root = nodeRoot("Fire/SteppedFireAdd");
    applyAnimationBindings(root, [{ path: "Fire/SteppedFireAdd", kind: "flameFlicker", loop: false }]);
    const el = at(root, "Fire/SteppedFireAdd");
    expect(el.style.animation).not.toContain("infinite");
    expect(el.style.animation).toContain("0ms");
  });
});

describe("static base-prop transitions", () => {
  it("composes a per-property transition from `pieces` (opacity + transform, distinct timing)", () => {
    const root = nodeRoot("SelectionReticle");
    const applied = applyTransitionBindings(root, {
      SelectionReticle: {
        pieces: [
          { property: "opacity", durationMs: 200, ease: "out" },
          { property: "transform", durationMs: 500, ease: "out", trans: "expo" },
        ],
      },
    });
    const el = at(root, "SelectionReticle");
    expect(applied.has("SelectionReticle")).toBe(true);
    expect(el.style.transition).toContain("opacity 200ms ease-out");
    // expo → the catalog's cubic-bezier (shared transitionCss easing).
    expect(el.style.transition).toContain("transform 500ms cubic-bezier(0.19, 1, 0.22, 1)");
  });

  it("supports the flat `{ properties, durationMs }` form, defaulting to opacity", () => {
    const root = nodeRoot("a", "b");
    applyTransitionBindings(root, {
      a: { durationMs: 150 },
      b: { properties: ["opacity", "transform"], durationMs: 300, ease: "out" },
    });
    expect(at(root, "a").style.transition).toBe("opacity 150ms ease-in-out");
    expect(at(root, "b").style.transition).toBe("opacity 300ms ease-out, transform 300ms ease-out");
  });

  it("clears the transition when the path disappears", () => {
    const root = nodeRoot("SelectionReticle");
    const first = applyTransitionBindings(root, { SelectionReticle: { durationMs: 200 } });
    const second = applyTransitionBindings(root, {});
    clearTransitionBindings(root, first, second);
    expect(at(root, "SelectionReticle").style.transition).toBe("");
  });
});

describe("default value-change transitions (isDefault, first-paint suppressed)", () => {
  const DEFAULT = {
    path: "Health/Bar",
    properties: ["opacity", "transform", "filter"],
    durationMs: 200,
    ease: "out",
    isDefault: true,
  };
  const DEFAULT_CSS =
    "opacity 200ms ease-out, transform 200ms ease-out, filter 200ms ease-out";

  it("does NOT install the transition on the first observation of a path", () => {
    const root = nodeRoot("Health/Bar");
    const applied = applyTransitionBindings(root, [DEFAULT]);
    // The path is reported as MANAGED (so clear does not wipe it) but no inline
    // transition is installed — the initial paint of the node snaps.
    expect(applied.has("Health/Bar")).toBe(true);
    expect(at(root, "Health/Bar").style.transition).toBe("");
  });

  it("installs the default from the SECOND observation onward", () => {
    const root = nodeRoot("Health/Bar");
    applyTransitionBindings(root, [DEFAULT]); // first paint: suppressed
    const applied = applyTransitionBindings(root, [DEFAULT]); // second paint: live
    expect(applied.has("Health/Bar")).toBe(true);
    expect(at(root, "Health/Bar").style.transition).toBe(DEFAULT_CSS);
  });

  it("resets to first-paint suppression after the path disappears (fresh remount)", () => {
    const root = nodeRoot("Health/Bar");
    const first = applyTransitionBindings(root, [DEFAULT]); // first paint
    const second = applyTransitionBindings(root, [DEFAULT]); // installs default
    expect(at(root, "Health/Bar").style.transition).toBe(DEFAULT_CSS);
    // Path disappears → cleared (inline transition wiped + first-paint memory forgotten).
    clearTransitionBindings(root, second, applyTransitionBindings(root, []));
    expect(at(root, "Health/Bar").style.transition).toBe("");
    // A re-appearance is treated as a fresh first paint again — suppressed on first observation.
    applyTransitionBindings(root, [DEFAULT]);
    expect(at(root, "Health/Bar").style.transition).toBe("");
    void first;
  });
});

describe("card-move animations (hand exits)", () => {
  it("cardFly translates by the to−from delta, shrinks toward the pile, one-shot forwards", () => {
    const root = nodeRoot("mv:c2:1000");
    const applied = applyAnimationBindings(root, [
      { path: "mv:c2:1000", kind: "cardFly", durationMs: 800, fromX: 700, fromY: 900, toX: 1500, toY: 980, scaleTo: 0.6, loop: false },
    ]);
    const el = at(root, "mv:c2:1000");
    expect(applied.has("mv:c2:1000")).toBe(true);
    expect(el.style.getPropertyValue("--spirectl-fly-dx")).toBe("800px"); // 1500 - 700
    expect(el.style.getPropertyValue("--spirectl-fly-dy")).toBe("80px"); // 980 - 900
    expect(el.style.getPropertyValue("--spirectl-fly-scale-to")).toBe("0.6");
    expect(el.style.animation).toContain("spirectl-card-fly");
    expect(el.style.animation).toContain("800ms");
    expect(el.style.animation).toContain("forwards");
    expect(el.style.animation.split(/\s+/)).toContain("1"); // one-shot (loop:false → single iteration)
  });

  it("cardFly applies a positive delayMs as an animation-delay (staggered shuffle/deal cascade)", () => {
    const root = nodeRoot("mv:cShuf:1000");
    applyAnimationBindings(root, [
      { path: "mv:cShuf:1000", kind: "cardFly", durationMs: 800, fromX: 1747, fromY: 856, toX: -65, toY: 856, loop: false, delayMs: 120 },
    ]);
    expect(at(root, "mv:cShuf:1000").style.animation).toContain("120ms");
  });

  it("cardFly is idempotent across re-applies (does NOT restart mid-flight)", () => {
    const root = nodeRoot("mv:c2:1000");
    const binding = { path: "mv:c2:1000", kind: "cardFly", durationMs: 800, fromX: 0, fromY: 0, toX: 100, toY: 0 } as const;
    applyAnimationBindings(root, [binding]);
    const first = at(root, "mv:c2:1000").style.animation;
    applyAnimationBindings(root, [binding]);
    expect(at(root, "mv:c2:1000").style.animation).toBe(first);
  });

  it("cardSmoke dissolves in place (scale-up + fade), no fly vars", () => {
    const root = nodeRoot("mv:c1:500");
    applyAnimationBindings(root, [{ path: "mv:c1:500", kind: "cardSmoke", durationMs: 2000, scaleTo: 1.25 }]);
    const el = at(root, "mv:c1:500");
    expect(el.style.animation).toContain("spirectl-card-smoke");
    expect(el.style.animation).toContain("2000ms");
    expect(el.style.getPropertyValue("--spirectl-smoke-scale-to")).toBe("1.25");
    expect(el.style.getPropertyValue("--spirectl-fly-dx")).toBe("");
  });

  it("clears card-move vars when the path disappears", () => {
    const root = nodeRoot("mv:c2:1000");
    const first = applyAnimationBindings(root, [
      { path: "mv:c2:1000", kind: "cardFly", durationMs: 800, fromX: 0, fromY: 0, toX: 100, toY: 0 },
    ]);
    const second = applyAnimationBindings(root, []);
    clearAnimationBindings(root, first, second);
    const el = at(root, "mv:c2:1000");
    expect(el.style.animation).toBe("");
    expect(el.style.getPropertyValue("--spirectl-fly-dx")).toBe("");
    expect(el.style.getPropertyValue("--spirectl-fly-scale-to")).toBe("");
  });
});

describe("enter one-shot mount reveal (Vehicle 2)", () => {
  it("plays once with a translate offset + Godot easing, releasing (no forwards)", () => {
    const root = nodeRoot("EndTurn");
    const applied = applyAnimationBindings(root, [
      { path: "EndTurn", kind: "enter", offsetX: 400, durationMs: 500, ease: "out", trans: "back" },
    ]);
    const el = at(root, "EndTurn");
    expect(applied.has("EndTurn")).toBe(true);
    expect(el.style.animation).toContain("spirectl-enter");
    expect(el.style.animation).toContain("500ms");
    // back/out → the shared cubic-bezier.
    expect(el.style.animation).toContain("cubic-bezier(0.34, 1.56, 0.64, 1)");
    // A transient reveal releases to the node's resting position (no held final frame).
    expect(el.style.animation).not.toContain("forwards");
    expect(el.style.animation.split(/\s+/)).toContain("1"); // one iteration
    expect(el.style.getPropertyValue("--spirectl-enter-x")).toBe("400px");
    expect(el.style.getPropertyValue("--spirectl-enter-y")).toBe("0px");
    expect(el.style.getPropertyValue("--spirectl-enter-opacity-from")).toBe("1");
  });

  it("supports an opacity/modulate fade variant (opacityFrom → no slide)", () => {
    const root = nodeRoot("Rest");
    applyAnimationBindings(root, [
      { path: "Rest", kind: "enter", opacityFrom: 0, durationMs: 500, ease: "out", trans: "cubic" },
    ]);
    const el = at(root, "Rest");
    expect(el.style.getPropertyValue("--spirectl-enter-opacity-from")).toBe("0");
    expect(el.style.getPropertyValue("--spirectl-enter-x")).toBe("0px");
    // cubic has no exact CSS bezier → falls back to the plain ease-out curve.
    expect(el.style.animation).toContain("ease-out");
    expect(el.style.animation).toContain("spirectl-enter");
  });

  it("plays ONCE — idempotent across re-applies (does NOT restart on remount re-walk)", () => {
    const root = nodeRoot("EndTurn");
    const binding = { path: "EndTurn", kind: "enter", offsetX: 400, durationMs: 500 } as const;
    applyAnimationBindings(root, [binding]);
    const first = at(root, "EndTurn").style.animation;
    expect(first).toContain("spirectl-enter");
    applyAnimationBindings(root, [binding]);
    expect(at(root, "EndTurn").style.animation).toBe(first);
  });

  it("clears the enter style + vars when the path disappears", () => {
    const root = nodeRoot("EndTurn");
    const first = applyAnimationBindings(root, [{ path: "EndTurn", kind: "enter", offsetX: 400 }]);
    const second = applyAnimationBindings(root, []);
    clearAnimationBindings(root, first, second);
    const el = at(root, "EndTurn");
    expect(el.style.animation).toBe("");
    expect(el.style.getPropertyValue("--spirectl-enter-x")).toBe("");
    expect(el.style.getPropertyValue("--spirectl-enter-opacity-from")).toBe("");
  });
});

describe("stateChange transition playback (Vehicle 1)", () => {
  // Shape B (confirm/back/proceed): boolean stateKey, enter/exit legs.
  const boolBinding = (active: boolean) => [
    {
      path: "Confirm",
      key: "confirm-slide",
      value: active,
      active,
      enter: { fromX: 180, fromY: 0, toX: 0, toY: 0, durationMs: 350, ease: "out", trans: "back" },
      exit: { fromX: 0, fromY: 0, toX: 180, toY: 0, durationMs: 350, ease: "out", trans: "expo" },
    },
  ];

  it("first observation applies the resting pose WITHOUT animating", () => {
    const root = nodeRoot("Confirm");
    const applied = applyStateTransitionBindings(root, boolBinding(false));
    expect(applied.has("Confirm")).toBe(true);
    // No spurious slide on the initial mount / page load.
    expect(at(root, "Confirm").style.animation).toBe("");
  });

  it("plays the enter slide on false→true and the exit slide on true→false", () => {
    const root = nodeRoot("Confirm");
    applyStateTransitionBindings(root, boolBinding(false)); // seed rest, no anim
    const el = at(root, "Confirm");
    expect(el.style.animation).toBe("");

    // OnEnable: slide in from the +180 offset to rest, back/out timing.
    applyStateTransitionBindings(root, boolBinding(true));
    expect(el.style.animation).toContain("spirectl-state-transition");
    expect(el.style.animation).toContain("350ms");
    expect(el.style.animation).toContain("cubic-bezier(0.34, 1.56, 0.64, 1)"); // back/out
    expect(el.style.getPropertyValue("--spirectl-st-from-x")).toBe("180px");
    expect(el.style.getPropertyValue("--spirectl-st-to-x")).toBe("0px");

    // OnDisable: slide out to +180 from rest, expo/out timing.
    applyStateTransitionBindings(root, boolBinding(false));
    expect(el.style.getPropertyValue("--spirectl-st-from-x")).toBe("0px");
    expect(el.style.getPropertyValue("--spirectl-st-to-x")).toBe("180px");
    expect(el.style.animation).toContain("cubic-bezier(0.19, 1, 0.22, 1)"); // expo/out
  });

  it("reverses the enter leg on deactivation when no explicit exit is authored", () => {
    const root = nodeRoot("Info");
    const enterOnly = (value: unknown) => [
      {
        path: "Info",
        key: "info-slide",
        value,
        active: !!value,
        enter: { fromX: -300, fromY: 0, toX: 0, toY: 0, durationMs: 500, ease: "out", trans: "expo" },
      },
    ];
    applyStateTransitionBindings(root, enterOnly(null)); // first obs
    applyStateTransitionBindings(root, enterOnly("char-a")); // activate → enter
    const el = at(root, "Info");
    expect(el.style.getPropertyValue("--spirectl-st-from-x")).toBe("-300px");
    expect(el.style.getPropertyValue("--spirectl-st-to-x")).toBe("0px");
    // Deactivate (→null): the enter leg is reversed (to→from).
    applyStateTransitionBindings(root, enterOnly(null));
    expect(el.style.getPropertyValue("--spirectl-st-from-x")).toBe("0px");
    expect(el.style.getPropertyValue("--spirectl-st-to-x")).toBe("-300px");
  });

  it("re-plays the enter slide on a truthy value CHANGE (string-key re-slide), not on a repeat", () => {
    const root = nodeRoot("Info");
    const sel = (value: string | null) => [
      {
        path: "Info",
        key: "info-slide",
        value,
        active: !!value,
        enter: { fromX: -300, fromY: 0, toX: 0, toY: 0, durationMs: 500, ease: "out", trans: "expo" },
      },
    ];
    applyStateTransitionBindings(root, sel(null)); // first obs
    applyStateTransitionBindings(root, sel("char-a")); // activate → enter
    const el = at(root, "Info");
    // A DIFFERENT truthy selection re-slides (observed by re-setting the animation).
    el.style.animation = "";
    applyStateTransitionBindings(root, sel("char-b"));
    expect(el.style.animation).toContain("spirectl-state-transition");
    // The SAME selection again does NOT re-slide.
    el.style.animation = "";
    applyStateTransitionBindings(root, sel("char-b"));
    expect(el.style.animation).toBe("");
  });

  it("clears the animation + forgets tracked state when the path disappears (fresh first-obs on remount)", () => {
    const root = nodeRoot("Confirm");
    const first = applyStateTransitionBindings(root, boolBinding(false));
    applyStateTransitionBindings(root, boolBinding(true)); // enter plays
    const el = at(root, "Confirm");
    expect(el.style.animation).toContain("spirectl-state-transition");
    // Path drops out of the walk.
    const second = applyStateTransitionBindings(root, []);
    clearStateTransitionBindings(root, first, second);
    expect(el.style.animation).toBe("");
    expect(el.style.getPropertyValue("--spirectl-st-from-x")).toBe("");
    // Re-appearing (active) is treated as a FRESH first observation → no spurious slide.
    applyStateTransitionBindings(root, boolBinding(true));
    expect(at(root, "Confirm").style.animation).toBe("");
  });
});

describe("animation-hint pre-arm (tween timing) pass", () => {
  // Simulate the default/explicit transition pass having run first: a full opacity/transform/filter
  // default already sits inline, and the hint pass MERGES its channel over it.
  const DEFAULT_TRANSITION = "opacity 200ms ease-out, transform 200ms ease-out, filter 200ms ease-out";

  it("arms the hinted channel with the hint's timing, keeping the other default channels", () => {
    const root = nodeRoot("Combat/HealthBar/Fill");
    const el = at(root, "Combat/HealthBar/Fill");
    el.style.transition = DEFAULT_TRANSITION;
    // modulate:a fade → opacity channel; Godot Expo/Out → its cubic-bezier.
    const applied = applyAnimationHintBindings(root, [
      { scene: "combat/health_bar", nodePath: "HealthBar/Fill", channel: "opacity", durationMs: 320, trans: "expo", ease: "out" },
    ]);
    expect(applied.has("Combat/HealthBar/Fill")).toBe(true);
    const transition = el.style.transition;
    // Hinted channel takes the hint timing (Expo/Out bezier), other channels keep the 200ms default.
    expect(transition).toContain("opacity 320ms cubic-bezier(0.19, 1, 0.22, 1)");
    expect(transition).toContain("transform 200ms ease-out");
    expect(transition).toContain("filter 200ms ease-out");
    // Exactly ONE opacity piece (the default opacity piece was replaced, not duplicated).
    expect(transition.split(",").filter((p) => p.trim().startsWith("opacity")).length).toBe(1);
  });

  it("suffix-matches ALL mounted instances of the scene-relative node path", () => {
    const root = nodeRoot("PartyA/HealthBar/Fill", "PartyB/HealthBar/Fill", "Other/Bar");
    for (const p of ["PartyA/HealthBar/Fill", "PartyB/HealthBar/Fill", "Other/Bar"]) {
      at(root, p).style.transition = DEFAULT_TRANSITION;
    }
    const applied = applyAnimationHintBindings(root, [
      { nodePath: "HealthBar/Fill", channel: "transform", durationMs: 150, trans: undefined, ease: undefined },
    ]);
    expect(applied.has("PartyA/HealthBar/Fill")).toBe(true);
    expect(applied.has("PartyB/HealthBar/Fill")).toBe(true);
    expect(applied.has("Other/Bar")).toBe(false);
    expect(at(root, "PartyA/HealthBar/Fill").style.transition).toContain("transform 150ms");
    expect(at(root, "PartyB/HealthBar/Fill").style.transition).toContain("transform 150ms");
    expect(at(root, "Other/Bar").style.transition).toBe(DEFAULT_TRANSITION);
  });

  it("restores the underlying default transition when the hint expires (dropped from the list)", () => {
    const root = nodeRoot("Combat/HealthBar/Fill");
    const el = at(root, "Combat/HealthBar/Fill");
    el.style.transition = DEFAULT_TRANSITION;
    const applied = applyAnimationHintBindings(root, [
      { nodePath: "HealthBar/Fill", channel: "opacity", durationMs: 320, trans: "expo", ease: "out" },
    ]);
    expect(el.style.transition).toContain("320ms");
    // Next frame: the hint's TTL lapsed (no hints), so clear restores the pre-hint default.
    const next = applyAnimationHintBindings(root, []);
    clearAnimationHintBindings(root, applied, next);
    expect(el.style.transition).toBe(DEFAULT_TRANSITION);
  });

  it("arms onto a bare element with no prior transition, then clears back to empty on expiry", () => {
    const root = nodeRoot("Solo/Node");
    const el = at(root, "Solo/Node");
    const applied = applyAnimationHintBindings(root, [
      { nodePath: "Solo/Node", channel: "opacity", durationMs: 200, trans: undefined, ease: undefined },
    ]);
    expect(el.style.transition).toContain("opacity 200ms");
    const next = applyAnimationHintBindings(root, []);
    clearAnimationHintBindings(root, applied, next);
    expect(el.style.transition).toBe("");
  });
});
