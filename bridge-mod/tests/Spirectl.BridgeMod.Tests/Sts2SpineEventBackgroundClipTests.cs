using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Embedding;
using Spirectl.Sts2.Live;
using Xunit;
using LaneKind = Spirectl.Sts2.Live.Sts2SpineEventBackgroundClip.SpineClipLaneKind;

namespace Spirectl.BridgeMod.Tests;

// WS-NEOW: the composed event-background spine-clip lane's PURE seams — routing (which lane a spine:// clip
// resolves to) + node-local placement math (Local = T_node^-1(union)) — plus the still-placement forwarding
// through the timeline wrapper and the SPCL ClipLocal* wire mapping. All offline (no live host required for the
// pure cases), pinned against the real neow numbers (SpineSprite scale 0.58 origin(-390,-57)).
public sealed class Sts2SpineEventBackgroundClipTests
{
    // neow.tscn: SpineSprite is a direct child of the Neow Control root at position (-390,-57) scale (0.58,0.58);
    // its cave-layer TextureRects overscan (-330,-49)..(2252,1172).
    private const double NeowOriginX = -390d;
    private const double NeowOriginY = -57d;
    private const double NeowScale = 0.58d;
    private const double OverscanX = -330d;
    private const double OverscanY = -49d;
    private const double OverscanW = 2582d; // 2252 - (-330)
    private const double OverscanH = 1221d; // 1172 - (-49)

    // ---- Routing truth table -------------------------------------------------------------------------------

    // The exact contract mirrored independently of the implementation, over every input combo.
    private static LaneKind Expected(bool isBackgroundStillScene, bool isCharacterSelect, bool still, bool composedEnabled)
    {
        if (still && isBackgroundStillScene)
        {
            return LaneKind.BackgroundStill;
        }

        if (!still && isBackgroundStillScene && !isCharacterSelect && composedEnabled)
        {
            return LaneKind.ComposedEventClip;
        }

        return LaneKind.Generic;
    }

    [Fact]
    public void ResolveSpineClipLaneKind_ExhaustiveTruthTable()
    {
        var bools = new[] { false, true };
        foreach (var background in bools)
        foreach (var charSelect in bools)
        foreach (var still in bools)
        foreach (var composed in bools)
        {
            var actual = Sts2SpineEventBackgroundClip.ResolveSpineClipLaneKind(background, charSelect, still, composed);
            var expected = Expected(background, charSelect, still, composed);
            Assert.True(
                actual == expected,
                $"background={background} charSelect={charSelect} still={still} composed={composed}: expected {expected}, got {actual}");
        }
    }

    [Fact]
    public void Routing_MerchantNeverReachesComposedLane()
    {
        // The merchant shop is NOT a background-still scene (IsBackgroundSpineStillScene returns false), so the
        // shop-crash guard is intact: it stays Generic (the proven-safe detach lane) for EVERY still/composed combo.
        foreach (var still in new[] { false, true })
        foreach (var composed in new[] { false, true })
        {
            Assert.Equal(
                LaneKind.Generic,
                Sts2SpineEventBackgroundClip.ResolveSpineClipLaneKind(isBackgroundStillScene: false, isCharacterSelect: false, still, composed));
        }
    }

    [Fact]
    public void Routing_EventBackgroundClip_TakesComposedLaneOnlyWhenEnabled()
    {
        Assert.Equal(
            LaneKind.ComposedEventClip,
            Sts2SpineEventBackgroundClip.ResolveSpineClipLaneKind(isBackgroundStillScene: true, isCharacterSelect: false, still: false, composedClipEnabled: true));

        // Kill switch (SPIRECTL_SPINE_EVENTBG_COMPOSED_CLIP=0) → today's routing (generic detached lane).
        Assert.Equal(
            LaneKind.Generic,
            Sts2SpineEventBackgroundClip.ResolveSpineClipLaneKind(isBackgroundStillScene: true, isCharacterSelect: false, still: false, composedClipEnabled: false));
    }

    [Fact]
    public void Routing_CharacterSelectClip_NeverComposed_StillStaysBackground()
    {
        // A char-select CLIP is excluded from the composed lane (its stills stay their own extractor).
        Assert.Equal(
            LaneKind.Generic,
            Sts2SpineEventBackgroundClip.ResolveSpineClipLaneKind(isBackgroundStillScene: true, isCharacterSelect: true, still: false, composedClipEnabled: true));
        // A char-select STILL still routes to the existing background-still lane.
        Assert.Equal(
            LaneKind.BackgroundStill,
            Sts2SpineEventBackgroundClip.ResolveSpineClipLaneKind(isBackgroundStillScene: true, isCharacterSelect: true, still: true, composedClipEnabled: true));
    }

