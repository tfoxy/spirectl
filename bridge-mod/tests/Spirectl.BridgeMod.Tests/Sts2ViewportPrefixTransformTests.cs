using Spirectl.Sts2.Core.SceneInspection;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// Locks the viewport→screen fit-scale math used to map a SubViewport's pixel space into the on-screen rect of
// the node that displays it via a ViewportTexture (the producer bakes this into SubViewport-content transforms
// so the multiplayer card-intent preview lands at the player's character instead of at (0,0)). Godot-free, so
// it runs without the live host (mirrors Sts2MapDrawingTransformTests).
public sealed class Sts2ViewportPrefixTransformTests
{
    [Fact]
    public void AspectMatchedKeepAspect_IsUniformHalfScale_NoOffset()
    {
        // The multiplayer card: CardIntent rect 90x112.5 displays CardSubViewport 180x225 keep-aspect-centered.
        // Aspect matches (0.8), so the viewport maps at a uniform 0.5 scale with no letterbox offset.
        var fit = Sts2ViewportPrefixTransform.Compute(90.0, 112.5, 180.0, 225.0, keepAspect: true);

        Assert.NotNull(fit);
        Assert.Equal(0.5, fit!.Value.ScaleX, 6);
        Assert.Equal(0.5, fit.Value.ScaleY, 6);
        Assert.Equal(0.0, fit.Value.OffsetX, 6);
        Assert.Equal(0.0, fit.Value.OffsetY, 6);

        // The Card sits centered in the viewport at local (90,112.5); through the fit it lands at the center of
        // the consumer's 90x112.5 box.
        Assert.Equal(45.0, 90.0 * fit.Value.ScaleX + fit.Value.OffsetX, 6);
        Assert.Equal(56.25, 112.5 * fit.Value.ScaleY + fit.Value.OffsetY, 6);
    }

    [Fact]
    public void NonMatchingAspectKeepAspect_LetterboxesOnTheConstrainedAxis()
    {
        // Square rect, taller-than-wide viewport (180x225): the HEIGHT binds (100/225 < 100/180), so
        // scale = 100/225 and the narrower scaled width is centered horizontally (pillarbox on X).
        var fit = Sts2ViewportPrefixTransform.Compute(100.0, 100.0, 180.0, 225.0, keepAspect: true);

        Assert.NotNull(fit);
        var s = 100.0 / 225.0;
        Assert.Equal(s, fit!.Value.ScaleX, 6);
        Assert.Equal(s, fit.Value.ScaleY, 6);
        Assert.Equal((100.0 - 180.0 * s) / 2.0, fit.Value.OffsetX, 6); // = (100 - 80)/2 = 10
        Assert.Equal(0.0, fit.Value.OffsetY, 6);
    }

    [Fact]
    public void Fill_ScalesEachAxisIndependently_NoOffset()
    {
        var fit = Sts2ViewportPrefixTransform.Compute(90.0, 112.5, 180.0, 225.0, keepAspect: false);

        Assert.NotNull(fit);
        Assert.Equal(0.5, fit!.Value.ScaleX, 6);
        Assert.Equal(0.5, fit.Value.ScaleY, 6);
        Assert.Equal(0.0, fit.Value.OffsetX, 6);
        Assert.Equal(0.0, fit.Value.OffsetY, 6);
    }

    [Theory]
    [InlineData(0.0, 100.0, 180.0, 225.0)] // zero rect width
    [InlineData(90.0, 112.5, 0.0, 225.0)]  // zero viewport width
    [InlineData(90.0, 112.5, 180.0, -1.0)] // negative viewport height
    public void DegenerateSizes_ReturnNoPrefix(double rectW, double rectH, double vpW, double vpH)
    {
        Assert.Null(Sts2ViewportPrefixTransform.Compute(rectW, rectH, vpW, vpH, keepAspect: true));
        Assert.Null(Sts2ViewportPrefixTransform.Compute(rectW, rectH, vpW, vpH, keepAspect: false));
    }

