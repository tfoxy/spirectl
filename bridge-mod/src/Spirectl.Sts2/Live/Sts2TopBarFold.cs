using System;
using Spirectl.Sts2.Core.SceneInspection;

namespace Spirectl.Sts2.Live;

// R13 producer TOP-BAR ICON FOLD — PURE and Godot-free (like Sts2MapPointPulse / Sts2DecorEmitSuppress /
// Sts2RegionEmitCap) so the math + policy are unit-testable without the Godot-coupled Sts2RuntimeSceneWatcher.
//
// WHY THIS REPLACES WS-D's PIN. WS-D stopped the three top-bar icon animators from churning the wire by pinning the
// node's WHOLE first-emitted transform (`DecorRestTransform ??= first emitted`). That had two problems:
//
//   1. A REAL BUG. The first emission happens before Godot's DEFERRED Control layout has settled, so the "rest"
//      sample was a mid-layout pose — and it was then frozen FOREVER. The deck icon is CENTER-anchored, so its
//      position depends on the button's width, which changes as soon as `DeckCardCount` auto-sizes its label; the
//      settings icon is GROW_BOTH and sits at its (-32,-32) min-size clamp until the parent is sized. Both rendered
//      MISPLACED in the browser for the rest of the run. (The map icon is full-rect anchored at (0,0), which is why
//      only two of the three looked broken.) Pinning a whole transform can never be safe for a Control whose
//      placement is laid out later; pinning ONE analytic channel can.
//   2. It DELETED the animation. The icons stopped rocking/spinning while their screen was open and stopped tilting
//      on hover/press — the affordance that tells you which screen the top bar has open.
//
// THE FOLD. Same shape as the map-point pulse: the producer divides the churning channel (here: the icon's ROTATION)
// back out of the streamed transform and ships a declarative `RuntimeSceneNodeDelta.PinnedLoopAnim` token naming the
// loop, which the client replays on its own clock. Position and scale keep streaming untouched, so a late layout
// pass, a hover scale-up (1.1x) and any future move all reach the browser — the mispositioning bug ceases to exist
// structurally, not by a better-chosen rest sample.
//
// ON SCREEN. While one of the top-bar screens is open, that button's icon animates — and all three animate the
// SAME node (`Control/Icon`) under the SAME gate (the registered script variable `IsScreenOpen`), with three
// different motions:
//
//   * deck screen open — the icon ROCKS: +/-0.12 rad about a base that decays back to 0, period 1570.8ms, so any
//     leftover hover tilt eases out while it rocks.                        -> "topBarDeckRock"
//   * map screen open — the icon rocks the same +/-0.12 rad, but as an infinite looped tween: two 0.8s Sine/InOut
//     legs, period 1600ms.                                                 -> "topBarMapRock"
//   * settings screen open — the icon SPINS at a constant 1 rad/s, period 6283.2ms. Its scene file is
//     `top_bar_settings_button.tscn` while its script class is the pause button's, so every rule here keys off the
//     SCENE, never the class.                                              -> "topBarSpin"
//
// WHY THE REST ROTATION IS ANALYTIC (0), not a sample. Every path off the loop drives the icon back to exactly 0 —
// unhovering, closing the screen, and stopping the map oscillation all end at rotation 0, and the deck rock's own
// base decays to 0. So `RestRotationRad = 0` can never strand an icon mid-loop — unlike a first-seen sample, which
// for the settings spin (an unbounded accumulator) would have been an arbitrary angle.
//
// GATE = `IsScreenOpen` ONLY. When the screen is CLOSED nothing is pinned and nothing is named: the hover /
// press-down / unhover one-shots (0.25-1.0s each, and the only rotation writers in that state) stream live exactly
// as they do today. That is the whole "animated while you're pointing at it" behaviour, for free, with no client
// work — and it is also why the fold cannot swallow a real animation: it is inert unless a KNOWN infinite loop is
// provably running.
//
// KNOWN COST, accepted (kill switches: SPIRECTL_TOPBAR_FOLD=0, or the master SPIRECTL_DECOR_EMIT_SUPPRESS=0):
//   * A client that does NOT consume `PinnedLoopAnim` (today: the native Godot client) shows the icon still while
//     its screen is open. Its hook points are `MirrorNode.PinnedLoopAnim` + `CosmeticAnimator.BindingFor`, the same
//     pair Sts2MapPointPulse needs; until then the switch above restores the streamed rotation for that client.
//     Note this is still strictly better than WS-D for that client: the icon is at rest but correctly PLACED.
//   * The 0.5s `StopOscillation` spring-out and the deck rock's base decay are no longer streamed — an icon whose
//     screen closes snaps from its client-side loop to rest instead of easing there (the same trade the map-point
//     fold accepts). The hover/unhover one-shots, which are what the player actually looks at, are untouched.
internal static class Sts2TopBarFold
{
    /// <summary>The three instanced top-bar button scenes.</summary>
    internal const string DeckButtonScene = "res://scenes/ui/top_bar/top_bar_deck_button.tscn";
    internal const string MapButtonScene = "res://scenes/ui/top_bar/top_bar_map_button.tscn";

