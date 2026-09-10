using Spirectl.Sts2.Live;
using Xunit;
using CellPlacement = Spirectl.Sts2.Live.Sts2SpineEventBackgroundClip.SpineClipCellPlacement;

namespace Spirectl.BridgeMod.Tests;

// WS-SPINESTREAM (FIX 2b) — the OFFLINE seamless-swap proof. FIX 2b paints a cheap 1-frame STILL first, then hot-swaps
// the full animated clip. The swap is seamless because placement is CELL-INVARIANT: a scene-root point lands at the
// same node-local position regardless of the raster cell size / capture scale / union top-left / padding. So the
// still-frame-0 and the clip-frame-0 land at the SAME node-local, and the client's canvas draw shows no edge jump.
public sealed class Sts2SpineStillSwapPlacementTests
{
    // neow SpineSprite node transform (bone/local space -> scene root): origin(-390,-57) scale 0.58.
    private const double OriginX = -390d;
    private const double OriginY = -57d;
    private const double Scale = 0.58d;

    // The client's on-screen draw of a placement (spineClip.ts applySpinePlacement / SpineAttachment._Draw): a
    // scene-root point R renders into the raster at canvas pixel (R - unionTopLeft) * captureScale, and the client
    // draws canvas pixel cp at node-local  nodeLocal = local + cp * (localExtent / canvasSize),  canvasSize == cell.
    // ComposeCellPlacement is built so this collapses to T_node^-1(R) = (R - origin)/scale, INDEPENDENT of
    // union / captureScale / cell — the seamless-swap invariant.
    private static double ClientNodeLocalX(CellPlacement p, double unionX, double captureScale, int cellWidth, double sceneRootX)
        => p.LocalX + ((sceneRootX - unionX) * captureScale) * (p.LocalWidth / cellWidth);

    private static double ClientNodeLocalY(CellPlacement p, double unionY, double captureScale, int cellHeight, double sceneRootY)
        => p.LocalY + ((sceneRootY - unionY) * captureScale) * (p.LocalHeight / cellHeight);

    // ---- Event-background: STILL placement == COMPOSED-CLIP placement (both via the real ComposeCellPlacement) -----

    [Fact]
    public void EventBgStillAndClip_MapSameSceneRootPointToSameNodeLocal()
    {
        // The STILL lane (ExtractEventBackgroundSpineStillAsync): captureScale 1, cell == the overscan raster,
        // union == overscan top-left.
        const double stillUnionX = -330d, stillUnionY = -49d;
        const int stillCellW = 2582, stillCellH = 1221;
        var still = Sts2SpineEventBackgroundClip.ComposeCellPlacement(
            stillUnionX, stillUnionY, OriginX, OriginY, Scale, Scale,
            nodeAxisAligned: true, captureScale: 1d, cellWidth: stillCellW, cellHeight: stillCellH);

        // The COMPOSED CLIP lane: a DIFFERENT union (its own tight union), a sub-1 captureScale, and its own integer
        // cell — deliberately UNLIKE the still's, to prove the swap does not depend on matching capture parameters.
        const double clipUnionX = 120.5d, clipUnionY = -33.25d;
        const double clipScale = 0.72d;
        const int clipCellW = 1733, clipCellH = 1010;
        var clip = Sts2SpineEventBackgroundClip.ComposeCellPlacement(
            clipUnionX, clipUnionY, OriginX, OriginY, Scale, Scale,
            nodeAxisAligned: true, captureScale: clipScale, cellWidth: clipCellW, cellHeight: clipCellH);

        Assert.NotNull(still);
        Assert.NotNull(clip);

        // A handful of scene-root points (e.g. a bone position) — each maps to the SAME node-local under BOTH
        // placements, and that node-local == T_node^-1(R) = (R - origin)/scale. So still-frame-0 and clip-frame-0 draw
        // the same content at the same node-local position: a seamless hot-swap.
        foreach (var (rx, ry) in new[] { (0d, 0d), (960d, 540d), (-330d, 1172d), (2252d, -49d), (500.25d, 133.5d) })
        {
            double expectedX = (rx - OriginX) / Scale;
            double expectedY = (ry - OriginY) / Scale;

            Assert.Equal(expectedX, ClientNodeLocalX(still!.Value, stillUnionX, 1d, stillCellW, rx), 4);
            Assert.Equal(expectedY, ClientNodeLocalY(still.Value, stillUnionY, 1d, stillCellH, ry), 4);
            Assert.Equal(expectedX, ClientNodeLocalX(clip!.Value, clipUnionX, clipScale, clipCellW, rx), 4);
            Assert.Equal(expectedY, ClientNodeLocalY(clip.Value, clipUnionY, clipScale, clipCellH, ry), 4);

            // And therefore the STILL and CLIP agree with each other (the swap invariant, stated directly).
            Assert.Equal(
                ClientNodeLocalX(still.Value, stillUnionX, 1d, stillCellW, rx),
                ClientNodeLocalX(clip.Value, clipUnionX, clipScale, clipCellW, rx), 4);
        }
    }