    // ---- Placement math ------------------------------------------------------------------------------------

    [Fact]
    public void ComposeCellPlacement_NeowNumbers_MapsUnionToBoneSpace()
    {
        // A clip captured at full res (s=1) over a union rect at (0,0) sized 1160x1160 in scene-root space, with the
        // neow node transform, maps the rect back to the SpineSprite's local (bone) space: the top-left offset by
        // -origin/scale, extents divided by the 0.58 scale.
        const int cell = 1160;
        var placement = Sts2SpineEventBackgroundClip.ComposeCellPlacement(
            unionX: 0d, unionY: 0d,
            nodeOriginX: NeowOriginX, nodeOriginY: NeowOriginY,
            nodeScaleX: NeowScale, nodeScaleY: NeowScale,
            nodeAxisAligned: true,
            captureScale: 1d,
            cellWidth: cell, cellHeight: cell);

        Assert.NotNull(placement);
        Assert.Equal((0d - NeowOriginX) / NeowScale, placement!.Value.LocalX, 6);
        Assert.Equal((0d - NeowOriginY) / NeowScale, placement.Value.LocalY, 6);
        Assert.Equal(cell / NeowScale, placement.Value.LocalWidth, 6);
        Assert.Equal(cell / NeowScale, placement.Value.LocalHeight, 6);
    }

    [Theory]
    [InlineData(0d, 0d, 1160, 1160)]
    [InlineData(-330d, -49d, 2582, 1221)] // neow overscan-sized cell
    [InlineData(120.5d, -33.25d, 733, 918)]
    public void ComposeCellPlacement_ForwardMapRoundTrip(double unionX, double unionY, int cellW, int cellH)
    {
        const double s = 1d;
        var placement = Sts2SpineEventBackgroundClip.ComposeCellPlacement(
            unionX, unionY, NeowOriginX, NeowOriginY, NeowScale, NeowScale, nodeAxisAligned: true, s, cellW, cellH);
        Assert.NotNull(placement);

        // Forward map: origin + scale*Local recovers the union top-left; scale*LocalW*s recovers the integer cell.
        Assert.Equal(unionX, NeowOriginX + (NeowScale * placement!.Value.LocalX), 6);
        Assert.Equal(unionY, NeowOriginY + (NeowScale * placement.Value.LocalY), 6);
        Assert.Equal(cellW, NeowScale * placement.Value.LocalWidth * s, 6);
        Assert.Equal(cellH, NeowScale * placement.Value.LocalHeight * s, 6);
    }

    [Fact]
    public void ComposeCellPlacement_CaptureScaleInvariant_SubOneFoldStaysCentered()
    {
        // The phone-memory fallback shrinks the raster (s<1) but the node-local placement is IDENTICAL: a full-res
        // s=1 (cell 1000) and a half-res s=0.5 (cell 500) describe the same bone-space rect — so the clip renders at
        // true size + centered either way, only lower resolution.
        var full = Sts2SpineEventBackgroundClip.ComposeCellPlacement(
            10d, 20d, NeowOriginX, NeowOriginY, NeowScale, NeowScale, nodeAxisAligned: true, captureScale: 1d, cellWidth: 1000, cellHeight: 800);
        var half = Sts2SpineEventBackgroundClip.ComposeCellPlacement(
            10d, 20d, NeowOriginX, NeowOriginY, NeowScale, NeowScale, nodeAxisAligned: true, captureScale: 0.5d, cellWidth: 500, cellHeight: 400);

        Assert.NotNull(full);
        Assert.NotNull(half);
        Assert.Equal(full!.Value.LocalX, half!.Value.LocalX, 6);
        Assert.Equal(full.Value.LocalY, half.Value.LocalY, 6);
        Assert.Equal(full.Value.LocalWidth, half.Value.LocalWidth, 6);
        Assert.Equal(full.Value.LocalHeight, half.Value.LocalHeight, 6);
    }

