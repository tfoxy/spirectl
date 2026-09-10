using System;
using Spirectl.Sts2.Core.SceneInspection;

namespace Spirectl.Sts2.Live;

// R14 producer ENEMY-INTENT BOB FOLD — PURE and Godot-free (like Sts2TopBarFold / Sts2ProceedGlow /
// Sts2MapPointPulse) so the math is unit-testable without the Godot-coupled Sts2RuntimeSceneWatcher.
//
// WHY. A visually IDLE combat (the player thinking, nothing animating that a human would call motion) still streams
// ~30 deltas/s per enemy, because the intent holder's POSITION is rewritten every single frame, forever, for every
// enemy on screen. Together with the orb spin (Sts2OrbSpinFold), the intent flipbook
// (Sts2IntentGlyphFold) and the end-turn glow (Sts2EndTurnGlowFold) this is the whole reason an idle combat wire
// never goes quiet — measured at 898 messages / 30s with a maximum gap of 91ms, i.e. not one 200ms window of
// silence in half a minute. Downstream that starves the mirror client's idle DOM hatchery (needs 200ms of wire
// quiet) and stops a headless instance from ever reaching its idle MaxFps (needs 1000ms).
//
// ON SCREEN. The enemy's intent icon hovers above it and bobs gently up and down forever: a pure sine of WALL-CLOCK
// time (not an accumulator), 10px of amplitude about a baseline 8px above the holder's resting spot, period 2000ms,
// each enemy's intent phased differently so a row of enemies does not bob in lockstep. It is straight-up-and-down
// only — the horizontal placement never moves.
//
// WHY THE REST POSITION IS ANALYTIC (0,0), NOT A SAMPLE. The bob is the ONLY writer of the holder's position, and
// it assigns the WHOLE vector, so x is 0 on every frame it has ever run and the authored `pivot_offset`/anchors
// carry the holder's real placement. The client's replay (`animAttributes.ts`'s `bob`
// binding, keyed on the scene-relative path `…/IntentHolder`) emits the FULL game offset as a CSS `translate`
// spanning `-(base+amp) = -18px` … `amp-base = +2px`, so the two compose to the game's exact position ONLY if the
// producer hands the client the holder at Position (0,0). A first-seen sample would be an arbitrary point of the
// sine and would double-count as a constant offset forever.
//
// This is also a BUG FIX for a non-headless host. The client bob is unconditional (a static path table — see
// `nodeAnimBinding`), so before this fold a host still ticking the bob streamed it AND the browser replayed it:
// the intent bobbed at roughly double amplitude. A headless host (whose per-frame work is frozen by
// CouchCoopHeadlessVisualSuspender) instead stranded the holder at whatever phase the freeze caught, adding a
// constant <=18px offset. Pinning to the analytic rest makes both hosts render the game's exact motion.
//
// NO GATE, deliberately. Unlike the top-bar rock or the proceed glow — which STOP, and whose non-loop states carry
// meaning — the bob has no off state: it is either ticking (and the fold removes exactly its contribution) or
// frozen (and the fold removes exactly the stranded contribution). Both cases want the same analytic rest, and the
// client replays unconditionally either way, so a gate could only introduce disagreement.
//
// LOCAL-TRANSFORM MODE ONLY (see the watcher's guard). Unlike the pivot-anchored rotation/scale undos, a POSITION
// undo is a LEFT-multiplication by `T(rest - pos)` in the node's PARENT space, so it is only a plain
// origin-subtraction while the streamed transform is the node's parent-relative local. In global mode the shift
// would have to be lifted through the parent's basis, and the client's replay would double-apply anyway (the
// holder's DOM children are NESTED inside its element and ride its CSS translate — which reproduces the game only
// when they stream as parent-relative locals). The fold is therefore a no-op in global mode, byte-identical to
// today, exactly like Sts2MapPointPulse and Sts2TopBarFold.
//
// KNOWN COST, accepted (kill switches: SPIRECTL_INTENT_BOB_FOLD=0, or the master SPIRECTL_DECOR_EMIT_SUPPRESS=0):
// a client that does NOT reproduce the bob renders enemy intents STILL, at the holder's resting placement (which
// is where the game's own sine spends most of its time, and is correctly placed — nothing is frozen mid-layout).
// Its hook point is the same path-keyed decorative-animation table the orb spin already uses.
internal static class Sts2IntentBobFold
{
    /// <summary>The instanced scene of one enemy intent.</summary>
    internal const string SceneFile = "res://scenes/combat/intent.tscn";