    [Theory]
    [InlineData(0, false)] // Scale
    [InlineData(1, false)] // Tile
    [InlineData(2, false)] // Keep
    [InlineData(3, false)] // KeepCentered
    [InlineData(4, true)]  // KeepAspect
    [InlineData(5, true)]  // KeepAspectCentered (the card intents)
    [InlineData(6, true)]  // KeepAspectCovered
    public void StretchKeepsAspect_MatchesGodotStretchModeEnum(int stretchMode, bool expected)
    {
        Assert.Equal(expected, Sts2ViewportPrefixTransform.StretchKeepsAspect(stretchMode));
    }

    // ---- SubViewportContainer branch (WS-A) -------------------------------------------------------------------
    // A SubViewportContainer has no ViewportTexture to find: it DRAWS its child viewport itself, at its own
    // control-local origin. Godot 4 SubViewportContainer::_notification(NOTIFICATION_DRAW):
    //     stretch ? draw_texture_rect(tex, Rect2(Vector2(), get_size()))
    //             : draw_texture_rect(tex, Rect2(Vector2(), c->get_size()))
    // so a NON-stretching container is a 1:1 blit from its origin (its own size does not enter), and a stretching
    // one magnifies the viewport (which it has already resized to get_size()/stretch_shrink) across its full box.
    // The container's on-screen POSITION rides GetGlobalTransformWithCanvas in the watcher, never this fit — hence
    // no offsets anywhere below.

    [Fact]
    public void EpochCard_NonStretchingContainer_IsIdentityFit()
    {
        // scenes/timeline_screen/epoch.tscn: SubViewportContainer 324x200 (offset 686,1827 → 1010,2027) over a
        // SubViewport size=Vector2i(324,200) with no `stretch` in the .tscn (Godot default false). 1:1 blit.
        var fit = Sts2ViewportPrefixTransform.ComputeContainerFit(
            324.0, 200.0, 324.0, 200.0, stretch: false, stretchShrink: 1);

        Assert.NotNull(fit);
        Assert.Equal(1.0, fit!.Value.ScaleX, 6);
        Assert.Equal(1.0, fit.Value.ScaleY, 6);
        Assert.Equal(0.0, fit.Value.OffsetX, 6);
        Assert.Equal(0.0, fit.Value.OffsetY, 6);
    }

    [Fact]
    public void EpochSlot_NonStretchingContainer_IsIdentityFit()
    {
        // scenes/timeline_screen/epoch_slot.tscn: SubViewportContainer 162x100 (offset -81,1350 → 81,1450) over a
        // SubViewport size=Vector2i(162,100), again non-stretching. Same 1:1 blit.
        var fit = Sts2ViewportPrefixTransform.ComputeContainerFit(
            162.0, 100.0, 162.0, 100.0, stretch: false, stretchShrink: 1);

        Assert.NotNull(fit);
        Assert.Equal(1.0, fit!.Value.ScaleX, 6);
        Assert.Equal(1.0, fit.Value.ScaleY, 6);
        Assert.Equal(0.0, fit.Value.OffsetX, 6);
        Assert.Equal(0.0, fit.Value.OffsetY, 6);
    }

    [Fact]
    public void NonStretchingContainer_IgnoresItsOwnSize()
    {
        // Non-stretch draws at the VIEWPORT's size, so a container that is larger (or smaller) than its viewport
        // still blits 1:1 from its origin — it under-fills / overflows exactly as Godot does. Using
        // containerSize/viewportSize here would misplace the content.
        var bigger = Sts2ViewportPrefixTransform.ComputeContainerFit(
            648.0, 400.0, 324.0, 200.0, stretch: false, stretchShrink: 1);
        var smaller = Sts2ViewportPrefixTransform.ComputeContainerFit(
            162.0, 100.0, 324.0, 200.0, stretch: false, stretchShrink: 1);

        Assert.Equal(1.0, bigger!.Value.ScaleX, 6);
        Assert.Equal(1.0, bigger.Value.ScaleY, 6);
        Assert.Equal(1.0, smaller!.Value.ScaleX, 6);
        Assert.Equal(1.0, smaller.Value.ScaleY, 6);
    }

