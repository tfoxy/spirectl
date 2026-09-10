namespace Spirectl.Sts2.Core.Map;

/// <summary>
/// Converts player map-drawing stroke points between the game's net-normalized stroke space
/// (<c>SerializableMapDrawingLine.mapPoints</c>, produced by <c>NMapDrawings.ToNetPosition</c>)
/// and "TheMap content space" — the coordinate space the presentation renderer / map NODES use.
///
/// The game normalizes each stored Line2D point with
/// <c>ToNetPosition(p) = ((p.x - Size.X*0.5)/960, p.y/Size.Y)</c> and reverses it with
/// <c>FromNetPosition(p) = (p.x*960 + Size.X*0.5, p.y*Size.Y)</c>. We invert the normalization
/// with the LIVE <c>NMapDrawings.Size</c>, then apply a small CONTENT affine (Scale/Offset) that
/// folds in the SubViewport + 0.5x runtime scale + the Drawings node offset. Those four constants
/// are CALIBRATED against a live draw (see the map-drawing live calibration gate); the round-trip
/// is exact for any choice, so read (<see cref="NetToContent"/>) and write
/// (<see cref="ContentToNet"/>) can never drift.
///
/// Godot-free (operates on doubles) so it lives in Core and is unit-testable without the live host.
/// </summary>
public static class Sts2MapDrawingTransform
{
    // Net-normalization width baked into the game (NMapDrawings.ToNetPosition divides X by 960).
    private const double NetWidth = 960.0;

    // CONTENT affine: NMapDrawings Line2D-point space (after FromNetPosition — i.e. HALF the local
    // draw position, since the game stores AddPoint(pos*0.5)) -> TheMap content space (mapNodeX/Y).
    // CALIBRATED analytically against the live game's REAL node grid (basic-map), NOT screen pixels:
    // screen position is confounded by the map's display zoom + scroll, but the net<->content affine
    // is zoom/scroll-independent (both spaces are pinned to the map content), so we fit it to ground
    // truth instead. Method: read every NMapNode's globalPosition via scene introspection, subtract the
    // live Drawings-node globalPosition to get each node's NMapDrawings-LOCAL center, halve it to reach
    // Line2D-point space, cluster into the 7-column x 16-row grid, and least-squares fit
    //   mapNodeX(col) = ScaleX*(localX) + OffsetX ,  mapNodeY(row) = ScaleY*(localY) + OffsetY .
    // (Drawings node measured Size=(1920,3240), so ToNetPosition divides X by 960 and Y by 3240.)
    // Fit residual ~10-12px mean: the IRREDUCIBLE mismatch between our synthetic node grid (even 127px
    // rows via mapNodeY) and the game's real, slightly-staggered ~152px-local layout — no single affine
    // removes it, and it is well within a 56px node. Read (NetToContent) and write (ContentToNet/
    // ContentToLocal) MUST share these constants so a stroke drawn at content C reads back as C.
    public const double ContentScaleX = 1.7033;
    public const double ContentScaleY = 1.6325;
    public const double ContentOffsetX = 160.1;
    public const double ContentOffsetY = -281.3;

    /// <summary>net-normalized stroke point -> TheMap content space (uses the live NMapDrawings size).</summary>
    public static (double X, double Y) NetToContent(double netX, double netY, double sizeX, double sizeY)
    {
        // Inverse of ToNetPosition (the game's FromNetPosition), with the live Size.
        var localX = netX * NetWidth + sizeX * 0.5;
        var localY = netY * sizeY;
        return (localX * ContentScaleX + ContentOffsetX, localY * ContentScaleY + ContentOffsetY);
    }

    /// <summary>TheMap content-space point -> net-normalized stroke point (exact inverse of <see cref="NetToContent"/>).</summary>
    public static (double X, double Y) ContentToNet(double contentX, double contentY, double sizeX, double sizeY)
    {
        var localX = (contentX - ContentOffsetX) / ContentScaleX;
        var localY = (contentY - ContentOffsetY) / ContentScaleY;
        var netX = (localX - sizeX * 0.5) / NetWidth;
        var netY = sizeY == 0.0 ? 0.0 : localY / sizeY;
        return (netX, netY);
    }

    /// <summary>
    /// TheMap content-space point -> the NMapDrawings-local position to pass to
    /// <c>NMapDrawings.BeginLineLocal</c>/<c>UpdateCurrentLinePositionLocal</c>. The game stores each
    /// point HALVED (<c>AddPoint(pos * 0.5)</c>), and the serialized stroke is
    /// <c>ToNetPosition(pos * 0.5)</c> — so the stored Line2D point equals the affine's "local". We
    /// therefore undo the content affine to recover that local point, then pre-multiply by 2 so the
    /// game's internal *0.5 lands it back on the local point. A stroke drawn at content C this way
    /// reads back through <see cref="NetToContent"/> as C. Size-independent: the content↔local affine
    /// lives in post-FromNetPosition (Line2D-point) space; only the net normalization uses Size.
    /// </summary>
    public static (double X, double Y) ContentToLocal(double contentX, double contentY)
    {
        var localX = (contentX - ContentOffsetX) / ContentScaleX;
        var localY = (contentY - ContentOffsetY) / ContentScaleY;
        return (localX * 2.0, localY * 2.0);
    }
}
