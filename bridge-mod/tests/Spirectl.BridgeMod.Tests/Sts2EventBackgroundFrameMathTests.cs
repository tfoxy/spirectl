using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// The event-backdrop placement arithmetic (extracted from the live-host-only
// TryResolveEventBackgroundFrame so it is provable without a game). Two consumers, two contracts:
// the recon-still lane keeps the game's own aspect lerp (Resolve — these cases pin the extraction
// as verbatim), and the explicit-size still lane (couch-coop's static background) gets the MIRROR
// composition (ResolveMirrorCentered — the 16:9 reference frame translated to the viewport center,
// because the web mirror re-centers the streamed 16:9 layout rigidly rather than re-lerping).
public sealed class Sts2EventBackgroundFrameMathTests
{
    private const float Tolerance = 1e-3f;

    // The clamp knees sit a float ulp away from the exact ratios (1.7777 vs 16/9, 1.3333 vs 4/3), so scales come
    // out ~1e-5 off their idealized endpoints. That slack is the knee's, not the arithmetic's.
    private const float ScaleTolerance = 1e-4f;

    [Fact]
    public void Resolve_At16x9_IsTheReferenceFrame()
    {
        var frame = Sts2EventBackgroundFrameMath.Resolve(1920f, 1080f);
        // ratio 1.77778 sits a hair ABOVE the 1.7777 knee, so the wide-segment weight is ~5.6e-5, not 0:
        // position (0, 40) + the 960·(1−scale) shrink re-center lands at 105.6314 (the idealized 105.6 plus the
        // knee's 0.031 px), scale 0.89000. These are the values the game has always produced — pinned as-is.
        Assert.Equal(105.6314f, frame.PositionX, Tolerance);
        Assert.Equal(40f, frame.PositionY, Tolerance);
        Assert.Equal(0.89f, frame.Scale, ScaleTolerance);
    }

    [Fact]
    public void Resolve_AtTheWideClamp_IsTheGamesOwnUltraWideFrame()
    {
        var frame = Sts2EventBackgroundFrameMath.Resolve(2520f, 1080f);
        // ratio 2.333 hits the far end of the wide lerp: scale back to 1.0, position (330, 40),
        // and a zero shrink re-center. This is the frame the MIRROR NEVER SHOWS — the reason
        // ResolveMirrorCentered exists.
        Assert.Equal(330f, frame.PositionX, Tolerance);
        Assert.Equal(40f, frame.PositionY, Tolerance);
        Assert.Equal(1f, frame.Scale, Tolerance);
    }

    [Fact]
    public void Resolve_AtTheNarrowClamp_IsTheGamesNarrowFrame()
    {
        var frame = Sts2EventBackgroundFrameMath.Resolve(1440f, 1080f);
        // ratio 1440/1080 = 1.333333 sits a hair above the 1.3333 clamp knee, so the narrow-segment weight is
        // ~7.5e-5, not 0: (-139.9835, 109.9948) at scale ~0.99999 — the game's real values, pinned as-is.
        Assert.Equal(-139.9835f, frame.PositionX, Tolerance);
        Assert.Equal(109.9948f, frame.PositionY, Tolerance);
        Assert.Equal(1f, frame.Scale, ScaleTolerance);
    }

    [Fact]
    public void ResolveMirrorCentered_At2520_IsTheReferenceFramePlusHalfTheWidening()
    {
        var frame = Sts2EventBackgroundFrameMath.ResolveMirrorCentered(2520f, 1080f);
        // The mirror composes the 16:9 layout shifted by half the widened margin: (105.6314 + 300, 40)
        // at the UNCHANGED ~0.89 scale. Compare Resolve_AtTheWideClamp — same viewport, different
        // picture, and the difference is the whole point.
        Assert.Equal(405.6314f, frame.PositionX, Tolerance);
        Assert.Equal(40f, frame.PositionY, Tolerance);
        Assert.Equal(0.89f, frame.Scale, ScaleTolerance);
    }

    [Fact]
    public void ResolveMirrorCentered_AtTheReferenceSize_IsExactlyTheReferenceFrame()
    {
        var reference = Sts2EventBackgroundFrameMath.Resolve(1920f, 1080f);
        var centered = Sts2EventBackgroundFrameMath.ResolveMirrorCentered(1920f, 1080f);
        // Zero growth → zero translation: an explicit 1920x1080 request must render the same
        // composition the size-less default path renders at a 1920x1080 root viewport.
        Assert.Equal(reference.PositionX, centered.PositionX);
        Assert.Equal(reference.PositionY, centered.PositionY);
        Assert.Equal(reference.Scale, centered.Scale);
    }

    [Fact]
    public void FrameSpec_ParsesTheCanonicalShapeAndRefusesEverythingElse()
    {
        var parsed = Sts2EventBackgroundFrameMath.TryParseFrameSpec("105.6,99.4,0.890");
        Assert.NotNull(parsed);
        Assert.Equal(105.6f, parsed!.Value.PositionX, Tolerance);
        Assert.Equal(99.4f, parsed.Value.PositionY, Tolerance);
        Assert.Equal(0.890f, parsed.Value.Scale, Tolerance);
        Assert.NotNull(Sts2EventBackgroundFrameMath.TryParseFrameSpec("-140.0,110.0,1.000"));
        Assert.Null(Sts2EventBackgroundFrameMath.TryParseFrameSpec(null));
        Assert.Null(Sts2EventBackgroundFrameMath.TryParseFrameSpec(""));
        Assert.Null(Sts2EventBackgroundFrameMath.TryParseFrameSpec("105.6,99.4"));
        Assert.Null(Sts2EventBackgroundFrameMath.TryParseFrameSpec("a,b,c"));
        Assert.Null(Sts2EventBackgroundFrameMath.TryParseFrameSpec("1,2,0"));
        Assert.Null(Sts2EventBackgroundFrameMath.TryParseFrameSpec("1,2,-0.5"));
    }

    [Fact]
    public void CenterFrame_TranslatesAProbedLiveFrameByHalfTheViewportGrowth()
    {
        // The shipped Neow measurement: container at (105.6, 99.4) scale 0.890 in the game's 16:9 layout.
        // At 2520x1080 the mirror composes it shifted +300 in x, untouched in y — scale never re-lerps.
        var live = new Sts2EventBackgroundFrameMath.EventFrame(105.6f, 99.4f, 0.890f);
        var centered = Sts2EventBackgroundFrameMath.CenterFrame(live, 2520f, 1080f);
        Assert.Equal(405.6f, centered.PositionX, Tolerance);
        Assert.Equal(99.4f, centered.PositionY, Tolerance);
        Assert.Equal(0.890f, centered.Scale, Tolerance);
    }

    [Fact]
    public void ResolveMirrorCentered_NeverReLerpsWithTheViewportAspect()
    {
        // The scale is pinned to the reference frame's whatever the requested size — a re-lerp is
        // exactly the bug this API exists to prevent.
        foreach (var width in new[] { 1920f, 2280f, 2520f })
        {
            Assert.Equal(0.89f, Sts2EventBackgroundFrameMath.ResolveMirrorCentered(width, 1080f).Scale, Tolerance);
        }
    }
}
