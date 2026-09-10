namespace Spirectl.Sts2.Live;

// WS5: PURE, Godot-free sizing rules for the generic spine-clip lane's SIDE-BY-SIDE composite (like
// Sts2SpineEventBackgroundClip — unit-testable without the Godot-coupled provider).
//
// The generic clip renderer renders `laneCount` independent skeleton clones into ONE SubViewport, laid out
// left-to-right: `viewport.Size = (cellWidth * laneCount, cellHeight)`, and slices lane i back out with
// `image.GetRegion(new Rect2I(i * cellWidth, 0, cellWidth, cellHeight))`.
//
// THE MERCHANT DEFECT. `FrameFromBoundsFitted` fits a rig's max dimension to MaxSceneSize (2048) and adds
// 2*Padding, so a full-bleed background rig (the merchant's shop_merchant_bottom `bg` attachment is 5120x2400)
// produces a 2176px-wide cell — and with the default 2 lanes the composite asks for a 4352px render target.
// When that request is refused/clamped, `viewport.GetTexture().GetImage()` reads back NARROWER than the
// composite, and Godot's `Image.get_region` does NOT error: it allocates an image of the REQUESTED size and
// blits the CLIPPED source into it, so the missing right-hand columns come back fully transparent. The lane-0
// cell therefore carries only the LEFT slice of the background, tight-cropped to it, on a canvas that still
// claims the full cell width — exactly "the merchant screen shows only the left side of its background".
//
// Lanes are a THROUGHPUT knob only (how many clip frames are captured per render wait), never correctness, so
// they can always be reduced. Two rules, both generic (no per-scene special cases):
//   1. A clip that COLLAPSED to a single still frame emits exactly one frame, so every extra lane is pure waste
//      — and it is precisely the oversized-background case that trips the width ceiling.
//   2. Otherwise, never let `cellWidth * laneCount` exceed the composite ceiling.
internal static class Sts2SpineClipComposite
{
    // How many of the prepared lanes the composite may actually use. Always >= 1 (a single lane is rendered even
    // if its own cell exceeds the ceiling — one cell is the irreducible unit, and the caller asserts the readback).
    internal static int ResolveLaneCount(
        int preparedLaneCount,
        int cellWidth,
        bool collapseToStill,
        int maxCompositeWidth)
    {
        var lanes = Math.Max(1, preparedLaneCount);
        if (collapseToStill)
        {
            return 1;
        }

        if (cellWidth <= 0 || maxCompositeWidth <= 0)
        {
            return lanes;
        }

        var allowed = Math.Max(1, maxCompositeWidth / cellWidth);
        return Math.Min(lanes, allowed);
    }

    // True when a readback is too small to contain the composite the caller laid out — i.e. at least one lane
    // cell (or the right-hand part of one) would be filled with transparent padding by Image.get_region instead
    // of rendered pixels. The caller turns this into a loud note/notice rather than shipping a half-image.
    internal static bool IsReadbackTruncated(
        int imageWidth,
        int imageHeight,
        int cellWidth,
        int cellHeight,
        int laneCount)
        => imageWidth < cellWidth * Math.Max(1, laneCount) || imageHeight < cellHeight;
}