    // ---- Creature (generic tight-crop lane) cell-invariance ---------------------------------------------------------

    // The generic lane's node-local placement (Sts2AssetExtractProvider.RenderAssets.cs:1208-1213 +
    // SceneHelpers.FrameFromBoundsFitted:215-227): fitScale f = maxDim>2048 ? 2048/maxDim : 1;
    // cell = ceil(size*f) + 2*Padding; nodePos = Padding - boundsPos*f; clipPlacement: LocalX = -nodePos/f,
    // LocalWidth = cell/f. A creature STILL (collapseToStill = frame 0) shares the SAME cell as its clip, so both map a
    // skeleton point identically; the invariance is that a skeleton-local point recovers to ITSELF regardless of
    // fitScale / padding / cell (the cell term cancels EXACTLY, even under the integer ceil). Modeled here because the
    // generic lane has no pure extractable seam (unlike ComposeCellPlacement).
    private const int Padding = 64;          // Sts2AssetExtractProvider.cs:20
    private const double MaxSceneSize = 2048d; // Sts2AssetExtractProvider.cs:19

    private readonly record struct GenericPlacement(double LocalX, double LocalWidth, int CellWidth, double FitScale, double NodePosX);

    private static GenericPlacement GenericCreaturePlacement(double boundsX, double boundsW)
    {
        double f = boundsW > MaxSceneSize ? MaxSceneSize / boundsW : 1d;
        int cell = (int)Math.Ceiling(boundsW * f) + (Padding * 2);
        double nodePosX = Padding - (boundsX * f);
        return new GenericPlacement(LocalX: -nodePosX / f, LocalWidth: cell / f, CellWidth: cell, FitScale: f, NodePosX: nodePosX);
    }

    // A skeleton point at node-local x renders (in the DETACHED bake) to cell pixel nodePos + x*f; the client maps it
    // back to LocalX + cp*(LocalWidth/cell) = x. (localWidth/cell = (cell/f)/cell = 1/f, so cell + padding cancel.)
    private static double ClientNodeLocalXGeneric(GenericPlacement p, double skeletonLocalX)
    {
        double cp = p.NodePosX + (skeletonLocalX * p.FitScale);
        return p.LocalX + (cp * (p.LocalWidth / p.CellWidth));
    }

    [Fact]
    public void CreatureStillAndClip_AreCellInvariant_RecoverSkeletonLocalPoint()
    {
        // Two framings of a creature: a small rig (fitScale 1, no downscale) and a large rig (> MaxSceneSize ->
        // fitScale < 1, the #4 downscale-instead-of-collapse case). A creature still is frame 0 of the clip, so
        // still == clip placement here; the point is that a skeleton-local point recovers to itself either way, so a
        // still and clip drawn from the SAME cell (or a downscaled one) agree exactly.
        var small = GenericCreaturePlacement(boundsX: -120d, boundsW: 900d);   // f == 1 (no downscale)
        var large = GenericCreaturePlacement(boundsX: -1500d, boundsW: 4791d); // f < 1 (the waterfall-giant case)

        Assert.Equal(1d, small.FitScale, 9);
        Assert.True(large.FitScale < 1d, $"expected a downscale for a >2048 rig, got fitScale {large.FitScale}");

        foreach (var x in new[] { -300d, 0d, 120.5d, 640d, 1200d })
        {
            Assert.Equal(x, ClientNodeLocalXGeneric(small, x), 4);
            Assert.Equal(x, ClientNodeLocalXGeneric(large, x), 4);
        }
    }
}
