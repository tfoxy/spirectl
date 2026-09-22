// @vitest-environment happy-dom
//
// The three animated STS2 rich-text tags, end to end through the parser that actually consumes them:
// the table declares them as per-character effects, godot-scene-web splits and classes the run, and the
// injected sheet carries a rule for each class — plus the Settings -> Text Effects gate over exactly the
// two tags the game itself gates.
import { godotBbcodeTagKind, richTextLayeredHtml } from "@godot-scene-web/html";
import { describe, expect, it } from "vitest";

import { DEFAULT_BBCODE_TAGS } from "../src/render/bbcodeTags";
import {
  ensureRichTextEffectStyles,
  RICH_FX_JITTER,
  RICH_FX_SINE,
  RICH_FX_THINKY_DOTS,
  RICH_TEXT_EFFECTS_ATTRIBUTE,
} from "../src/render/richTextEffects";

function sheet(): string {
  document.getElementById("spirectl-presentation-rich-text-effects")?.remove();
  ensureRichTextEffectStyles(document);
  return (
    document.getElementById("spirectl-presentation-rich-text-effects")
      ?.textContent ?? ""
  );
}

describe("STS2 rich-text effects", () => {
  it("declares the three looping tags as per-character effects", () => {
    for (const name of ["sine", "jitter", "thinky_dots"]) {
      expect(
        godotBbcodeTagKind(name, DEFAULT_BBCODE_TAGS),
        `[${name}] is an effect`,
      ).toBe("effect");
    }
  });

  it("leaves the three one-shot reveals inert, but still consumed", () => {
    // Inert is not the same as unknown: an unrecognised tag renders LITERALLY, which would put
    // "[fade_in]" on screen in the middle of a sentence.
    for (const name of ["ancient_banner", "fade_in", "fly_in"]) {
      expect(godotBbcodeTagKind(name, DEFAULT_BBCODE_TAGS)).toBe("style");
    }
    const html = richTextLayeredHtml("a[fade_in]b[/fade_in]c", {
      customTags: DEFAULT_BBCODE_TAGS,
    });
    expect(html).not.toContain("fade_in]");
    expect(html).not.toContain("godot-rich-char");
  });

  it("splits a [sine] run into per-character spans carrying its class and index", () => {
    const html = richTextLayeredHtml("[sine]ab[/sine]", {
      customTags: DEFAULT_BBCODE_TAGS,
    });
    expect(html).toContain(RICH_FX_SINE);
    expect(html).toContain('<span class="godot-rich-char" style="--i: 0">a');
    expect(html).toContain('<span class="godot-rich-char" style="--i: 1">b');
  });

  it("gives every declared effect class a rule in the sheet", () => {
    const css = sheet();
    for (const className of [
      RICH_FX_SINE,
      RICH_FX_JITTER,
      RICH_FX_THINKY_DOTS,
    ]) {
      expect(css, `${className} is animated`).toContain(
        `.${className} .godot-rich-char {\n  animation:`,
      );
    }
  });

  it("gates exactly the two effects the game gates", () => {
    const css = sheet();
    const gate = css.slice(css.indexOf(`[${RICH_TEXT_EFFECTS_ATTRIBUTE}="off"]`));
    expect(gate).toContain(RICH_FX_SINE);
    expect(gate).toContain(RICH_FX_THINKY_DOTS);
    // The tremble keeps going with the setting off, because it does in the game.
    expect(gate).not.toContain(RICH_FX_JITTER);
    // …and it stands the animation DOWN rather than pausing it mid-excursion.
    expect(gate).toContain("animation: none");
  });

  it("injects once, however many labels ask", () => {
    document.getElementById("spirectl-presentation-rich-text-effects")?.remove();
    ensureRichTextEffectStyles(document);
    ensureRichTextEffectStyles(document);
    ensureRichTextEffectStyles(document.body);
    expect(
      document.querySelectorAll("#spirectl-presentation-rich-text-effects"),
    ).toHaveLength(1);
  });
});