    /// <summary>
    /// Scene-relative path of the bobbing container. The co-op player intents live in a DIFFERENT scene
    /// (`multiplayer_player_intent.tscn`) and so are never matched.
    /// </summary>
    internal const string HolderRelPath = "IntentHolder";

    /// <summary>The analytic resting position: the bob writes the whole vector, so rest is the origin.</summary>
    internal const double RestPositionX = 0.0;
    internal const double RestPositionY = 0.0;

    // The bob's own constants, single-sourced here so the client-side replay can be checked against ONE place:
    // the holder sits `Sin(t + phase) * AmplitudePx + BaselineUpPx` pixels ABOVE its resting origin.
    internal const double AmplitudePx = 10.0;
    internal const double BaselineUpPx = 8.0;

    /// <summary>2000ms — the argument advances by π per 1000ms, so a full sine takes 2π rad = 2s.</summary>
    internal const double PeriodMs = 2000.0;

    /// <summary>Positions within this of rest need no pin (the already-at-rest case).</summary>
    private const double PositionEpsilon = 1e-6;

    /// <summary>True when this scene identity is the bobbing enemy-intent holder.</summary>
    internal static bool IsHolder(string? sceneFilePath, string? relPath)
        => string.Equals(sceneFilePath, SceneFile, StringComparison.Ordinal)
           && string.Equals(relPath, HolderRelPath, StringComparison.Ordinal);

    /// <summary>
    /// Subtract the bob's POSITION back out of <paramref name="live"/>, leaving the node's rotation, scale and skew
    /// exactly as streamed. Returns <paramref name="live"/> itself (no allocation) when there is nothing to undo.
    /// </summary>
    /// <param name="live">The node's LOCAL transform as read this tick (see the mode guard above).</param>
    /// <param name="positionX">`Control.Position.X` read LIVE this tick — never a stored sample.</param>
    /// <param name="positionY">`Control.Position.Y`.</param>
    /// <remarks>
    /// A Godot Control's transform is `M(pos) = T(pos + p) · R · S · T(-p)` (p = PivotOffset), and `pos` enters ONLY
    /// through the leading translation, so
    ///     M(rest) = T(rest - pos) · M(pos)
    /// and a left-multiplication by a pure translation is just an addition on the origin — the basis is untouched,
    /// which is what keeps a hover scale / rotation / skew streaming live. (Being a LEFT-multiplication in the
    /// node's parent space is also exactly why this one needs the local-mode guard: `A · T(d) · M ≠ T(d) · A · M`
    /// unless A is a pure translation.)
    /// </remarks>
    internal static RuntimeSceneTransform2DSnapshot? PinRestPosition(
        RuntimeSceneTransform2DSnapshot? live,
        double positionX,
        double positionY)
    {
        if (live is null || !double.IsFinite(positionX) || !double.IsFinite(positionY))
        {
            return live;
        }

        var dx = RestPositionX - positionX;
        var dy = RestPositionY - positionY;
        if (Math.Abs(dx) <= PositionEpsilon && Math.Abs(dy) <= PositionEpsilon)
        {
            return live;
        }

        return new RuntimeSceneTransform2DSnapshot(
            live.XAxis,
            live.YAxis,
            new RuntimeSceneVector2Snapshot(live.Origin.X + dx, live.Origin.Y + dy));
    }
}