    [Theory]
    [InlineData(1, 324.0, 200.0)] // no shrink: viewport == container
    [InlineData(2, 162.0, 100.0)] // shrink 2: Godot resized the viewport to container/2 → 2x magnification
    [InlineData(4, 81.0, 50.0)]   // shrink 4 → 4x
    public void StretchingContainer_MagnifiesByTheMeasuredContainerOverViewportRatio(
        int stretchShrink,
        double vpW,
        double vpH)
    {
        // With stretch the container draws across its FULL box while the viewport renders at container/shrink, so
        // the drawn magnification is containerSize/viewportSize — which equals stretch_shrink once Godot's resize
        // notification has been applied. We use the MEASURED ratio because that is what the draw call does.
        var fit = Sts2ViewportPrefixTransform.ComputeContainerFit(
            324.0, 200.0, vpW, vpH, stretch: true, stretchShrink);

        Assert.NotNull(fit);
        Assert.Equal(stretchShrink, fit!.Value.ScaleX, 6);
        Assert.Equal(stretchShrink, fit.Value.ScaleY, 6);
        Assert.Equal(0.0, fit.Value.OffsetX, 6);
        Assert.Equal(0.0, fit.Value.OffsetY, 6);
    }

    [Fact]
    public void StretchingContainer_MeasuredRatioWinsOverADisagreeingShrink()
    {
        // Mid-resize (or a manually resized viewport) the two disagree. The DRAW is still container ← viewport, so
        // the measured ratio is ground truth and the shrink value is only the mechanism that normally produces it.
        var fit = Sts2ViewportPrefixTransform.ComputeContainerFit(
            324.0, 200.0, 324.0, 200.0, stretch: true, stretchShrink: 2);

        Assert.NotNull(fit);
        Assert.Equal(1.0, fit!.Value.ScaleX, 6);
        Assert.Equal(1.0, fit.Value.ScaleY, 6);
    }

    [Fact]
    public void StretchingContainer_AnisotropicBoxScalesEachAxisIndependently()
    {
        var fit = Sts2ViewportPrefixTransform.ComputeContainerFit(
            324.0, 400.0, 162.0, 100.0, stretch: true, stretchShrink: 2);

        Assert.NotNull(fit);
        Assert.Equal(2.0, fit!.Value.ScaleX, 6);
        Assert.Equal(4.0, fit.Value.ScaleY, 6);
    }

    [Theory]
    [InlineData(0.0, 200.0, 324.0, 200.0)] // zero container width
    [InlineData(324.0, -1.0, 324.0, 200.0)] // negative container height
    [InlineData(324.0, 200.0, 0.0, 200.0)] // zero viewport width
    [InlineData(324.0, 200.0, 324.0, 0.0)] // zero viewport height
    public void DegenerateContainerOrViewport_ReturnsNoFit(double cw, double ch, double vw, double vh)
    {
        Assert.Null(Sts2ViewportPrefixTransform.ComputeContainerFit(cw, ch, vw, vh, stretch: false, stretchShrink: 1));
        Assert.Null(Sts2ViewportPrefixTransform.ComputeContainerFit(cw, ch, vw, vh, stretch: true, stretchShrink: 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void StretchingContainer_InvalidShrink_ReturnsNoFit_ConservativeFallback(int stretchShrink)
    {
        // Godot itself forbids shrink < 1. Rather than guess, report NO fit: the watcher then reports no prefix
        // (today's behaviour) and Sts2ViewportContentPrune's SubViewportContainer exclusion keeps the content
        // streaming instead of pruning it.
        Assert.Null(Sts2ViewportPrefixTransform.ComputeContainerFit(
            324.0, 200.0, 162.0, 100.0, stretch: true, stretchShrink));

        // Non-stretch never consults the shrink at all (Godot only applies it while stretching).
        Assert.NotNull(Sts2ViewportPrefixTransform.ComputeContainerFit(
            324.0, 200.0, 162.0, 100.0, stretch: false, stretchShrink));
    }
}
