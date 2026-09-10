using System;

namespace Spirectl.Sts2.Core.SceneInspection;

/// <summary>
/// Pure fit-scale math for mapping a Godot <c>SubViewport</c>'s local pixel space into the on-screen rect of
/// the node that DISPLAYS it via a <c>ViewportTexture</c>.
///
/// STS2 renders some 2D content (e.g. the multiplayer card-intent preview) into an offscreen SubViewport and
/// shows it on a consumer node positioned in the main canvas. A node INSIDE a SubViewport reports a
/// viewport-LOCAL global transform, so the live mirror (which places by global transform) would paint it at
/// the viewport origin (0,0) instead of at the consumer's on-screen frame. The producer fixes this by baking a
/// "viewport→screen" prefix into the streamed transform; this class computes the prefix's SCALE + OFFSET from
/// the consumer display rect, the viewport size, and the consumer's texture stretch — reproducing how the
/// consumer's <c>TextureRect</c>/<c>Sprite2D</c> fits the viewport texture into its box.
///
/// Godot-free (operates on doubles) so it lives in Core and is unit-testable without the live host — mirrors
/// <see cref="Spirectl.Sts2.Core.Map.Sts2MapDrawingTransform"/>.
/// </summary>
public static class Sts2ViewportPrefixTransform
{
    /// <summary>An affine (scale + translate) mapping viewport-local pixels into the consumer's display rect.</summary>
    public readonly record struct Fit(double ScaleX, double ScaleY, double OffsetX, double OffsetY);

    /// <summary>
    /// Map a SubViewport (<paramref name="vpW"/> x <paramref name="vpH"/>) into a consumer display rect
    /// (<paramref name="rectW"/> x <paramref name="rectH"/>). When <paramref name="keepAspect"/> the texture is
    /// uniformly min-fit and centered (TextureRect KEEP_ASPECT* — the card intents use KEEP_ASPECT_CENTERED);
    /// otherwise it fills the rect anisotropically (Scale/Tile, or a Sprite2D showing the texture 1:1 in its own
    /// box — its own scale rides the consumer transform). Returns null for a degenerate viewport/rect so the
    /// caller falls back to NO prefix (current behavior, never worse).
    /// </summary>
    public static Fit? Compute(double rectW, double rectH, double vpW, double vpH, bool keepAspect)
    {
        if (rectW <= 0 || rectH <= 0 || vpW <= 0 || vpH <= 0)
        {
            return null;
        }

        if (keepAspect)
        {
            var s = Math.Min(rectW / vpW, rectH / vpH);
            return new Fit(s, s, (rectW - vpW * s) / 2.0, (rectH - vpH * s) / 2.0);
        }

        return new Fit(rectW / vpW, rectH / vpH, 0.0, 0.0);
    }

    /// <summary>
    /// True when a Godot <c>TextureRect.StretchMode</c> (enum 0..6) preserves aspect ratio: KEEP_ASPECT(4),
    /// KEEP_ASPECT_CENTERED(5), KEEP_ASPECT_COVERED(6). Scale(0)/Tile(1)/Keep(2)/KeepCentered(3) → fill path.
    /// </summary>
    public static bool StretchKeepsAspect(int stretchMode) => stretchMode >= 4;

    /// <summary>
    /// Map a SubViewport into the on-screen rect of its parent <c>SubViewportContainer</c>. Unlike the
    /// <see cref="Compute"/> path there is no separate consumer node: a SubViewportContainer DRAWS its child
    /// viewport's texture itself, at its OWN control-local origin (0,0) — so the fit is a pure scale with no
    /// letterbox offset, and the caller composes it after the container's
    /// <c>GetGlobalTransformWithCanvas()</c>.
    ///
    /// Godot 4 <c>SubViewportContainer::_notification(NOTIFICATION_DRAW)</c> semantics, which this reproduces:
    /// <list type="bullet">
    /// <item><description><paramref name="stretch"/> = false (the STS2 default; both timeline epoch scenes) — the
    /// container draws the texture at the viewport's OWN size, <c>Rect2(Vector2(), c-&gt;get_size())</c>. The
    /// container's size does not enter: the texture is 1:1 from the container origin (and simply overflows or
    /// under-fills a mismatched container). Fit = identity scale.</description></item>
    /// <item><description><paramref name="stretch"/> = true — the container draws the texture across its FULL box,
    /// <c>Rect2(Vector2(), get_size())</c>, and it has already resized the child viewport to
    /// <c>get_size() / stretch_shrink</c>. So the drawn magnification is exactly
    /// <c>containerSize / viewportSize</c>, which equals <paramref name="stretchShrink"/> once Godot's resize
    /// notification has been applied. We use the MEASURED ratio rather than the shrink value because the ratio is
    /// what the draw call actually does — it stays correct in the window before/around a resize, where the two
    /// disagree.</description></item>
    /// </list>
    ///
    /// The viewport's <c>size_2d_override</c> (supersampling — also what <c>stretch</c> configures) is NOT handled
    /// here: the caller composes the same <c>size / size_2d_override</c> factor it already applies to the
    /// Control/Sprite2D path, which makes the net content→screen scale correct under either Godot convention for
    /// what a stretching container overrides.
    ///
    /// Returns null for a degenerate container/viewport size, or for an invalid <paramref name="stretchShrink"/>
    /// (&lt; 1, which Godot itself forbids) while stretching — the caller then falls back to NO prefix, which is
    /// today's behaviour, and the prune's SubViewportContainer exclusion keeps such content streaming as before.
    /// </summary>
    public static Fit? ComputeContainerFit(
        double containerW,
        double containerH,
        double vpW,
        double vpH,
        bool stretch,
        int stretchShrink)
    {
        if (containerW <= 0 || containerH <= 0 || vpW <= 0 || vpH <= 0)
        {
            return null;
        }

        if (!stretch)
        {
            return new Fit(1.0, 1.0, 0.0, 0.0);
        }

        if (stretchShrink < 1)
        {
            return null; // ambiguous configuration → no prefix (never worse than today)
        }

        return new Fit(containerW / vpW, containerH / vpH, 0.0, 0.0);
    }
}
