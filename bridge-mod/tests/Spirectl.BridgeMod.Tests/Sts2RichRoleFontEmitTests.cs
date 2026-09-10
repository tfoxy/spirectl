using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// WS-A `[b]` renders un-bold in the browser mirror — the pure per-role rich-text font emit gates
// (Sts2RichRoleFontEmit). Godot renders a `[b]` span by swapping the RichTextLabel to its `bold_font` THEME ITEM (a
// different font FILE: res://fonts/kreon_bold.ttf), never by synthesising bold; the wire carried one font per node,
// so the mirror's <strong> inherited the label's single-face normal font. These are the decisions that keep the
// added static fields free for the ~all nodes that have nothing to say.
public sealed class Sts2RichRoleFontEmitTests
{
    private const string KreonRegular = "res://fonts/kreon_regular.ttf";
    private const string KreonBold = "res://fonts/kreon_bold.ttf";

    [Theory]
    [InlineData("res://fonts/kreon_bold.ttf", true)]
    [InlineData("res://fonts/spectral_bold.otf", true)]
    [InlineData("res://fonts/kreon_bold.WOFF", true)]   // suffix test is case-insensitive
    [InlineData("res://fonts/kreon_bold.woff2", true)]
    [InlineData("res://themes/kreon_bold_glyph_space_one.tres", false)] // a FontVariation, served as JSON
    [InlineData("res://themes/kreon_bold_shared.tres", false)]
    [InlineData("", false)]                              // Godot's built-in default-theme font
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsFontBinaryPath_AcceptsOnlyServeableFontBinaries(string? path, bool expected)
    {
        Assert.Equal(expected, Sts2RichRoleFontEmit.IsFontBinaryPath(path));
    }

    [Fact]
    public void RoleFontDifferentFromNormal_IsEmitted()
    {
        // The fix's whole point: an event/description label whose normal font is kreon_regular but whose theme names
        // kreon_bold for `bold_font`. Streaming it is what lets the client @font-face a real 700 face.
        Assert.True(Sts2RichRoleFontEmit.ShouldEmitRoleFont(KreonBold, KreonRegular));
    }

    [Fact]
    public void RoleFontSameAsNormal_IsSuppressed()
    {
        // The common case for the many STS2 labels whose OWN font is already kreon_bold: the `[b]` span would render
        // in the face it already inherits, so the field carries no information. Case-insensitive, like the
        // suffix gate — Godot res:// paths are compared the same way everywhere in this producer.
        Assert.False(Sts2RichRoleFontEmit.ShouldEmitRoleFont(KreonBold, KreonBold));
        Assert.False(Sts2RichRoleFontEmit.ShouldEmitRoleFont(KreonBold, "res://fonts/KREON_BOLD.TTF"));
    }

    [Theory]
    [InlineData("res://themes/kreon_bold_glyph_space_one.tres")] // unresolved FontVariation → /res/ serves JSON
    [InlineData("")]                                             // built-in default-theme font
    [InlineData(null)]                                           // no theme font at all
    public void UnresolvableRoleFont_IsSuppressed(string? resolvedPath)
    {
        // GetThemeFont NEVER fails — a label with no `bold_font` override and no theme entry still gets Godot's
        // default-theme font back. The binary-suffix gate is what turns that fallback into "nothing streamed"
        // instead of a family the client cannot fetch.
        Assert.False(Sts2RichRoleFontEmit.ShouldEmitRoleFont(resolvedPath, KreonRegular));
    }

    [Fact]
    public void RoleFontEmit_IsIndependentOfWhetherTheNormalFontResolved()
    {
        // A node whose own font failed to resolve (null path) still gets its role font: the de-dup is only ever an
        // equality test, never a precondition.
        Assert.True(Sts2RichRoleFontEmit.ShouldEmitRoleFont(KreonBold, normalFontPath: null));
    }

    [Theory]
    [InlineData(21, 18, 21.0)]  // card.tscn authors bold_font_size=21 over a smaller normal size
    [InlineData(24, 20, 24.0)]  // timeline_screen.tscn
    [InlineData(28, 24, 28.0)]  // unlock_relics_screen.tscn
    [InlineData(18, 18, null)]  // equal to the node's normal size → nothing to say
    [InlineData(0, 18, null)]   // role item unset
    [InlineData(21, 0, null)]   // normal size unknown → cannot say it differs
    [InlineData(0, 0, null)]
    [InlineData(-4, 18, null)]  // non-positive is not a real size
    public void RoleFontSizePx_OmittedWhenEqualOrUnset(int roleSize, int normalSize, double? expected)
    {
        Assert.Equal(expected, Sts2RichRoleFontEmit.RoleFontSizePxOrNull(roleSize, normalSize));
    }

    [Theory]
    [InlineData(1, 1.0)]   // themes/kreon_bold_glyph_space_one.tres
    [InlineData(2, 2.0)]   // ..._two
    [InlineData(3, 3.0)]   // ..._three
    [InlineData(-1, -1.0)] // Godot allows tightening; stream it
    [InlineData(0, null)]  // the default — a plain FontFile or an unspaced variation
    public void RoleFontSpacingPx_OmittedOnlyWhenZero(int spacingGlyph, double? expected)
    {
        Assert.Equal(expected, Sts2RichRoleFontEmit.RoleFontSpacingPxOrNull(spacingGlyph));
    }
}