    /// <summary>
    /// The settings button's SCENE file. Its script class is the PAUSE button's (verified against a live
    /// keyframe), so every rule here keys off the scene path, never the class name.
    /// </summary>
    internal const string SettingsButtonScene = "res://scenes/ui/top_bar/top_bar_settings_button.tscn";

    /// <summary>Scene-relative path of the animated icon, shared by all three buttons.</summary>
    internal const string IconRelPath = "Control/Icon";

    /// <summary>
    /// The `PinnedLoopAnim` tokens shipped while each button's screen is open. The CLIENT owns the vocabulary
    /// (period, amplitude, easing) exactly like Sts2MapPointPulse.LoopAnimName — the producer only says WHICH loop
    /// it pinned, so the wire stays one short interned string. The three buttons share the scene-relative path
    /// `Control/Icon` while running three DIFFERENT animations, which is precisely why a path-keyed client-side
    /// binding could never tell them apart and the producer has to name the loop.
    /// </summary>
    internal const string DeckLoopAnimName = "topBarDeckRock";
    internal const string MapLoopAnimName = "topBarMapRock";
    internal const string SettingsLoopAnimName = "topBarSpin";

    /// <summary>The analytic resting rotation every non-loop path converges to.</summary>
    internal const double RestRotationRad = 0.0;

    // ---- the loops' own constants ---------------------------------------------------------------------------
    // Single-sourced here so a client-side replay can be checked against ONE place (the tests pin them, so a drift
    // between producer and client keyframes cannot go unnoticed).

    /// <summary>The deck rock: a sine of the given amplitude, its argument advancing at the given rate.</summary>
    internal const double DeckRockAmplitudeRad = 0.12;
    internal const double DeckRockRateRadPerSec = 4.0;
    internal const double DeckRockPeriodMs = 2000.0 * Math.PI / DeckRockRateRadPerSec;

    /// <summary>The map rock: two 0.8s Sine/InOut legs between -/+0.12 rad, looping forever.</summary>
    internal const double MapRockAmplitudeRad = 0.12;
    internal const double MapRockLegMs = 800.0;
    internal const double MapRockPeriodMs = 2.0 * MapRockLegMs;

    /// <summary>The settings spin: a constant 1 rad/s.</summary>
    internal const double SpinRateRadPerSec = 1.0;
    internal const double SpinPeriodMs = 2000.0 * Math.PI / SpinRateRadPerSec;

    /// <summary>Rotations within this of <see cref="RestRotationRad"/> need no pin (nothing to undo).</summary>
    private const double RotationEpsilon = 1e-9;

    private const double TwoPi = 2.0 * Math.PI;

    /// <summary>True when this scene identity is one of the three animated top-bar icons.</summary>
    internal static bool IsIcon(string? sceneFilePath, string? relPath)
        => string.Equals(relPath, IconRelPath, StringComparison.Ordinal)
           && LoopAnimFor(sceneFilePath) is not null;

    /// <summary>
    /// The loop token for a top-bar button SCENE, or null when the scene is not one of the three. Scene identity —
    /// not the script class — is the discriminator (see <see cref="SettingsButtonScene"/>).
    /// </summary>
    internal static string? LoopAnimFor(string? sceneFilePath) => sceneFilePath switch
    {
        DeckButtonScene => DeckLoopAnimName,
        MapButtonScene => MapLoopAnimName,
        SettingsButtonScene => SettingsLoopAnimName,
        _ => null,
    };

    /// <summary>
    /// True when this button's icon is looping RIGHT NOW, given the button's own live open-state. The predicate is
    /// one flag; WHICH member yields that flag is per-button — see
    /// <see cref="GateIsOpenMethod"/>.
    /// </summary>
    internal static bool IsAnimating(bool isScreenOpen) => isScreenOpen;