    [Fact]
    public void ComposeCellPlacement_RotatedOrDegenerate_ReturnsNullForLegacyLane()
    {
        // Rotation/skew (non-axis-aligned) → null → generic detached lane.
        Assert.Null(Sts2SpineEventBackgroundClip.ComposeCellPlacement(
            0d, 0d, NeowOriginX, NeowOriginY, NeowScale, NeowScale, nodeAxisAligned: false, captureScale: 1d, cellWidth: 100, cellHeight: 100));

        // Non-positive / degenerate node scale → null (a flip can't be a positive-width rect).
        Assert.Null(Sts2SpineEventBackgroundClip.ComposeCellPlacement(
            0d, 0d, NeowOriginX, NeowOriginY, -0.58d, NeowScale, nodeAxisAligned: true, captureScale: 1d, cellWidth: 100, cellHeight: 100));
        Assert.Null(Sts2SpineEventBackgroundClip.ComposeCellPlacement(
            0d, 0d, NeowOriginX, NeowOriginY, NeowScale, 0d, nodeAxisAligned: true, captureScale: 1d, cellWidth: 100, cellHeight: 100));

        // Zero capture scale / empty cell → null.
        Assert.Null(Sts2SpineEventBackgroundClip.ComposeCellPlacement(
            0d, 0d, NeowOriginX, NeowOriginY, NeowScale, NeowScale, nodeAxisAligned: true, captureScale: 0d, cellWidth: 100, cellHeight: 100));
        Assert.Null(Sts2SpineEventBackgroundClip.ComposeCellPlacement(
            0d, 0d, NeowOriginX, NeowOriginY, NeowScale, NeowScale, nodeAxisAligned: true, captureScale: 1d, cellWidth: 0, cellHeight: 100));
    }

    // ---- Capture scale + footprint fold --------------------------------------------------------------------

    [Fact]
    public void ComputeCaptureScale_ShrinksOnlyAboveTheCeiling()
    {
        Assert.Equal(1d, Sts2SpineEventBackgroundClip.ComputeCaptureScale(maxDim: 1160d, ceiling: 4096d), 9);
        Assert.Equal(1d, Sts2SpineEventBackgroundClip.ComputeCaptureScale(maxDim: 4096d, ceiling: 4096d), 9);
        Assert.Equal(4096d / 8192d, Sts2SpineEventBackgroundClip.ComputeCaptureScale(maxDim: 8192d, ceiling: 4096d), 9);
    }

    [Fact]
    public void FoldFootprintBudget_NoFoldWhenUnderBudget_ShrinksWhenOver()
    {
        // Under budget → unchanged.
        Assert.Equal(1d, Sts2SpineEventBackgroundClip.FoldFootprintBudget(1d, 1000, 1000, 10, budgetDecodedPixels: 96_000_000L), 9);

        // Over budget → uniform sqrt shrink (cell 4000x4000 * 20f = 320M px vs a 96M budget).
        var folded = Sts2SpineEventBackgroundClip.FoldFootprintBudget(1d, 4000, 4000, 20, budgetDecodedPixels: 96_000_000L);
        Assert.True(folded < 1d && folded > 0d, $"expected a sub-1 fold, got {folded}");
        var expected = System.Math.Sqrt(96_000_000d / (4000d * 4000d * 20d));
        Assert.Equal(expected, folded, 9);

        // Disabled budget / non-positive scale → unchanged (no fold).
        Assert.Equal(1d, Sts2SpineEventBackgroundClip.FoldFootprintBudget(1d, 4000, 4000, 20, budgetDecodedPixels: 0L), 9);
    }

    // ---- Still placement forwarding + SPCL null->0 default --------------------------------------------------

    [Fact]
    public void StillPlacement_NeowOverscan_ComputesForwardableRect()
    {
        // The value ExtractEventBackgroundSpineStillAsync computes from the overscan rect + T_node and forwards
        // through WrapStillImageAsTimeline: captureScale=1, cell == raster == overscan size.
        var placement = Sts2SpineEventBackgroundClip.ComposeCellPlacement(
            OverscanX, OverscanY, NeowOriginX, NeowOriginY, NeowScale, NeowScale,
            nodeAxisAligned: true, captureScale: 1d, cellWidth: (int)OverscanW, cellHeight: (int)OverscanH);
        Assert.NotNull(placement);
        Assert.Equal((OverscanX - NeowOriginX) / NeowScale, placement!.Value.LocalX, 6);
        Assert.Equal((OverscanY - NeowOriginY) / NeowScale, placement.Value.LocalY, 6);
        Assert.Equal(OverscanW / NeowScale, placement.Value.LocalWidth, 6);
        Assert.Equal(OverscanH / NeowScale, placement.Value.LocalHeight, 6);
        // Non-zero (the whole point of the bugfix — the old ClipLocal*=0 rendered scale(0) = invisible).
        Assert.True(placement.Value.LocalWidth > 1d && placement.Value.LocalHeight > 1d);
    }

