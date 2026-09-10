using System;
using System.Collections.Generic;

namespace Spirectl.Sts2.Live;

// R15 producer CREATURE SPINE-ANCHOR FOLD — PURE and Godot-free (like Sts2OrbSpinFold / Sts2IntentBobFold) so the
// scene table, the class allowlist and the whole decision table are unit-testable without the Godot-coupled
// Sts2RuntimeSceneWatcher.
//
// WHY. After R14 silenced the orb spin, the intent bob/flip-book and the end-turn glow, a VISUALLY IDLE combat was
// still not a QUIET combat: a live 6.003s capture off the phone (four phantasmal gardeners, no input, nothing else
// moving) measured 52.81 msg/s, 83.29 KB/s and EIGHT node ids — all four `phantasmal_gardener.tscn`
// `Visuals/SpewSlotNode` anchors plus their four `SpewParticles` children, i.e. 100% of the wire. Max gap between
// messages 46.7ms; not one 200ms window of silence (the Aug-7 round's success criterion for a fold). The Aug-6
// recording shows the same shape one encounter earlier: `sludge_spinner.tscn`'s `MouthSpraySlot`,
// `MouthDribbleBoneNode` and `testBone` in 868 of 868 messages.
//
// WHAT THESE NODES ARE. `SpineSlotNode` / `SpineBoneNode` are spine-godot GDExtension `Node2D`s (registered by
// `addons/spine/spine_godot_extension.gdextension`, so they have no `MegaCrit.Sts2.*` script). Each binds one
// skeleton attachment by name and the extension WRITES ITS OWN position/rotation/scale from the skeleton's live
// pose every frame. They set no texture, no size, no rect, no content of any kind — pure transform carriers. That
// is the same class the watcher's frozen-spine read-elision already calls "browser-inert" (see
// Tracked.IsSpineSkeletonLeafType), and the mirror client agrees: `placementBox` (`nodeStyles.ts:256`) returns null
// for them, so the anchor's own element is a bare grouping div with no transform of its own.
//
// BUT THE TRANSFORM IS NOT DEAD WEIGHT — this is the constraint the whole fold is shaped around. CouchCoop streams
// `transformSpace: "local"`, and the renderer composes a running global down the tree
// (`mirrorRenderer.ts:9023-9033`), so an anchor's local IS a factor in its children's composed global. Its only
// screen effect is positioning a child emitter. Withholding a MOVING anchor's transform therefore drags that child
// with it — which is why arm B below is gated on the emitters being idle, and why the fold must resume the instant
// one starts (a particle burst has to come out of the right mouth).
//
// TWO ARMS, deliberately different in kind:
//
//   A. CHILDLESS anchor — STRUCTURAL, no scene table. An anchor with no tracked descendants cannot be a factor in
//      anybody's composed global, and paints nothing itself, so its transform provably cannot move a pixel under
//      ANY condition. 70 of the corpus's 236 anchors are childless pure measurement/targeting probes
//      (`sludge_spinner.tscn`'s `testBone` / `HoverHeightAdjust` / `TargetingDistanceBone` bind `oil_barf_bone`,
//      `hover_target_bone`, `attack_target_bone` — the game reads their global position in C#; the browser has no
//      use for them at all). No table can be justified here: the proof is structural and re-checked EVERY tick, so
//      an anchor that GAINS a runtime-attached VFX child stops qualifying on the very capture the child appears in
//      (pre-order DFS puts the child in `_ordered` before the anchor is read).
//
//   B. EMITTER-PARENT anchor — SCENE-IDENTITY TABLE (below) + runtime gates. Here the decision is about CONTENT
//      ("this anchor's motion does not matter right now"), not structure, so it follows the house rule of
//      Sts2DecorEmitSuppress: an EXPLICIT table, never a heuristic. On top of the table the watcher re-proves, per
//      tick, that (a) the node is a spine skeleton leaf, (b) it carries no browser-visible field of its own, and
//      (c) every descendant is inert-or-emitter AND every emitter is quiet — quiet meaning `Emitting == false` for
//      at least its own burst tail (see BurstTailMs), so the particles of a burst that just ended are not left
//      hanging at a stale mouth.
//
// SUPPRESSION, NOT SUBSTITUTION. Unlike every R12-R14 fold this one pins NOTHING and names no `PinnedLoopAnim`:
// there is no analytic rest pose to substitute (the skeleton owns the value) and nothing for the client to replay
// (it paints no pixel). The watcher simply withholds the transform via the same `Tracked.SuppressTransformUntil`
// machinery + depth sentinel that streaming tween-suppression uses, which is also what silences the CHILD emitters
// for free — their re-based local wobbles in the 4th decimal every tick as the parent moves (measured: 53-81
// distinct values per child over 6s, crossing the emit test's 2-dp rounding ~150 times), and a suppressed subtree
// stops re-sending it.
//
// AND IT SELF-HEALS ON THE EDGE. While suppressed, `ApplyIfChanged` does not advance `LastTransform`, so the moment
// any gate stops holding — an emitter starts, a VFX child is attached, the creature's own paint appears — the live
// transform differs from the stale last-emitted one and the node emits immediately, on that same capture. The
// membership edge is its own change trigger, exactly like `PinnedLoopAnim` is for the R12b map-point fold.
//
// KNOWN COST, accepted (kill switches: SPIRECTL_SPINE_ANCHOR_FOLD=0, or the master SPIRECTL_DECOR_EMIT_SUPPRESS=0):
// while a creature idles, its emitters' spawn points sit at the pose they last streamed instead of tracking the
// skeleton. Nothing is being emitted there (arm B's whole gate), and the previous burst has outlived its tail, so
// the only thing that can be visibly wrong is a particle system that spawns while reporting `Emitting == false` —
// which Godot cannot do.
internal static class Sts2SpineAnchorFold
{
    /// <summary>
    /// Scene file → the scene-relative paths of that creature's EMITTER-PARENT anchors (arm B). Childless anchors
    /// are deliberately ABSENT: arm A handles them structurally, corpus-wide, and listing them here would imply the
    /// table is the thing keeping them safe.
    ///
    /// <para>SCOPE: `scenes/creature_visuals/` — the measured problem is the COMBAT idle floor, and a creature is
    /// the only thing mounted for a whole fight. Background/merchant/rest-site spines have anchors too (236 across
    /// 80 scenes corpus-wide); they are out of scope for arm B until something measures them, while arm A already
    /// covers their childless probes.</para>
    ///
    /// <para>DERIVATION (reproducible): every `SpineSlotNode`/`SpineBoneNode` in
    /// `.sts2/toolchain/recovered-project/scenes/creature_visuals/*.tscn` whose authored subtree is non-empty,
    /// contains at least one `GPUParticles2D`/`CPUParticles2D`, and contains NOTHING but those plus plain `Node2D`
    /// grouping nodes — i.e. every anchor that positions only emitters. Anchors whose subtree holds a `Line2D`
    /// (architect's trail, kin_follower's boomerangs, soul_nexus's paths), a `Sprite2D`/`TextureRect`/`ColorRect`
    /// (necrobinder's head, spectral_knight's head fire, queen's eyes), a `MeshInstance2D` (the decimillipede's
    /// fx nodes, cubex's laser) or a nested `SpineSprite` (regent's weapons, test_subject's burn VFX) are EXCLUDED
    /// by construction — those paint, so their anchor's motion is on screen. The runtime subtree scan re-proves the
    /// same property per tick, so a stale row can only ever fail closed.</para>
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmitterAnchorPathsByScene =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["res://scenes/creature_visuals/architect.tscn"] =
                ["Visuals/FireSlot"],
            ["res://scenes/creature_visuals/axebot.tscn"] =
                ["Visuals/SmokeNodeRight", "Visuals/SmokeNodeLeft", "Visuals/SparksBoneNode"],
            ["res://scenes/creature_visuals/battle_friend_v1.tscn"] =
                ["Visuals/ParticlesSlot"],
            ["res://scenes/creature_visuals/battle_friend_v2.tscn"] =
                ["Visuals/ParticlesSlot"],
            ["res://scenes/creature_visuals/battle_friend_v3.tscn"] =
                ["Visuals/ParticlesSlot"],
            ["res://scenes/creature_visuals/battleworn_dummy.tscn"] =
                ["Visuals/ParticlesSlot"],
            ["res://scenes/creature_visuals/decimillipede.tscn"] =
                ["ShakeNode/Visuals3/fxnode2", "ShakeNode/Visuals2/fxnode1", "ShakeNode/Visuals2/fxnode2", "ShakeNode/Visuals/fxnode3"],
            ["res://scenes/creature_visuals/decimillipede_segment_back.tscn"] =
                ["Visuals/fxnode2"],
            ["res://scenes/creature_visuals/decimillipede_segment_front.tscn"] =
                ["Visuals/fxnode4/fxnode3"],
            ["res://scenes/creature_visuals/decimillipede_segment_middle.tscn"] =
                ["Visuals/fxnode1", "Visuals/fxnode2"],
            ["res://scenes/creature_visuals/devoted_sculptor.tscn"] =
                ["Visuals/VoiceBoneNode"],
            ["res://scenes/creature_visuals/fake_merchant_monster.tscn"] =
                ["Visuals/ParticlesSlot"],
            ["res://scenes/creature_visuals/fogmog.tscn"] =
                ["Visuals/DustSlotNode", "Visuals/ThrustSlotNode"],
            ["res://scenes/creature_visuals/fossil_stalker.tscn"] =
                ["Visuals/DebuffBone"],
            ["res://scenes/creature_visuals/fuzzy_wurm_crawler.tscn"] =
                ["Visuals/SpitParticlesBone"],
            ["res://scenes/creature_visuals/gas_bomb.tscn"] =
                ["Visuals/SmokeBallSlot"],
            ["res://scenes/creature_visuals/haunted_ship.tscn"] =
                ["Visuals/HeadSlot", "Visuals/EyeBone1", "Visuals/EyeBone2", "Visuals/EyeBone3"],
            ["res://scenes/creature_visuals/hunter_killer.tscn"] =
                ["Visuals/MouthBone"],
            ["res://scenes/creature_visuals/kaiser_crab_boss_setup.tscn"] =
                ["Visuals/RocketSlot", "Visuals/SpittleSlot", "Visuals/RegenSplatSlot", "Visuals/PlowChunkSlot"],
            ["res://scenes/creature_visuals/kin_follower.tscn"] =
                ["Visuals/HaySlot"],
            ["res://scenes/creature_visuals/living_fog.tscn"] =
                ["Visuals/AttackFXNode"],
            ["res://scenes/creature_visuals/magi_knight.tscn"] =
                ["Visuals/SpineSlotNode"],
            ["res://scenes/creature_visuals/mecha_knight.tscn"] =
                ["Visuals/EngineSlot/EngineBone", "Visuals/FlameParticlesBone"],
            ["res://scenes/creature_visuals/necrobinder.tscn"] =
                ["Visuals/ScytheVfxSlot2", "Visuals/ScytheVfxSlot1"],
            ["res://scenes/creature_visuals/parafright.tscn"] =
                ["Visuals/ParticlesSlot"],
            // The measured case: four instances of this one anchor were 100% of a 6s idle wire.
            ["res://scenes/creature_visuals/phantasmal_gardener.tscn"] =
                ["Visuals/SpewSlotNode"],
            ["res://scenes/creature_visuals/phrog_parasite.tscn"] =
                ["Visuals/BubbleBSlotNode", "Visuals/BubbleCBoneNode", "Visuals/BubbleABoneNode"],
            ["res://scenes/creature_visuals/queen.tscn"] =
                ["Visuals/SpineParticleSlot"],
            ["res://scenes/creature_visuals/regent.tscn"] =
                ["Visuals/SpineArmBone", "Visuals/SpineChestBone", "Visuals/SpineLegBoneL", "Visuals/SpineLegBone"],
            ["res://scenes/creature_visuals/sewer_clam.tscn"] =
                ["Visuals/MouthSlot"],
            ["res://scenes/creature_visuals/skulking_colony.tscn"] =
                ["Visuals/ParticleSlot1", "Visuals/ParticleSlot2", "Visuals/ParticleSlot3"],
            ["res://scenes/creature_visuals/slimed_berserker.tscn"] =
                ["Visuals/ParticleSlotNodeL", "Visuals/ParticleSlotNodeR", "Visuals/ParticleSlotNodeVomit"],
            // The Aug-6 recording's always-on pair (its `testBone` is arm A's).
            ["res://scenes/creature_visuals/sludge_spinner.tscn"] =
                ["Visuals/MouthSpraySlot", "Visuals/MouthDribbleBoneNode"],
            ["res://scenes/creature_visuals/test_subject.tscn"] =
                ["CanvasGroup/Visuals/NeckParticlesSlot"],
            ["res://scenes/creature_visuals/the_forgotten.tscn"] =
                ["Visuals/GranuleEmitterBone"],
            ["res://scenes/creature_visuals/the_insatiable.tscn"] =
                ["Visuals/SandSlotNode", "Visuals/SalivaSlotNode", "Visuals/BaseBlastSlot"],
            ["res://scenes/creature_visuals/the_lost.tscn"] =
                ["Visuals/GranuleEmitterBone"],
            ["res://scenes/creature_visuals/the_obscura.tscn"] =
                ["Visuals/ParticlesSlot"],
            ["res://scenes/creature_visuals/torch_head_amalgam.tscn"] =
                ["Visuals/torch2Slot", "Visuals/torch3Slot", "Visuals/torch1Slot", "Visuals/laserBaseBone", "Visuals/torch1UnscaledBone", "Visuals/torch2UnscaledBone", "Visuals/torch3UnscaledBone"],
            ["res://scenes/creature_visuals/vantom.tscn"] =
                ["Visuals/SprayBoneNode", "Visuals/DeathSpraySlotNode", "Visuals/DeathSprayBackSlotNode", "Visuals/DeathExplosionSlotNode"],
            ["res://scenes/creature_visuals/waterfall_giant.tscn"] =
                ["Visuals/SteamSlot4", "Visuals/SteamSlot3", "Visuals/SteamLeakSlot3", "Visuals/MistSlot", "Visuals/SteamLeakSlot1", "Visuals/SteamLeakSlot2", "Visuals/SteamSlot5", "Visuals/SteamSlot6", "Visuals/MouthDropletsSlot", "Visuals/SteamSlot1", "Visuals/SteamSlot2"],
        };

    /// <summary>True when this scene identity is one of the tabled emitter-parent anchors (arm B).</summary>
    internal static bool IsEmitterAnchor(string? sceneFilePath, string? relPath)
    {
        if (sceneFilePath is null || relPath is null
            || !EmitterAnchorPathsByScene.TryGetValue(sceneFilePath, out var paths))
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

    // ---- The decision --------------------------------------------------------------------------------------

    /// <summary>
    /// The whole rule, as one pure table. <paramref name="hasDescendants"/> selects the arm.
    ///
    /// <para>`skeletonLeaf` = Tracked.IsSpineSkeletonLeafType (a spine-family native class that is NOT the
    /// SpineSprite clip root). `carriesOwnPaint` = this node contributes a browser-visible field of its own; both
    /// arms refuse then, because the client WOULD place such a node and a withheld transform would strand it.</para>
    /// </summary>
    internal static bool ShouldSuppressTransform(
        bool skeletonLeaf,
        bool carriesOwnPaint,
        bool hasDescendants,
        bool tabledEmitterAnchor,
        bool subtreeQuiet)
    {
        if (!skeletonLeaf || carriesOwnPaint)
        {
            return false;
        }

        // Arm A — childless: provable from structure alone, no table, no gate.
        // Arm B — emitter parent: the table names it AND the live subtree scan agreed it is idle this tick.
        return hasDescendants ? tabledEmitterAnchor && subtreeQuiet : true;
    }

    // ---- The subtree scan's policy (the walk supplies the facts) -------------------------------------------

    /// <summary>
    /// How deep below the anchor the subtree scan will look, and how many nodes it will look at, before it gives up
    /// and streams (fail-open). The authored subtrees are 1-2 deep and 1-3 wide; the caps exist so a runtime
    /// reparent can never turn the scan into an unbounded walk on the per-tick path.
    /// </summary>
    internal const int MaxSubtreeDepth = 3;

    internal const int MaxSubtreeNodes = 16;

    /// <summary>
    /// Native classes a descendant may have without disqualifying the anchor: pure grouping/attachment nodes that
    /// paint nothing themselves. Anything else — a Sprite2D, TextureRect, Line2D, MeshInstance2D, ColorRect, Label,
    /// a nested SpineSprite, an unrecognised class — makes the anchor's motion visible, so the fold refuses.
    /// Matched on the NATIVE class (Godot `GetClass()`), which sees through a script-attached wrapper whose managed
    /// type is only the script's base — the same reason Tracked.IsSpineSkeletonLeafType and ClassifyLine use it.
    /// Particle emitters are NOT here: they are recognised by their static ParticleSpec and gated on `Emitting`.
    /// </summary>
    internal static bool IsInertDescendantClass(string? nativeClass)
        => nativeClass is "Node2D" or "Marker2D" or "SpineBoneNode" or "SpineSlotNode" or "SpineMesh2D";

    /// <summary>
    /// How long after an emitter's last `Emitting == true` tick its particles can still be on screen, i.e. how long
    /// the anchor must keep streaming after a burst ends. Godot runs a cycle for `lifetime * (2 - explosiveness)`
    /// (particles.cpp `active_time`: at explosiveness 1 every particle is born at t=0 so the cycle is one lifetime;
    /// at 0 births spread over a lifetime, so the last particle dies at 2x), which also bounds the tail of a LOOPING
    /// emitter that was just switched off. Clamped so a degenerate spec can neither skip the tail nor hold the
    /// anchor streaming indefinitely.
    ///
    /// <para>WHY IT MATTERS, concretely: the browser runtime simulates every particle INSIDE its emitter's element
    /// — `localCoords` reaches its config and nothing reads it (godot-scene-web `particles/state.ts`,
    /// `particles/config.ts`) — so a live burst tracks whatever transform that element last received, even for the
    /// STS2 emitters authored `local_coords = false`. Freezing the anchor the instant `Emitting` clears would
    /// therefore leave a dying burst hanging at a stale mouth. Waiting out the tail costs a fraction of a second of
    /// streaming per burst and removes the whole failure class.</para>
    /// </summary>
    internal static long BurstTailMs(double lifetimeSeconds, double explosiveness)
    {
        var tail = lifetimeSeconds * (2.0 - explosiveness) * 1000.0;
        if (!double.IsFinite(tail))
        {
            return DefaultBurstTailMs;
        }

        return (long)Math.Clamp(tail, MinBurstTailMs, MaxBurstTailMs);
    }

    /// <summary>Tail used when a descendant emitter has no readable spec (inspection failed on add).</summary>
    internal const long DefaultBurstTailMs = 1000;

    /// <summary>Floor/ceiling for <see cref="BurstTailMs"/>. The floor covers one client frame plus wire latency.</summary>
    internal const long MinBurstTailMs = 100;

    internal const long MaxBurstTailMs = 3000;
}
