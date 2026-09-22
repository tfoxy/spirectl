// STS2's ANIMATED RICH-TEXT EFFECTS, as CSS.
//
// The game decorates a lot of its prose with BBCode effect tags. Three of them loop forever off nothing
// but elapsed time, which is exactly what a CSS animation is, so a host that lays STS2 rich text out in
// the DOM can reproduce them with no per-frame work of its own:
//
//   [sine]        a slow vertical wave — "wavy text" in the game's own Settings copy
//   [jitter]      a fast two-axis tremble — "shaky text"
//   [thinky_dots] a single hop that travels along the run and then waits out a long pause
//
// `DEFAULT_BBCODE_TAGS` declares them as per-character effects carrying the class names below, so
// godot-scene-web splits their content into `<span class="godot-rich-char" style="--i: N">` elements
// and the rules here key on that `--i` for the per-character phase. Everything is expressed relative to
// the run's own font size (`em`, and time), so a host that scales text up for readability — the couch
// co-op mirror scales it a long way up for phones — keeps the motion in proportion instead of shrinking
// it into invisibility.
//
// THE SETTING GATE, AND WHY IT COVERS ONLY TWO OF THE THREE. The game has a Settings -> Text Effects
// toggle, and it is not self-enforcing here: the game leaves the markup in the string and skips the
// per-character transform instead, so a remote host receives identical BBCode either way and has to be
// told. It is also not uniform — with the setting off the game stops the wave and the hop and nothing
// else; the tremble, and Godot's own built-in `[rainbow]`/`[shake]`, keep going. This sheet reproduces
// that exactly. A host stamps `data-spirectl-text-effects="off"` on any ancestor of its labels (one
// attribute write for a whole screen) and the two gated rules stand down.
//
// THE NUMBERS ARE THE GAME'S, CONVERTED ONCE. Each block says what a reader sees — period, peak
// excursion, which way the stagger runs. They are quoted against the ~21px body text these effects are
// authored for, which is what turns the game's pixel amplitudes into the `em` values below.
//
// ONE KNOWN GAP, MEASURED RATHER THAN ASSUMED. The game nests these: `[sine][rainbow …]…[/rainbow][/sine]`
// appears in five event and epoch strings, and there the text both waves and cycles colour. Here it only
// cycles: godot-scene-web emits a span per open effect, but both rules land on the same one-element-per-glyph
// span, so `animation` resolves by specificity and the built-in rainbow rule wins. Composing the two would
// need an element per effect per glyph. `scripts/probe-text-effects.mjs` in the couch-coop checkout reports
// this case explicitly rather than leaving it to be discovered.

const STYLE_ID = "spirectl-presentation-rich-text-effects";

/** Class the `[sine]` tag's run carries. Gated by `data-spirectl-text-effects="off"`. */
export const RICH_FX_SINE = "spirectl-rich-fx-sine";
/** Class the `[jitter]` tag's run carries. NOT gated — the game does not gate it either. */
export const RICH_FX_JITTER = "spirectl-rich-fx-jitter";
/** Class the `[thinky_dots]` tag's run carries. Gated by `data-spirectl-text-effects="off"`. */
export const RICH_FX_THINKY_DOTS = "spirectl-rich-fx-thinky-dots";

/** The attribute a host writes to turn the gated effects off, and the value that does it. */
export const RICH_TEXT_EFFECTS_ATTRIBUTE = "data-spirectl-text-effects";

const SINE = "spirectl-rich-sine";
const JITTER = "spirectl-rich-jitter";
const THINKY_DOTS = "spirectl-rich-thinky-dots";

// Only the two the game itself gates. Written as one selector list so the gate cannot drift from the
// rules above it.
const GATED = [
  `[${RICH_TEXT_EFFECTS_ATTRIBUTE}="off"] .${RICH_FX_SINE} .godot-rich-char`,
  `[${RICH_TEXT_EFFECTS_ATTRIBUTE}="off"] .${RICH_FX_THINKY_DOTS} .godot-rich-char`,
].join(",\n");