    // ---- WS5: the char-select still's TRIMMED capture region -------------------------------------------------

    // The char-select still lane renders into a synthetic viewport-sized root (frame position (0,0), scale 1) but
    // TRIMS transparent bounds, so the raster is a sub-rect of the capture, not the whole capture. The placement
    // must therefore be composed from the reported trim region — composing it from the full viewport rect would
    // anchor the raster at the viewport origin and stretch it over the whole viewport.
    [Fact]
    public void CharacterSelectStillPlacement_UsesTheTrimRegionNotTheWholeViewport()
    {
        // Synthetic char-select root: AnimatedBg at (-388,-80) scale 1.1 (pivot (1280,600)), spine inside it.
        // T_node (root -> spine) for a spine at the container origin: origin = (-388,-80) + pivot*(1-1.1),
        // scale 1.1. Only the numbers matter here, not their provenance.
        const double originX = -516d;
        const double originY = -140d;
        const double scale = 1.1d;
        // Trim region inside a 1920x1080 capture.
        const int trimX = 132;
        const int trimY = 40;
        const int trimW = 1740;
        const int trimH = 1010;

        var placement = Sts2SpineEventBackgroundClip.ComposeCellPlacement(
            trimX, trimY, originX, originY, scale, scale,
            nodeAxisAligned: true, captureScale: 1d, cellWidth: trimW, cellHeight: trimH);

        Assert.NotNull(placement);
        Assert.Equal((trimX - originX) / scale, placement!.Value.LocalX, 6);
        Assert.Equal((trimY - originY) / scale, placement.Value.LocalY, 6);
        Assert.Equal(trimW / scale, placement.Value.LocalWidth, 6);
        Assert.Equal(trimH / scale, placement.Value.LocalHeight, 6);

        // The regression this closes: a non-zero LocalWidth. The client computes `scale = localWidth / canvasWidth`,
        // so ClipLocalWidth=0 (the old null placement -> SPCL 0 default) painted the backdrop at scale(0).
        Assert.True(placement.Value.LocalWidth > 1d);
        Assert.NotEqual(0d, placement.Value.LocalWidth);

        // And it must NOT be the whole-viewport placement — that is the wrong rect for a trimmed raster.
        var wholeViewport = Sts2SpineEventBackgroundClip.ComposeCellPlacement(
            0, 0, originX, originY, scale, scale,
            nodeAxisAligned: true, captureScale: 1d, cellWidth: 1920, cellHeight: 1080);
        Assert.NotNull(wholeViewport);
        Assert.NotEqual(wholeViewport!.Value.LocalX, placement.Value.LocalX, 6);
        Assert.NotEqual(wholeViewport.Value.LocalWidth, placement.Value.LocalWidth, 6);
    }

    // The client's paint transform is `translate(localX, localY) scale(localWidth / canvasWidth)` about the node
    // origin, so a round trip of the raster's own corners through the placement must land on the capture rect
    // expressed in node-local units — i.e. the trim offset is preserved, not swallowed.
    [Fact]
    public void CharacterSelectStillPlacement_RoundTripsTheTrimOffset()
    {
        const double originX = -516d;
        const double originY = -140d;
        const double nodeScale = 1.1d;
        const int trimX = 132;
        const int trimY = 40;
        const int trimW = 1740;
        const int trimH = 1010;

        var placement = Sts2SpineEventBackgroundClip.ComposeCellPlacement(
            trimX, trimY, originX, originY, nodeScale, nodeScale,
            nodeAxisAligned: true, captureScale: 1d, cellWidth: trimW, cellHeight: trimH);
        Assert.NotNull(placement);

        var s = placement!.Value.LocalWidth / trimW; // the client's canvas->local scale
        // raster pixel (0,0) and (trimW,trimH) mapped to node-local, then forward through T_node to capture space.
        var localLeft = placement.Value.LocalX + (0 * s);
        var localRight = placement.Value.LocalX + (trimW * s);
        Assert.Equal(trimX, originX + (localLeft * nodeScale), 6);
        Assert.Equal(trimX + trimW, originX + (localRight * nodeScale), 6);

        var sY = placement.Value.LocalHeight / trimH;
        Assert.Equal(trimY, originY + (placement.Value.LocalY * nodeScale), 6);
        Assert.Equal(trimY + trimH, originY + ((placement.Value.LocalY + (trimH * sY)) * nodeScale), 6);
    }

