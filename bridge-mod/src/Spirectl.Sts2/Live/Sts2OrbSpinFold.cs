using System;
using System.Collections.Generic;
using Spirectl.Sts2.Core.SceneInspection;

namespace Spirectl.Sts2.Live;

// R14 producer ENERGY/STAR ORB SPIN FOLD — PURE and Godot-free (like Sts2TopBarFold / Sts2IntentBobFold) so the
// scene table and the rest pin are unit-testable without the Godot-coupled Sts2RuntimeSceneWatcher.
//
// WHY. The combat energy counter (and, on the characters that have one, the star counter) spins the children of its
// `%RotationLayers` container EVERY frame, forever, whether or not anything in the fight is happening. Two layers
// per counter × one counter per seat is a steady per-frame transform stream that keeps a visually IDLE combat wire
// awake — see Sts2IntentBobFold for the measured idle baseline this fold is one of four parts of.
//
// ON SCREEN. Each counter is a stack of concentric decorative rings that turn forever at constant speed, the outer
// ring faster than the inner one (the rate scales with the layer's index). The spin is pure garnish — nothing about
// the fight changes it — and it never stops. Both counter families animate their rings the same way.
//
// WHY THE REST ROTATION IS ANALYTIC (0), NOT A SAMPLE. The angle is an UNBOUNDED accumulator, so a first-seen
// sample is an arbitrary angle that would be frozen forever — the same class of bug WS-D hit on the top bar. 0 is
// the layers' AUTHORED rotation in every counter scene (none of the six `.tscn` files sets `rotation` on a
// RotationLayers child) and the only value the node has ever had that is not an accumulation, so the client replay
// — a constant-speed `rotate` keyed by the same scene-relative path (`animAttributes.ts`) — can compose from it
// exactly. The layers are authored pivot-CENTRED (`pivot_offset = (64, 64)` on a 128x128 box in all six scenes),
// which is what lets the client spin its self-layer about the element's default centre origin.
//
// NO GATE, deliberately — same reasoning as Sts2IntentBobFold: the loop has no meaningful off state (it is running
// unless the counter is frozen game-side, in which case the stranded angle is exactly what wants removing), and the
// client's replay is unconditional. NOTE the one thing the fold does NOT reproduce, unchanged from before it: the
// rings slow to a sixth of their speed (30 -> 5 deg/s) while the counter reads zero, and the client replay has
// always used a single rate. That discrepancy predates this fold and is unaffected by it.
//
// LOCAL-TRANSFORM MODE ONLY (the watcher's guard), for uniformity with the other transform folds. The pin itself is
// a RIGHT-multiplication (see Sts2TopBarFold.PinRestRotation) so it would be correct in global space too, and the
// layers are leaves with no descendants to double-apply to — but a global-mode client has no replay either, so the
// no-op there is byte-identical to today and the house rule stays "transform folds are local-mode only".
//
// KNOWN COST, accepted (kill switches: SPIRECTL_ORB_SPIN_FOLD=0, or the master SPIRECTL_DECOR_EMIT_SUPPRESS=0): a
// client that does not reproduce the spin renders the orb layers at their authored angle instead of spinning.
internal static class Sts2OrbSpinFold
{
    /// <summary>The star counter's scene (its layers hang off `Icon/RotationLayers`).</summary>
    internal const string StarCounterScene = "res://scenes/combat/energy_counters/star_counter.tscn";

    /// <summary>
    /// Scene file → the scene-relative paths of that counter's spinning layers. An EXPLICIT table rather than a
    /// `*/RotationLayers/*` heuristic, for the same reason Sts2DecorEmitSuppress is explicit — and because the two
    /// counter families NEST and NAME those children differently (`Layers/RotationLayers/{Layer2,Layer3}` for the
    /// energy counters, `Icon/RotationLayers/{Layer1,Layer2}` for the star counter), which is the exact ambiguity
    /// that once made the client spin the star counter's second layer at the first layer's rate. Necrobinder has a
    /// SINGLE rotation layer (its `Layers/Layer3` is a plain sibling of RotationLayers and must not be listed).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> LayerPathsByScene =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["res://scenes/combat/energy_counters/ironclad_energy_counter.tscn"] =
                ["Layers/RotationLayers/Layer2", "Layers/RotationLayers/Layer3"],
            ["res://scenes/combat/energy_counters/silent_energy_counter.tscn"] =
                ["Layers/RotationLayers/Layer2", "Layers/RotationLayers/Layer3"],
            ["res://scenes/combat/energy_counters/defect_energy_counter.tscn"] =
                ["Layers/RotationLayers/Layer2", "Layers/RotationLayers/Layer3"],
            ["res://scenes/combat/energy_counters/regent_energy_counter.tscn"] =
                ["Layers/RotationLayers/Layer2", "Layers/RotationLayers/Layer3"],
            ["res://scenes/combat/energy_counters/necrobinder_energy_counter.tscn"] =
                ["Layers/RotationLayers/Layer2"],
            [StarCounterScene] = ["Icon/RotationLayers/Layer1", "Icon/RotationLayers/Layer2"],
        };

    /// <summary>The analytic resting rotation — the layers' authored angle in all six counter scenes.</summary>
    internal const double RestRotationRad = Sts2TopBarFold.RestRotationRad;

    // The spin's own constants, single-sourced here.
    /// <summary>Degrees per second for the innermost ring, scaled by (layer index + 1); 5 while the counter reads zero.</summary>
    internal const double SpinDegreesPerSec = 30.0;
    internal const double SpinDegreesPerSecAtZero = 5.0;

    /// <summary>One full turn of the FIRST (innermost) layer, in ms: 360 / 30 deg-per-sec.</summary>
    internal const double FirstLayerTurnMs = 360.0 / SpinDegreesPerSec * 1000.0;

    /// <summary>True when this scene identity is one of the spinning orb layers.</summary>
    internal static bool IsSpinLayer(string? sceneFilePath, string? relPath)
    {
        if (sceneFilePath is null || relPath is null || !LayerPathsByScene.TryGetValue(sceneFilePath, out var paths))
        {
            return false;
        }

        for (var i = 0; i < paths.Count; i++)
        {
            if (string.Equals(paths[i], relPath, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Divide the spin's ROTATION back out of <paramref name="live"/>, leaving position/scale exactly as streamed.
    /// The algebra is IDENTICAL to the top-bar icon fold's (a pivot-anchored right-multiplication by `R(-θ)` about
    /// the same analytic rest of 0), so it is single-sourced there rather than copied — a drift between the two
    /// would be a silent geometry bug in whichever copy went stale.
    /// </summary>
    /// <param name="live">The transform as read this tick.</param>
    /// <param name="rotationRad">`Control.Rotation` read LIVE this tick — never a stored sample (see above).</param>
    /// <param name="pivotX">`Control.PivotOffset.X` (64 in every counter scene).</param>
    /// <param name="pivotY">`Control.PivotOffset.Y` (64 in every counter scene).</param>
    internal static RuntimeSceneTransform2DSnapshot? PinRestRotation(
        RuntimeSceneTransform2DSnapshot? live,
        double rotationRad,
        double pivotX,
        double pivotY)
        => Sts2TopBarFold.PinRestRotation(live, rotationRad, pivotX, pivotY);
}
