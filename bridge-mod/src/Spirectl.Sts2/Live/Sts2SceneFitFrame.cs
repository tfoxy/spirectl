namespace Spirectl.Sts2.Live;

/// <summary>
/// Godot-free SCENE-FIT FRAMING: how an off-screen bake sizes the canvas it renders a rig into, and the
/// node-LOCAL rect a consumer needs to place that canvas back under the live node's own transform.
///
/// <para>WHY THIS IS ITS OWN FILE. Both halves used to live inside <c>Sts2AssetExtractProvider</c>, which is
/// live-host-only, so neither could be unit-tested without the game — and the second half (the placement
/// algebra) is now emitted by TWO producers: the raster spine clip's <c>clipPlacement</c> and the geoclip
/// baker's <c>meta.placement</c>. Those two must agree numerically for the same rig, and the only way to
/// guarantee that is one implementation. <c>Sts2AssetExtractProvider.FrameFromBoundsFitted</c> and the geoclip
/// baker are both thin <c>Rect2</c> wrappers over <see cref="Fit"/>.</para>
///
/// <para>THE CONTRACT, stated once. <see cref="Fit"/> maps skeleton-local (Godot units, y-down) to canvas
/// pixels as <c>canvasX = skelX * fitScale + nodePositionX</c>. <see cref="Place"/> inverts that into the
/// node-local rect a client places the canvas by — <c>localX = -nodePositionX / fitScale</c>,
/// <c>localWidth = canvasWidth / fitScale</c> — so a client that computes
/// <c>s = localWidth / canvasWidth = 1 / fitScale</c> and draws the canvas at
/// <c>translate(localX, localY) scale(s)</c> lands every pixel exactly where the bake put it.</para>
/// </summary>
internal static class Sts2SceneFitFrame
{
    /// <summary>Fallback square when the bounds say nothing usable.</summary>
    internal const int DefaultSceneSize = 1024;

    /// <summary>Above this on the longer side the rig is uniformly SCALED to fit rather than cropped.</summary>
    internal const int MaxSceneSize = 2048;

    /// <summary>Transparent margin on every side, in canvas pixels.</summary>
    internal const int Padding = 64;

    /// <summary>
    /// The sizing verdict: a canvas, where the node's origin sits in it, and the uniform scale the node is
    /// rendered at. Deliberately primitive-typed (no <c>Vector2I</c>) so it compiles without the game.
    /// </summary>
    internal readonly record struct FittedFrame(
        int ViewportWidth,
        int ViewportHeight,
        float NodePositionX,
        float NodePositionY,
        float NodeScale);

    /// <summary>
    /// Size a canvas around <paramref name="boundsWidth"/> x <paramref name="boundsHeight"/> at
    /// (<paramref name="boundsX"/>, <paramref name="boundsY"/>), scaling the whole rig DOWN uniformly when its
    /// longer side exceeds <see cref="MaxSceneSize"/>.
    ///
    /// <para>The uniform fit (rather than a per-dimension clamp) is the point: a per-dimension clamp keeps a
    /// fixed-size viewport, so a large rig with a large negative offset overflows the right/bottom edge and is
    /// CROPPED. Scaling to fit captures the whole skeleton.</para>
    /// </summary>
    internal static FittedFrame Fit(float boundsX, float boundsY, float boundsWidth, float boundsHeight)
    {
        var maxDim = MathF.Max(boundsWidth, boundsHeight);
        var fitScale = maxDim > MaxSceneSize ? MaxSceneSize / maxDim : 1f;
        var width = (int)MathF.Ceiling(boundsWidth * fitScale) + (Padding * 2);
        var height = (int)MathF.Ceiling(boundsHeight * fitScale) + (Padding * 2);
        return new FittedFrame(
            width,
            height,
            Padding - (boundsX * fitScale),
            Padding - (boundsY * fitScale),
            fitScale);
    }

    /// <summary>
    /// The frame used when a bounds read came back degenerate: a <see cref="DefaultSceneSize"/> square with the
    /// node parked at the padding origin and no fit scale. Matches what the raster clip lane falls back to.
    /// </summary>
    internal static FittedFrame Fallback()
        => new(DefaultSceneSize, DefaultSceneSize, Padding, Padding, 1f);

    /// <summary>
    /// A canvas plus the node-LOCAL rect it occupies. <c>Local*</c> are in the node's own units (skeleton-local
    /// for a spine rig); <c>Canvas*</c> are the pixels the bake actually produced.
    /// </summary>
    internal readonly record struct FitPlacement(
        int CanvasWidth,
        int CanvasHeight,
        double LocalX,
        double LocalY,
        double LocalWidth,
        double LocalHeight,
        double FitScale);

    /// <summary>
    /// Invert a fitted frame into the node-local placement rect. <paramref name="nodeScaleX"/> at or below zero
    /// is treated as 1 — a zero fit scale would put the clip at infinity, and visibly wrong beats invisible.
    ///
    /// <para>Computed in <c>float</c> and widened, NOT recomputed in <c>double</c>: the raster clip lane's
    /// <c>clipPlacement</c> has always been float arithmetic widened into a double field, and the whole value of
    /// sharing this function is that the geoclip's <c>meta.placement</c> and the raster clip's
    /// <c>clipPlacement</c> compare EQUAL for the same rig rather than merely close.</para>
    /// </summary>
    internal static FitPlacement Place(
        int viewportWidth,
        int viewportHeight,
        float nodePositionX,
        float nodePositionY,
        float nodeScaleX)
    {
        var fitScale = nodeScaleX <= 0f ? 1f : nodeScaleX;
        return new FitPlacement(
            viewportWidth,
            viewportHeight,
            -nodePositionX / fitScale,
            -nodePositionY / fitScale,
            viewportWidth / fitScale,
            viewportHeight / fitScale,
            fitScale);
    }

    internal static FitPlacement Place(FittedFrame frame)
        => Place(frame.ViewportWidth, frame.ViewportHeight, frame.NodePositionX, frame.NodePositionY, frame.NodeScale);
}