    /// <summary>
    /// True when this button's live gate must be read via its registered <c>IsOpen()</c> METHOD (Godot Call)
    /// instead of the <c>IsScreenOpen</c> script variable (Godot Get). Live-validated 2026-08-05: the variable is
    /// exact for the deck and settings buttons, but on the MAP button it is never updated and reads a dead false
    /// while the map is open — its rock is driven from the map screen itself — so only the method is truthful there.
    /// </summary>
    internal static bool GateIsOpenMethod(string? sceneFilePath)
        => string.Equals(sceneFilePath, MapButtonScene, StringComparison.Ordinal);

    /// <summary>
    /// Divide the icon's ROTATION back out of <paramref name="live"/>, leaving the node's position and scale exactly
    /// as streamed. Returns <paramref name="live"/> itself (no allocation) when there is nothing to undo — the
    /// resting steady state, i.e. every tick on which no screen is open.
    /// </summary>
    /// <param name="live">The transform as read this tick (local or global — see the remarks).</param>
    /// <param name="rotationRad">
    /// The Control's `Rotation` READ LIVE THIS TICK. It must come from the live node, never from a stored
    /// first-emitted basis: deriving θ from a remembered transform is exactly the WS-D bug (a pre-layout sample
    /// frozen forever).
    /// </param>
    /// <param name="pivotX">The Control's `PivotOffset.X` — Godot bakes the pivot into a Control's transform.</param>
    /// <param name="pivotY">The Control's `PivotOffset.Y`.</param>
    /// <remarks>
    /// A Godot Control's transform is `M(θ) = T(pos + p) · R(θ) · S(k) · T(-p)` (p = PivotOffset), so the rotation
    /// moves BOTH the basis and the origin (`p - R·S·p`) — dividing the basis alone would swing the icon off its
    /// pivot. The exact undo is a RIGHT-multiplication by the pivot-anchored inverse rotation:
    ///     C = M(θ)^-1 · M(0) = T(p) · R(-θ) · T(-p)
    /// (using `R(θ)·S(k)·R(-θ) = S(k)` for the icons' UNIFORM scale — 1.0 at rest, 1.1 while hovered), which is
    /// independent of `pos` and of `k`, so `live · C` restores the resting pose whatever the node's placement or
    /// hover state. Being a right-multiplication it works on a GLOBAL transform too (`(A·M(θ))·C = A·M(0)`), which
    /// is why the formula needs no separate global-mode branch — the rotation-only twin of
    /// <see cref="Sts2MapPointPulse.PinRestScale"/>.
    ///
    /// θ is reduced with <see cref="Math.IEEERemainder(double,double)"/> before the sin/cos. `R` is 2π-periodic so
    /// this changes nothing mathematically, but the settings spin is an UNBOUNDED accumulator (`+= delta` forever —
    /// thousands of radians into a long run), where the float precision of a raw Cos/Sin degrades badly.
    /// </remarks>
    internal static RuntimeSceneTransform2DSnapshot? PinRestRotation(
        RuntimeSceneTransform2DSnapshot? live,
        double rotationRad,
        double pivotX,
        double pivotY)
    {
        if (live is null || !double.IsFinite(rotationRad))
        {
            return live;
        }

        var theta = Math.IEEERemainder(rotationRad, TwoPi);
        if (Math.Abs(theta - RestRotationRad) <= RotationEpsilon)
        {
            return live;
        }

        // C = T(p) · R(-θ) · T(-p): basis = R(-θ) (Godot column convention: XAxis = (cos φ, sin φ),
        // YAxis = (-sin φ, cos φ) for φ = -θ), origin = p - R(-θ)·p.
        var cos = Math.Cos(theta);
        var sin = Math.Sin(theta);
        var cx = pivotX * (1.0 - cos) - pivotY * sin;
        var cy = pivotY * (1.0 - cos) + pivotX * sin;
        return new RuntimeSceneTransform2DSnapshot(
            // live.basis · C.XAxis, C.XAxis = (cos, -sin)
            new RuntimeSceneVector2Snapshot(
                live.XAxis.X * cos - live.YAxis.X * sin,
                live.XAxis.Y * cos - live.YAxis.Y * sin),
            // live.basis · C.YAxis, C.YAxis = (sin, cos)
            new RuntimeSceneVector2Snapshot(
                live.XAxis.X * sin + live.YAxis.X * cos,
                live.XAxis.Y * sin + live.YAxis.Y * cos),
            // live.basis · C.origin + live.origin
            new RuntimeSceneVector2Snapshot(
                live.Origin.X + live.XAxis.X * cx + live.YAxis.X * cy,
                live.Origin.Y + live.XAxis.Y * cx + live.YAxis.Y * cy));
    }
}