    [Fact]
    public void MapResult_ForwardsClipPlacement_NullDefaultsToZero()
    {
        var frame = new AssetExtractFrame(
            Index: 0, Format: "png", ContentType: "image/png", Width: 100, Height: 80,
            Contents: [1, 2, 3], DurationMs: 0, OffsetX: 0, OffsetY: 0, CanvasWidth: 100, CanvasHeight: 80);

        // A timeline result carrying a placement → SPCL ClipLocal* forwarded verbatim.
        var withPlacement = AssetExtractOperationResult.SuccessTimeline(
            requestId: "req-1", source: DataSourceKind.Live, provisional: false, format: "png", width: 100, height: 80,
            renderMode: "composed-event-background-spine-clip", durationMs: 0, frames: [frame], notes: [],
            clipPlacement: new AssetExtractClipPlacement(103.4, 13.7, 4451.7, 2105.1));
        var mapped = BridgeEmbeddableAssetProvider.MapResult("res://scenes/events/background_scenes/neow.tscn", withPlacement);
        Assert.True(mapped.Success);
        Assert.Equal(103.4, mapped.Payload!.ClipLocalX, 6);
        Assert.Equal(13.7, mapped.Payload.ClipLocalY, 6);
        Assert.Equal(4451.7, mapped.Payload.ClipLocalWidth, 6);
        Assert.Equal(2105.1, mapped.Payload.ClipLocalHeight, 6);

        // A null placement (a rotated/skewed node, or the char-select lane under
        // SPIRECTL_SPINE_CHARSELECT_PLACEMENT=0) → the pinned 0 default.
        var noPlacement = AssetExtractOperationResult.SuccessTimeline(
            requestId: "req-2", source: DataSourceKind.Live, provisional: false, format: "png", width: 100, height: 80,
            renderMode: "flattened-event-background-spine-still", durationMs: 0, frames: [frame], notes: [],
            clipPlacement: null);
        var mappedNull = BridgeEmbeddableAssetProvider.MapResult("res://scenes/screens/char_select/char_select_bg_silent.tscn", noPlacement);
        Assert.True(mappedNull.Success);
        Assert.Equal(0d, mappedNull.Payload!.ClipLocalX);
        Assert.Equal(0d, mappedNull.Payload.ClipLocalY);
        Assert.Equal(0d, mappedNull.Payload.ClipLocalWidth);
        Assert.Equal(0d, mappedNull.Payload.ClipLocalHeight);
    }

#if ENABLE_STS2_LIVE_HOST
    [Fact]
    public void WrapStillImageAsTimeline_ForwardsTheStillsClipPlacement()
    {
        var provider = new Sts2AssetExtractProvider(new Spirectl.Sts2.Core.Logging.InMemoryLogStream());
        var request = new AssetExtractRequestSnapshot(
            RequestId: "req-still", SourceRoot: "resources",
            SourcePath: "res://scenes/events/background_scenes/neow.tscn",
            LoadPath: "res://scenes/events/background_scenes/neow.tscn", OutputFormat: "png");

        var placement = new AssetExtractClipPlacement(103.4, 13.7, 4451.7, 2105.1);
        var still = AssetExtractOperationResult.Success(
            requestId: "req-still", source: DataSourceKind.Live, provisional: false, format: "png",
            width: 2582, height: 1221, contents: [9, 9, 9], renderMode: "flattened-event-background-spine-still", notes: ["still"]) with
        {
            ClipPlacement = placement,
        };

        var wrapped = provider.WrapStillImageAsTimeline(still, request);
        Assert.Null(wrapped.Error);
        Assert.Equal(AssetExtractArtifactKind.Timeline, wrapped.ArtifactKind);
        Assert.NotNull(wrapped.ClipPlacement);
        Assert.Equal(placement.LocalX, wrapped.ClipPlacement!.LocalX, 6);
        Assert.Equal(placement.LocalWidth, wrapped.ClipPlacement.LocalWidth, 6);

        // A still WITHOUT a placement (char-select) stays null through the wrapper (→ SPCL 0 default downstream).
        var stillNoPlacement = AssetExtractOperationResult.Success(
            requestId: "req-still2", source: DataSourceKind.Live, provisional: false, format: "png",
            width: 10, height: 10, contents: [1], renderMode: "flattened-character-select-bg-spine-still", notes: []);
        var wrappedNull = provider.WrapStillImageAsTimeline(stillNoPlacement, request);
        Assert.Null(wrappedNull.ClipPlacement);
    }
#endif
}
