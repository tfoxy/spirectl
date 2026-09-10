using System;

namespace Spirectl.Sts2.Live;

// Producer decision rules for a rich-text label's PER-ROLE theme fonts (`bold_font` / `italics_font` /
// `bold_italics_font`) — PURE and Godot-free (like Sts2ReparentEmit / Sts2ViewportContentPrune) so the emit
// decisions are unit-testable without the Godot-coupled watcher.
//
// Background (`[b]` renders un-bold in the browser mirror). Godot does NOT synthesise bold inside a
// `RichTextLabel`: it renders a `[b]` span by swapping the label's font to its `bold_font` THEME ITEM — a different
// font FILE (STS2: `res://fonts/kreon_bold.ttf`, reached through the `kreon_bold_glyph_space_one.tres` →
// `kreon_bold_shared.tres` FontVariation chain). The wire streamed only ONE font per node (the label's
// `normal_font`), so the client's `<strong class="godot-rich-bold">` inherited kreon_regular — a single 400 face —
// and `font-synthesis: none` (deliberate) correctly refused to fake it. Bold looked right on OTHER nodes only
// because those stream kreon_bold as their OWN normal font.
//
// Fix: probe the three role fonts via `Control.GetThemeFont(name)` (the EFFECTIVE font: per-node override → theme
// chain → default theme), resolve each to its underlying font BINARY through the same `ResolveFontBinary` walk the
// normal font uses, and stream them alongside it. The consuming contract already exists in godot-scene-web
// (`--godot-rich-bold-font-family` and friends); the missing datum was producer-side.
//
// SKIPPED BY DESIGN: `mono_font`. godot-scene-web exposes no `--godot-rich-mono-font-family` CSS variable, so a
// fourth role font would be dead weight on every rich-text node's static block. Add it here (and the three sibling
// size/spacing probes) if and when gsw grows the var.
//
// DEFAULT ON; SPIRECTL_SCENE_WATCH_RICH_ROLE_FONTS=0 seeds it OFF (restoring the one-font-per-node wire
// byte-identically). See Sts2SceneWatchRuntimeSettings.StreamRichRoleFonts.
internal static class Sts2RichRoleFontEmit
{
    // True when a `res://` path names a font BINARY the /res/ route can serve as a real `@font-face` source.
    //
    // Shared with the normal-font resolution (`Sts2RuntimeSceneWatcher.ResolveFontBinary`), which uses it as its
    // "already a binary, stop walking" short-circuit; the role-font probes use it as an EMIT GATE. That second use
    // is what makes it matter: `GetThemeFont` never fails — a label with no `bold_font` override and no theme entry
    // still gets Godot's DEFAULT-THEME font back, a built-in resource with an empty (or non-binary) resource path.
    // Gating on the suffix turns that into "no role font streamed" instead of a bogus family the client cannot
    // fetch.
    internal static bool IsFontBinaryPath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && (path!.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".woff", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase));

    // True when a probed role font should actually be STREAMED for this node.
    //
    // Two suppressions, both of which keep the wire at its pre-fix size for the overwhelming majority of nodes:
    //   * not a font binary   — unresolvable / default-theme font (see IsFontBinaryPath). Emitting it would hand the
    //                           client a family it cannot @font-face.
    //   * same as the normal  — the label's `[b]` spans would render in the font they already inherit, so the field
    //     font path            carries no information. This is the common case for the many STS2 labels whose OWN
    //                           font is already kreon_bold.
    internal static bool ShouldEmitRoleFont(string? resolvedRoleFontPath, string? normalFontPath)
        => IsFontBinaryPath(resolvedRoleFontPath)
           && !string.Equals(resolvedRoleFontPath, normalFontPath, StringComparison.OrdinalIgnoreCase);

    // The per-role font SIZE to stream, or null to omit it. Godot resolves `[b]` spans at the label's
    // `bold_font_size` theme item, which STS2 authors independently of `normal_font_size` on some scenes
    // (card.tscn: 21, timeline_screen.tscn: 24, unlock_relics_screen.tscn: 28). Omitted when it EQUALS the node's
    // normal size (nothing to say) or when either size is non-positive (an unset/absent theme item), so a client
    // that sees null just keeps rendering the span at the node's own size.
    internal static double? RoleFontSizePxOrNull(int roleFontSizePx, int normalFontSizePx)
        => roleFontSizePx > 0 && normalFontSizePx > 0 && roleFontSizePx != normalFontSizePx
            ? roleFontSizePx
            : null;

    // The per-role GLYPH SPACING (extra px inserted after every glyph) to stream, or null to omit it. STS2 reaches
    // its role fonts through `FontVariation` resources that carry `spacing_glyph` — `kreon_bold_glyph_space_one.tres`
    // sets 1, `..._two` sets 2, `..._three` sets 3 — so a `[b]` span is tracked WIDER than the same text in the
    // label's normal font. Omitted when zero (the default, i.e. nothing to say) so the wire is unchanged for every
    // node whose role font is a plain FontFile or an unspaced variation. Negative spacing is legal in Godot
    // (tightening) and is streamed.
    internal static double? RoleFontSpacingPxOrNull(int spacingGlyphPx)
        => spacingGlyphPx != 0 ? spacingGlyphPx : null;
}
