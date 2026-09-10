namespace Spirectl.Sts2.Live;

/// <summary>
/// Godot-free EVENT-BACKDROP FRAMING: where an event background scene
/// (<c>res://scenes/events/background_scenes/*.tscn</c>) sits inside a capture viewport.
///
/// <para>WHY THIS IS ITS OWN FILE (the <see cref="Sts2SceneFitFrame"/> precedent): the arithmetic used to live
/// inline in <c>Sts2AssetExtractProvider.TryResolveEventBackgroundFrame</c>, which is live-host-only, so it could
/// not be unit-tested without the game — and it now has TWO consumers with different framing needs that must not
/// drift: the recon-still lane renders at the live root viewport with the game's own aspect lerp
/// (<see cref="Resolve"/>), while an explicit-size render (the couch-coop static-background policy, 2520x1080)
/// needs the MIRROR's composition (<see cref="ResolveMirrorCentered"/>).</para>
///
/// <para>THE MIRROR COMPOSITION, stated once. The web mirror streams the game's 16:9 (1920x1080) layout and,
/// on a widened stage, re-centers the whole backdrop subtree rigidly by half the widened margin (the
/// event-background spread branch). So the still that replaces that subtree must be the REFERENCE-aspect frame
/// translated to the requested viewport's center — NOT the frame this arithmetic would produce for the wide
/// aspect itself (at 21:9 the game re-lerps to scale 1.0 / position 330, which the mirror never shows).</para>
/// </summary>
internal static class Sts2EventBackgroundFrameMath
{
    internal const float ReferenceWidth = 1920f;
    internal const float ReferenceHeight = 1080f;

    /// <summary>Uniform scale + position of the backdrop root inside the capture viewport (float on purpose —
    /// the extract provider has always run this math in float, and staying float keeps the refactor
    /// byte-identical for the default path).</summary>
    internal readonly record struct EventFrame(float PositionX, float PositionY, float Scale);

    /// <summary>
    /// The backdrop's aspect-dependent placement, computed exactly as the live scene places it (same operations,
    /// same order, same float width): a two-segment lerp over the clamped aspect ratio, then a horizontal
    /// re-center for the scale shrink.
    /// </summary>
    internal static EventFrame Resolve(float viewportWidth, float viewportHeight)
    {
        var ratio = Math.Clamp(viewportWidth / viewportHeight, 1.3333f, 2.3333f);
        float positionX;
        float positionY;
        float scale;
        if (ratio < 1.7777f)
        {
            var weight = InverseLerp(1.3333f, 1.7777f, ratio);
            positionX = Lerp(-140f, 0f, weight);
            positionY = Lerp(110f, 40f, weight);
            scale = Lerp(1f, 0.89f, weight);
        }
        else
        {
            var weight = InverseLerp(1.7777f, 2.3333f, ratio);
            positionX = Lerp(0f, 330f, weight);
            positionY = Lerp(40f, 40f, weight);
            scale = Lerp(0.89f, 1f, weight);
        }

        positionX += (viewportWidth * 0.5f) * (1f - scale);
        return new EventFrame(positionX, positionY, scale);
    }

    /// <summary>
    /// The frame an explicit-size still must use to match the web mirror: the 16:9 reference frame, translated
    /// by half the viewport growth on each axis. At 2520x1080 that is scale 0.89 at (405.6, 40) — the mirror's
    /// rigid ½Δ re-center of the game's 16:9 layout — where <see cref="Resolve"/> would give the game's own
    /// 21:9 answer (scale 1.0 at (330, 40)), a picture the mirror never composes.
    /// </summary>
    internal static EventFrame ResolveMirrorCentered(float viewportWidth, float viewportHeight)
        => CenterFrame(Resolve(ReferenceWidth, ReferenceHeight), viewportWidth, viewportHeight);

    /// <summary>The ½Δ re-center of a 1080-design frame into the requested capture viewport.</summary>
    internal static EventFrame CenterFrame(EventFrame frame, float viewportWidth, float viewportHeight)
        => new(
            frame.PositionX + ((viewportWidth - ReferenceWidth) * 0.5f),
            frame.PositionY + ((viewportHeight - ReferenceHeight) * 0.5f),
            frame.Scale);

    /// <summary>
    /// Parse an <c>EventBackgroundFrame</c> override — <c>"x,y,scale"</c>, invariant-culture floats in
    /// 1080-design units, the LIVE backdrop container transform the caller probed. The lerp above predicts the
    /// game's placement; a probe MEASURES it, and the shipped game has already drifted from the recovered
    /// constants (Neow's container sits at y 99.4 where the lerp says 40), so a caller that has the live answer
    /// sends it. Null on any malformation — the render then falls back to the reference lerp rather than
    /// guessing at a half-parsed frame.
    /// </summary>
    internal static EventFrame? TryParseFrameSpec(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return null;
        }

        var parts = spec.Split(',');
        if (parts.Length != 3
            || !float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scale)
            || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(scale)
            || scale <= 0f)
        {
            return null;
        }

        return new EventFrame(x, y, scale);
    }

    // Godot's Mathf.InverseLerp / Mathf.Lerp, reproduced so this file compiles without the engine while keeping
    // bit-identical float results (both are the canonical one-liners).
    private static float InverseLerp(float from, float to, float value) => (value - from) / (to - from);

    private static float Lerp(float from, float to, float weight) => from + ((to - from) * weight);
}
