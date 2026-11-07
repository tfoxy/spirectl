namespace Spirectl.Sts2.Live;

// PURE, Godot-free routing + placement math for the event-background composed spine-clip lane
// (like Sts2ReparentEmit / Sts2RegionEmitCap — unit-testable without the Godot-coupled provider).
//
// Why: the GENERIC tight-crop clip renderer bare-detaches the SpineSprite from its scene ancestors. For a
// full-bleed event-background Spine (res://scenes/events/background_scenes/*) that drops the in-scene container
// transforms the background needs, so the clip renders UNCENTERED, and the creature downscale shrinks the big rig
// so the client upscales it BLURRY.
//
// So event backgrounds (still or clip) route through the in-situ composition (mount the scene root, hide
// siblings, deterministic seek), then expresses the capture rect in the SpineSprite's OWN LOCAL (bone) space so
// the client re-applies the streamed node+ancestor transforms exactly once (no double-apply).
internal static class Sts2SpineEventBackgroundClip
{
    // Which render lane a spine:// clip request resolves to.
    internal enum SpineClipLaneKind
    {
        // The existing single-raster background-still composition wrapped as a 1-frame timeline (char-select OR event
        // background, when the client asked for &still=1). Byte-identical to today.
        BackgroundStill,

        // NEW: the in-situ composed animated clip lane for an EVENT background (non-still, non-char-select) when the
        // composed-clip switch is ON.
        ComposedEventClip,

        // The generic tight-crop / detached clip renderer (creatures, merchant, char-select clips, and anything not a
        // background-still scene). Unchanged — the merchant shop-crash guard lives in IsBackgroundSpineStillScene,
        // which returns false for the merchant so it stays on this proven-safe path.
        Generic,
    }

    // Routing truth table. `isBackgroundStillScene` == IsBackgroundSpineStillScene (char-select OR event background);
    // `isCharacterSelect` narrows the char-select subset within it.
    //   still  + background            -> BackgroundStill (existing still lanes; char-select still stays here too)
    //   clip   + event-bg + composedOn -> ComposedEventClip
    //   everything else                -> Generic
    internal static SpineClipLaneKind ResolveSpineClipLaneKind(
        bool isBackgroundStillScene,
        bool isCharacterSelect,
        bool still,
        bool composedClipEnabled)
    {
        if (still && isBackgroundStillScene)
        {
            return SpineClipLaneKind.BackgroundStill;
        }

        if (!still
            && isBackgroundStillScene
            && !isCharacterSelect
            && composedClipEnabled)
        {
            return SpineClipLaneKind.ComposedEventClip;
        }

        return SpineClipLaneKind.Generic;
    }

    // The node-LOCAL (bone-space) rect a composed clip/still canvas covers, ready to hand to AssetExtractClipPlacement.
    internal readonly record struct SpineClipCellPlacement(
        double LocalX,
        double LocalY,
        double LocalWidth,
        double LocalHeight);

    private const double ScaleEpsilon = 1e-6;

    // Capture scale s = min(1, ceiling / maxDim): shrink the raster only when the union cell would exceed the raster
    // ceiling (4096).
    internal static double ComputeCaptureScale(double maxDim, double ceiling)
        => maxDim > ceiling && maxDim > 0d && ceiling > 0d ? ceiling / maxDim : 1d;

    // SPIRECTL_SPINE_EVENTBG_FULLRES=0 low-memory fallback: fold a decoded-footprint budget INTO the capture scale
    // (cell px x frames <= budget) as a uniform shrink. Because the placement below is s-INVARIANT (s cancels), a
    // uniform footprint shrink keeps the clip centered and true-size, only lower raster resolution — never off-center.
    // Returns the (possibly reduced) capture scale; the caller re-derives the integer cell from it.
    internal static double FoldFootprintBudget(
        double captureScale,
        long cellWidth,
        long cellHeight,
        int frameCount,
        long budgetDecodedPixels)
    {
        if (captureScale <= 0d || budgetDecodedPixels <= 0)
        {
            return captureScale;
        }

        var frames = Math.Max(1, frameCount);
        var footprint = Math.Max(1L, cellWidth) * Math.Max(1L, cellHeight) * frames;
        if (footprint <= budgetDecodedPixels)
        {
            return captureScale;
        }

        return captureScale * Math.Sqrt((double)budgetDecodedPixels / footprint);
    }

    // Local = T_node^-1(R). The clip raster covers scene-root rect R (top-left (unionX, unionY), rendered at
    // captureScale into an integer cellWidth x cellHeight cell). T_node maps the SpineSprite's LOCAL (bone) space to
    // that same scene-root frame (axis-aligned translate+scale). Inverting maps
    // the cell back to bone space so the client, re-applying the node's FULL streamed transform chain, lands it on
    // screen exactly once:
    //   LocalX = (unionX - originX) / scaleX            (exact anchor from the un-ceiled union top-left)
    //   LocalW = (cellWidth / captureScale) / scaleX    (size from the CEIL'd integer cell -> no anchor drift, s cancels)
    // Rotation/skew (non-axis-aligned) or a non-positive/degenerate node scale can't be a simple rect -> null, so the
    // caller falls back to the generic (detached) lane.
    internal static SpineClipCellPlacement? ComposeCellPlacement(
        double unionX,
        double unionY,
        double nodeOriginX,
        double nodeOriginY,
        double nodeScaleX,
        double nodeScaleY,
        bool nodeAxisAligned,
        double captureScale,
        int cellWidth,
        int cellHeight)
    {
        if (!nodeAxisAligned
            || captureScale <= 0d
            || cellWidth <= 0
            || cellHeight <= 0
            || nodeScaleX <= ScaleEpsilon
            || nodeScaleY <= ScaleEpsilon)
        {
            return null;
        }

        return new SpineClipCellPlacement(
            LocalX: (unionX - nodeOriginX) / nodeScaleX,
            LocalY: (unionY - nodeOriginY) / nodeScaleY,
            LocalWidth: (cellWidth / captureScale) / nodeScaleX,
            LocalHeight: (cellHeight / captureScale) / nodeScaleY);
    }
}
