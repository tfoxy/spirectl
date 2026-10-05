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
// THREE ARMS, deliberately different in kind:
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
//   C. HIDDEN-DESCENDANT anchor — STRUCTURAL again, no table (switch SPIRECTL_SPINE_ANCHOR_HIDDEN_FOLD, default on,
//      under both masters). Every descendant is either an inert class or a CanvasItem whose OWN `visible` is false;
//      a hidden descendant's subtree is pruned, not scanned, because nothing below a hidden CanvasItem paints and
//      the client composes visibility down the same chain. The measured case after R15 was the Ironclad's
//      `Visuals/EyeSlot` (a `SpineSlotNode`) whose only child `EyeFire` (a `TextureRect`) is hidden: about 35
//      transform deltas per second, nearly the whole idle-combat wire, for a pose nothing on screen used. Arms A
//      and B both miss it (it has a child; the child is neither inert nor an emitter). The fact is built from what
//      the walk already holds — each descendant's last-read `visible` and its cached class — so it adds no read.
//
//      THE PRICE OF REUSING LAST CAPTURE'S `visible`: the walk is pre-order, so an anchor is decided before its
//      descendants are read this tick. On the tick a hidden descendant turns visible the anchor has already been
//      withheld, and the descendant was read relative to the anchor's LIVE pose. So the watcher records each arm-C
//      subtree as it reads it (FlipFlushRecorder) and, after the walk, re-runs the same scan over THIS tick's reads;
//      if it no longer holds, it ships the anchor's live transform — and that of every recorded subtree node that
//      moved — in the same delta, ahead of the descendant (DecideFlush / InsertInOrder). Every value it ships was
//      read this tick; nothing is re-read. The
//      next capture's scan sees the new `visible` and stops folding on its own. Arms A and B need none of this:
//      their facts (structure, `Emitting`) are read live at the anchor.
//
// FROZEN SKELETONS. The watcher stops reading the skeleton leaves of a frozen, stationary SpineSprite root (their
// pose cannot change). A pose this fold withheld before the freeze would then stay stale on the client for good,
// while the anchor's children are still read and placed against its LIVE pose. So a withheld leaf with descendants
// is exempt from that elision (ShouldElideFrozenLeaf), and under a frozen, stationary root no arm withholds
// (DecideArm): the pose ships once, and the elision takes over from the next capture. Which poses count as withheld
// is PoseStillWithheld: the mark survives any other suppression (a tween window) until the pose really ships.
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
// KNOWN COST, accepted (kill switches: SPIRECTL_SPINE_ANCHOR_FOLD=0, or the master SPIRECTL_DECOR_EMIT_SUPPRESS=0;
// arm C alone: SPIRECTL_SPINE_ANCHOR_HIDDEN_FOLD=0): while a creature idles, its emitters' spawn points sit at the
// pose they last streamed instead of tracking the skeleton. Nothing is being emitted there (arm B's whole gate),
// and the previous burst has outlived its tail, so the only thing that can be visibly wrong is a particle system
// that spawns while reporting `Emitting == false` — which Godot cannot do. Arm C's hidden children likewise sit at
// a stale pose only while they cannot be seen; the flip flush moves them before the frame they become visible.
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

    /// <summary>Which arm withheld the transform. Only <see cref="Arm.HiddenDescendants"/> needs the flip flush.</summary>
    internal enum Arm : byte
    {
        None = 0,
        Childless,
        TabledEmitters,
        HiddenDescendants,
    }

    /// <summary>
    /// The whole rule, as one pure table, naming the arm that withholds the transform (or <see cref="Arm.None"/>).
    /// <paramref name="hasDescendants"/> selects the arm. The watcher needs the name: arms A and B are decided on
    /// facts that are live this tick, so they self-heal on the edge; arm C is decided on facts one capture old, so
    /// its suppression must be undone in the same delta when they turn out to have changed (see
    /// <see cref="DecideFlush"/>).
    ///
    /// <para>`skeletonLeaf` = Tracked.IsSpineSkeletonLeafType (a spine-family native class that is NOT the
    /// SpineSprite clip root). `carriesOwnPaint` = this node contributes a browser-visible field of its own; every
    /// arm refuses then, because the client WOULD place such a node and a withheld transform would strand it.</para>
    ///
    /// <para><paramref name="hiddenArmEnabled"/> is the SPIRECTL_SPINE_ANCHOR_HIDDEN_FOLD switch (the watcher only
    /// reaches this call at all under the two R15 master switches), and <paramref name="descendantsHiddenOrInert"/>
    /// is <see cref="DescendantsHiddenOrInert{TView}"/>'s verdict.</para>
    ///
    /// <para><paramref name="frozenStationaryRoot"/>: the anchor sits under a frozen, stationary SpineSprite root
    /// whose skeleton leaves the watcher read-elides. Then no arm withholds: the leaf cannot move any more, so
    /// streaming it costs one settle emit, after which it is elided like every other frozen leaf (see
    /// <see cref="ShouldElideFrozenLeaf"/>). Withholding it instead would freeze a STALE pose on the client for as
    /// long as the skeleton stays frozen, while its children keep being placed against the live one.</para>
    /// </summary>
    internal static Arm DecideArm(
        bool skeletonLeaf,
        bool carriesOwnPaint,
        bool hasDescendants,
        bool tabledEmitterAnchor,
        bool subtreeQuiet,
        bool hiddenArmEnabled,
        bool descendantsHiddenOrInert,
        bool frozenStationaryRoot = false)
    {
        if (!skeletonLeaf || carriesOwnPaint || frozenStationaryRoot)
        {
            return Arm.None;
        }

        // Arm A — childless: provable from structure alone, no table, no gate.
        if (!hasDescendants)
        {
            return Arm.Childless;
        }

        // Arm B — emitter parent: the table names it AND the live subtree scan agreed it is idle this tick.
        if (tabledEmitterAnchor && subtreeQuiet)
        {
            return Arm.TabledEmitters;
        }

        // Arm C — every descendant is inert or locally hidden: structural, no table.
        return hiddenArmEnabled && descendantsHiddenOrInert ? Arm.HiddenDescendants : Arm.None;
    }

    /// <summary>
    /// The watcher's frozen-spine read-elision, with the one exemption this fold needs. Under a frozen, stationary
    /// SpineSprite root a skeleton leaf's pose cannot change, so it is normally not read at all — but a leaf whose
    /// pose the fold WITHHELD (it, or an ancestor anchor, was suppressed while the skeleton still moved) is stale on
    /// the client, and if it has descendants they are still read and placed against its LIVE pose. Such a leaf takes
    /// the normal read path instead, where <see cref="DecideArm"/>'s frozen-root rule lets its pose ship once; that
    /// clears <paramref name="poseWithheld"/> and it is elided from the next capture on. A childless withheld leaf
    /// stays elided: nothing composes against it.
    /// </summary>
    internal static bool ShouldElideFrozenLeaf(
        bool elisionEnabled,
        bool fullCapture,
        bool frozenStationaryRoot,
        bool skeletonLeaf,
        bool poseWithheld,
        bool hasDescendants)
        => elisionEnabled && !fullCapture && frozenStationaryRoot && skeletonLeaf && !(poseWithheld && hasDescendants);

    /// <summary>
    /// After a node's read and emit step: does the client still hold a pose for it that the FOLD left stale? This is
    /// the mark <see cref="ShouldElideFrozenLeaf"/> reads, so clearing it early would let the elision freeze a stale
    /// pose for good; keeping it a capture too long costs at most one extra read and settle emit.
    ///
    /// <list type="bullet">
    /// <item>Arm A withheld it (<paramref name="foldedChildless"/>): marked without a compare. The node is childless,
    /// so the elision ignores the mark until a child is attached — exactly when the stale pose starts to matter —
    /// and the hundreds of childless skeleton meshes pay nothing.</item>
    /// <item>Inside an arm-B/C fold (root or subtree): withheld while this tick's transform differs from the last one
    /// shipped — unless this capture's upsert carried it and no tween window covers the node, in which case the
    /// client now holds exactly it.</item>
    /// <item>Otherwise suppressed (a tween window the fold did not open — the fold block is skipped then): the change
    /// test neither saw nor recorded the pose, so the mark stays as it was. Clearing it here was the bug where a
    /// creature's lunge tween wiped the mark of an anchor the fold had withheld just before.</item>
    /// <item>Not suppressed at all: the change test compared the live transform and recorded it when it emitted, so
    /// the client holds the live pose (to the emit test's precision).</item>
    /// </list>
    /// </summary>
    internal static bool PoseStillWithheld(
        bool wasWithheld,
        bool foldedChildless,
        bool insideFold,
        bool transformSuppressed,
        bool tweenWindow,
        bool shippedLiveTransform,
        bool differsFromLastShipped)
    {
        if (foldedChildless)
        {
            return true;
        }

        if (insideFold)
        {
            return !(shippedLiveTransform && !tweenWindow) && differsFromLastShipped;
        }

        return transformSuppressed && wasWithheld;
    }

    // ---- Arm C: the hidden-descendant fact --------------------------------------------------------------------

    /// <summary>What one descendant contributes to arm C's fact.</summary>
    internal enum DescendantKind : byte
    {
        /// <summary>A grouping/attachment class that paints nothing itself (<see cref="IsInertDescendantClass"/>).</summary>
        Inert = 0,

        /// <summary>A CanvasItem whose OWN `visible` is false: it and its whole subtree paint nothing.</summary>
        Hidden,

        /// <summary>Visible and not an inert class: the anchor's motion reaches the screen through it.</summary>
        Paints,

        /// <summary>No trustworthy fact yet (a node not read since it was added). Treated like
        /// <see cref="Paints"/>: the fold fails closed, i.e. keeps streaming.</summary>
        Unknown,
    }

    /// <summary>
    /// A pre-order slice of the watcher's node list starting AT the anchor: index 0 is the anchor, the entries after
    /// it belong to its subtree while their depth exceeds the anchor's. <see cref="KindAt"/> is only asked for
    /// entries the scan actually looks at, so a caller can make it lazy (the class probe is not free).
    /// </summary>
    internal interface ISubtreeView
    {
        int Count { get; }

        int DepthAt(int index);

        DescendantKind KindAt(int index);
    }

    /// <summary>
    /// Arm C's fact: is every descendant of the anchor either inert or locally hidden? A hidden descendant's own
    /// subtree is PRUNED, not scanned — nothing below a hidden CanvasItem can paint, and the client composes
    /// visibility down the same chain. Bounded like arm B's scan: past <see cref="MaxSubtreeNodes"/> visited entries
    /// (pruned ones included, so a large hidden subtree cannot make the walk unbounded) or past
    /// <see cref="MaxSubtreeDepth"/> for an entry it has to classify, it answers false and the anchor streams.
    /// </summary>
    internal static bool DescendantsHiddenOrInert<TView>(ref TView view)
        where TView : struct, ISubtreeView
    {
        var count = view.Count;
        if (count <= 1)
        {
            return true;
        }

        var anchorDepth = view.DepthAt(0);
        var visited = 0;
        var prunedBelow = int.MaxValue;
        for (var i = 1; i < count; i++)
        {
            var depth = view.DepthAt(i);
            if (depth <= anchorDepth)
            {
                break; // left the anchor's subtree
            }

            if (++visited > MaxSubtreeNodes)
            {
                return false;
            }

            if (depth > prunedBelow)
            {
                continue; // inside a hidden descendant's subtree
            }

            prunedBelow = int.MaxValue;
            if (depth - anchorDepth > MaxSubtreeDepth)
            {
                return false;
            }

            switch (view.KindAt(i))
            {
                case DescendantKind.Hidden:
                    prunedBelow = depth;
                    break;
                case DescendantKind.Inert:
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    // ---- Arm C: the flip flush -----------------------------------------------------------------------------------

    /// <summary>
    /// One node of an arm-C-suppressed subtree as the capture saw it THIS tick, root first, in pre-order. Only nodes
    /// the walk actually read are recorded (a node pruned under a still-hidden parent is not).
    /// </summary>
    /// <param name="Depth">Tree depth, for the same pruning as <see cref="DescendantsHiddenOrInert{TView}"/>.</param>
    /// <param name="Kind">This tick's kind (own `visible` as just read). Ignored for the root.</param>
    /// <param name="Emitted">The capture already put an upsert for this node in the delta.</param>
    /// <param name="EmittedTransform">...and that upsert carries a transform (this tick's read).</param>
    /// <param name="HasTransform">This tick's read has a transform at all.</param>
    /// <param name="TransformDiffers">This tick's transform differs from the last one shipped, at the emit test's
    /// precision.</param>
    /// <param name="TweenWindow">A client-replayed transform window covers the node — its own, or an ancestor's
    /// (inside the region too), apart from the fold's own sentinel: the client discards a streamed transform for it,
    /// and one shipped mid-transition would be pinned for the rest of the window — the reason a reparent emit omits
    /// it. The flush never adds one.</param>
    internal readonly record struct FlushEntry(
        int Depth,
        DescendantKind Kind,
        bool Emitted,
        bool EmittedTransform,
        bool HasTransform,
        bool TransformDiffers,
        bool TweenWindow = false);

    internal enum FlushAction : byte
    {
        /// <summary>Leave the node alone.</summary>
        None = 0,

        /// <summary>The delta already ships this tick's transform: record it as the last shipped one.</summary>
        Advance,

        /// <summary>The node's upsert omitted its transform: put this tick's transform on it, then advance.</summary>
        Patch,

        /// <summary>Add an upsert carrying this tick's transform at the node's pre-order position, then advance.</summary>
        Ship,
    }

    /// <summary>A node's part in the flip flush this capture.</summary>
    internal enum FlushRole : byte
    {
        None = 0,
        Root,
        Descendant,
    }

    /// <summary>
    /// What to do with one recorded node after the walk.
    ///
    /// <para>An upsert that already carries this tick's transform is always recorded as shipped, flip or not: the
    /// client now holds that pose (a keyframe, or a hidden child whose modulate changed), so the next change test
    /// must compare against it.</para>
    ///
    /// <para>On a flip, every recorded node of the subtree must reach the client at this tick's pose IN THIS DELTA:
    /// the newly visible descendant was read relative to the root's live pose, so the root and every node between
    /// them have to arrive with it, or it draws one capture late at a stale anchor. A node whose transform did not
    /// change since it was last shipped is already there.</para>
    ///
    /// <para>A node a tween window covers is left alone entirely: the client is replaying that tween and would pin
    /// whatever transform arrived, and the window's own settle re-emit ships the final pose.</para>
    /// </summary>
    internal static FlushAction DecideFlush(in FlushEntry entry, bool triggered)
    {
        if (entry.TweenWindow)
        {
            return FlushAction.None;
        }

        if (entry.Emitted)
        {
            if (entry.EmittedTransform)
            {
                return FlushAction.Advance;
            }

            return triggered && entry.HasTransform ? FlushAction.Patch : FlushAction.None;
        }

        return triggered && entry.HasTransform && entry.TransformDiffers ? FlushAction.Ship : FlushAction.None;
    }

    /// <summary>
    /// Merge flushed upserts into the delta's upsert list at their pre-order positions. A position is the list's
    /// length when the walk reached that node, so a root is inserted ahead of any descendant upsert that the walk
    /// appended later, and entries sharing a position keep the order they were recorded in (pre-order). Positions
    /// must be non-decreasing, which the walk guarantees.
    /// </summary>
    internal static void InsertInOrder<T>(List<T> items, IReadOnlyList<(int Position, T Item)> inserts)
    {
        if (inserts.Count == 0)
        {
            return;
        }

        var merged = new List<T>(items.Count + inserts.Count);
        var next = 0;
        for (var i = 0; i <= items.Count; i++)
        {
            while (next < inserts.Count && inserts[next].Position <= i)
            {
                merged.Add(inserts[next].Item);
                next++;
            }

            if (i < items.Count)
            {
                merged.Add(items[i]);
            }
        }

        while (next < inserts.Count)
        {
            merged.Add(inserts[next].Item);
            next++;
        }

        items.Clear();
        items.AddRange(merged);
    }

    /// <summary>What the flush does to the watcher's own state; the recorder decides, this acts.</summary>
    internal interface IFlushTarget<TPayload, TDelta>
    {
        /// <summary>A volatile upsert carrying this tick's read (transform included), for a node that had none.</summary>
        TDelta BuildShip(in TPayload node);

        /// <summary>The node's existing upsert, with this tick's transform put on it.</summary>
        TDelta WithTransform(TDelta upsert, in TPayload node);

        /// <summary>This tick's transform reached the client: it is now the last shipped one.</summary>
        void Shipped(in TPayload node);

        /// <summary>The node's changed transform stays withheld this capture (profiler bookkeeping).</summary>
        void Withheld(in TPayload node);
    }

    /// <summary>
    /// The recording half of the flip flush, so the bookkeeping the watcher's walk does is the code under test:
    /// which nodes belong to an arm-C region (<see cref="Enter"/> / <see cref="BeginRoot"/>), where each node's upsert
    /// sits in pre-order (<see cref="Record"/> takes it from the upsert list itself — right after this node's own
    /// append, or where it would have gone), and the post-walk decision and merge (<see cref="Flush"/>). One reused
    /// list of structs; nothing allocates per capture unless a flip actually ships something.
    /// </summary>
    internal sealed class FlipFlushRecorder<TPayload, TDelta>
    {
        private readonly List<Recorded> _records = [];
        private int _rootDepth = int.MaxValue;

        public int Count => _records.Count;

        /// <summary>Forget everything recorded. Called at the start of every capture, whatever it returns.</summary>
        public void Reset()
        {
            _records.Clear();
            _rootDepth = int.MaxValue;
        }

        /// <summary>Called for EVERY node the walk visits, before anything can skip it: leaves the current region
        /// when the walk is back at or above its root's depth, and says whether this node is inside one.</summary>
        public FlushRole Enter(int depth)
        {
            if (_rootDepth != int.MaxValue && depth <= _rootDepth)
            {
                _rootDepth = int.MaxValue;
            }

            return _rootDepth != int.MaxValue ? FlushRole.Descendant : FlushRole.None;
        }

        /// <summary>Arm C (and only arm C) withheld this node: it starts a region.</summary>
        public FlushRole BeginRoot(int depth)
        {
            _rootDepth = depth;
            return FlushRole.Root;
        }

        /// <summary>
        /// Record a node the walk read, AFTER its emit step. <paramref name="emitted"/> means its upsert was the last
        /// one appended to <paramref name="upserts"/>; otherwise it would have gone at the current end.
        /// </summary>
        public void Record(
            FlushRole role,
            List<TDelta> upserts,
            in TPayload payload,
            int depth,
            DescendantKind kind,
            bool emitted,
            bool emittedTransform,
            bool hasTransform,
            bool transformDiffers,
            bool tweenWindow)
        {
            if (role == FlushRole.None)
            {
                return;
            }

            var entry = new FlushEntry(
                depth,
                role == FlushRole.Root ? DescendantKind.Inert : kind,
                emitted,
                emitted && emittedTransform,
                hasTransform,
                transformDiffers,
                tweenWindow);
            _records.Add(new Recorded(entry, payload, emitted ? upserts.Count - 1 : upserts.Count, role == FlushRole.Root));
        }

        /// <summary>
        /// After the walk, before the delta is judged empty: decide each region and apply the decisions. Inserts are
        /// merged last, so every recorded position still indexes the walk's own list while patches are applied.
        /// </summary>
        public void Flush(List<TDelta> upserts, IFlushTarget<TPayload, TDelta> target)
        {
            List<(int Position, TDelta Item)>? inserts = null;
            var start = 0;
            while (start < _records.Count)
            {
                var end = start + 1;
                while (end < _records.Count && !_records[end].IsRoot)
                {
                    end++;
                }

                // Did arm C's fact stop holding THIS tick? The anchor's own scan, over this tick's reads instead of
                // last capture's. When it did, the next capture's anchor scan fails on its own (the descendants' last
                // visible flags are now these), so the flush is needed exactly once per flip.
                var view = new RecordView(_records, start, end - start);
                var triggered = !DescendantsHiddenOrInert(ref view);
                for (var i = start; i < end; i++)
                {
                    var record = _records[i];
                    switch (DecideFlush(record.Entry, triggered))
                    {
                        case FlushAction.Advance:
                            target.Shipped(record.Payload);
                            break;
                        case FlushAction.Patch:
                            upserts[record.Position] = target.WithTransform(upserts[record.Position], record.Payload);
                            target.Shipped(record.Payload);
                            break;
                        case FlushAction.Ship:
                            (inserts ??= []).Add((record.Position, target.BuildShip(record.Payload)));
                            target.Shipped(record.Payload);
                            break;
                        default:
                            if (record.Entry.TransformDiffers && !record.Entry.EmittedTransform)
                            {
                                target.Withheld(record.Payload);
                            }
                            break;
                    }
                }

                start = end;
            }

            if (inserts is not null)
            {
                InsertInOrder(upserts, inserts);
            }

            Reset();
        }

        private readonly record struct Recorded(FlushEntry Entry, TPayload Payload, int Position, bool IsRoot);

        private readonly struct RecordView(List<Recorded> records, int start, int count) : ISubtreeView
        {
            public int Count => count;

            public int DepthAt(int index) => records[start + index].Entry.Depth;

            public DescendantKind KindAt(int index) => records[start + index].Entry.Kind;
        }
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