const CSS = `
/* WAVE — a 1.333s vertical cycle, 0.038em peak, moving DOWN first. Each character leads its left-hand
   neighbour by 67ms, which is what makes the run read as a travelling wave rather than a bounce. */
.${RICH_FX_SINE} .godot-rich-char {
  animation: ${SINE} 1.333s linear infinite;
  animation-delay: calc(var(--i, 0) * -0.0667s);
}
@keyframes ${SINE} {
  0%, 100% { transform: translateY(0); }
  25% { transform: translateY(0.038em); }
  75% { transform: translateY(-0.038em); }
}

/* TREMBLE — a fast two-axis shiver. The game gives every character its own noise seed, so neighbours are
   uncorrelated; CSS has one curve, so the per-character delay steps by an amount deliberately NOT a
   simple fraction of the loop (0.137 / 0.45 is irrational enough to spread a run of characters right
   around the cycle). The stops are a sampled 2D walk rather than a step, because the game's driver is
   Perlin noise.

   THE PEAK IS WELL UNDER THE NOMINAL AMPLITUDE, AND THAT IS THE POINT. The game multiplies its noise by
   an amplitude the noise itself almost never reaches — fractal Perlin spends nearly all its time near
   the middle of its range — so a keyframe walk that visited the full amplitude every 56ms trembled
   several times harder than the game does. Measured by eye against the same string at the same size, it
   shredded the word (letters visibly pulled apart, kerning gone); this envelope reads as the shiver the
   game actually draws. Peak here is ~0.05em against a nominal ~0.14em. */
.${RICH_FX_JITTER} .godot-rich-char {
  animation: ${JITTER} 0.45s linear infinite;
  animation-delay: calc(var(--i, 0) * -0.137s);
}
@keyframes ${JITTER} {
  0%, 100% { transform: translate(0.035em, -0.018em); }
  12.5% { transform: translate(-0.022em, -0.045em); }
  25% { transform: translate(-0.050em, 0.012em); }
  37.5% { transform: translate(-0.010em, 0.043em); }
  50% { transform: translate(0.031em, 0.039em); }
  62.5% { transform: translate(0.048em, -0.014em); }
  75% { transform: translate(0.007em, -0.042em); }
  87.5% { transform: translate(-0.038em, -0.026em); }
}

/* HOP — one 0.4s half-sine 0.071em upward, then a 4s wait: a 4.4s cycle that is still for 91% of its
   length. Each character LAGS its left-hand neighbour by 100ms, so the hop walks along the run the way
   a loading ellipsis does. */
.${RICH_FX_THINKY_DOTS} .godot-rich-char {
  animation: ${THINKY_DOTS} 4.4s linear infinite;
  animation-delay: calc(var(--i, 0) * 0.1s);
}
@keyframes ${THINKY_DOTS} {
  0% { transform: translateY(0); }
  4.5455% { transform: translateY(-0.071em); }
  9.0909%, 100% { transform: translateY(0); }
}

/* The game's Text Effects setting, off. "animation: none" rather than a pause, because a paused
   animation freezes wherever it was — the characters would stay displaced. */
${GATED} {
  animation: none;
}
`;

/**
 * Inject the rich-text effect stylesheet into `root`'s document, once.
 *
 * Idempotent and safe to call per label; the id is the guard. A host calls this wherever it builds a
 * rich-text element, the same way it calls `ensureAnimationStyles`.
 */
export function ensureRichTextEffectStyles(root: ParentNode): void {
  const doc = root.nodeType === 9 ? (root as Document) : root.ownerDocument;
  if (!doc || doc.getElementById(STYLE_ID)) return;
  const style = doc.createElement("style");
  style.id = STYLE_ID;
  style.textContent = CSS;
  (doc.head ?? doc.documentElement ?? doc).appendChild(style);
}
