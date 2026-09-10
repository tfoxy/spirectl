namespace Spirectl.Sts2.Live;

// ABSOLUTE SCROLL OFFSET — the Godot-free half of the `set-scroll-offset` semantic action.
//
// WHY THE ACTION EXISTS. Every other way to scroll STS2 from outside is RELATIVE: a wheel tick is worth a fixed
// number of px, a drag is a stream of motion deltas the screen adds to its own target. A remote client that wants
// the game to end up at a SPECIFIC offset therefore has to guess how many quantised ticks that is, cannot express
// a sub-tick remainder at all, and has no way to learn what the game actually settled at. `set-scroll-offset`
// closes that: the caller names the scroll container by its live node instance id and the offset it wants, the
// host clamps to the surface's OWN limits, and the clamped value comes back in the action result so the caller can
// reconcile when the game refused the full travel.
//
// This file holds only the parts that need no Godot: the kill switch, the offset parse, and the clamp. The live
// resolve/apply (which surface owns the node, and writing that surface's scroll target) is
// Sts2ActionHandler.Scroll.cs, inside the live-host glob.
public enum ScrollSurfaceKind
{
    None,
    Map,
    Grid,
}

public static class Sts2ScrollOffsetMath
{
    // The request argument carrying the wanted container-local Y, and the two keys the result answers with.
    public const string OffsetArgumentKey = "offsetY";
    public const string ResultOffsetKey = "offsetY";
    public const string ResultRequestedKey = "requestedY";
    public const string ResultSurfaceKey = "surface";

    // The map screen's own scroll window. These are compile-time constants inside the game, so they are read
    // reflectively where possible (see Sts2ActionHandler.Scroll.cs) and fall back to these literals — which are the
    // same pair the browser mirror already carries client-side, so a drift would show up as a clamp disagreement
    // rather than as silence.
    public const double DefaultMapLimitLo = -600d;
    public const double DefaultMapLimitHi = 1800d;

    // SPIRECTL_SCROLL_OFFSET_ACTION: escape hatch for the whole action. Default ON; `0`/`false`/`off`/`no` makes
    // every request fail with NotEnabled, which is what a caller needs to fall back to its own relative replay.
    // Read once, in the established switch style.
    private static readonly bool EnabledByEnvironment =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SCROLL_OFFSET_ACTION") ?? string.Empty)
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    public static bool Enabled => EnabledByEnvironment;

    // The wanted offset, as the browser spells it: a plain invariant-culture number. Anything else (empty, a word,
    // an infinity, a NaN) is refused rather than coerced — a scroll target is a position, and a bad one would park
    // the surface somewhere nobody asked for.
    public static bool TryParseOffset(string? raw, out double value)
    {
        value = 0d;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!double.TryParse(
                raw.Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed))
        {
            return false;
        }

        if (double.IsNaN(parsed) || double.IsInfinity(parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    // Clamp into a window whose two ends arrive in EITHER order: a scroll window's "bottom" is the more negative
    // end for a card grid and the more positive one for the map, and the caller should not have to know which.
    public static double Clamp(double value, double endA, double endB)
    {
        var lo = endA <= endB ? endA : endB;
        var hi = endA <= endB ? endB : endA;
        return value < lo ? lo : value > hi ? hi : value;
    }
}
