using System;
using System.Collections.Generic;

namespace Spirectl.Sts2.Live;

// Producer DECORATIVE-ANIMATOR SCENE-IDENTITY TABLE — PURE and Godot-free (like Sts2RegionEmitCap /
// Sts2CosmeticEmitCap) so the policy is unit-testable without the Godot-coupled Sts2RuntimeSceneWatcher.
//
// WHY. A VISUALLY IDLE STS2 screen is not a QUIET screen. Several always-mounted UI nodes run an INFINITE, purely
// decorative per-frame animator whose only output is a transform (or a self-modulate alpha) that changes every
// single frame: the top-bar map/deck icons on the map and dialog screens, the proceed-button outline glow on the
// treasure and reward screens, and, in combat, the enemy-intent bob and glyph flip-book, the energy/star orb spin
// and the end-turn glow. Each one keeps a still screen paying the full delta pipeline every frame, and the
// watcher's idle backoff (MinEmitIntervalMs -> MaxIdleIntervalMs) can never engage, because something genuinely
// changed on every capture.
//
// WHAT THIS FILE IS. The SCENE-IDENTITY half of the answer, and only that: a node is matched by its instanced-scene
// file plus its scene-relative path, and the match names a CHANNEL. The watcher resolves each node's identity once
// (on add / on reparent) and then routes the matched channel to the fold that owns it:
//
//   channel               owner                 what it does
//   TopBarRotation        Sts2TopBarFold        divides the icon's rotation out + names the loop (deck/map/spin)
//   ProceedGlowAlpha      Sts2ProceedGlow       substitutes the loop's analytic alpha + names the loop
//   MapPointPulseScale    Sts2MapPointPulse     divides the travelable pulse's uniform scale out + names the loop
//   IntentBobPosition     Sts2IntentBobFold     subtracts the intent holder's sine position out (client bobs it)
//   IntentGlyphTexture    Sts2IntentGlyphFold   substitutes frame 0 of the already-streamed intent frame set
//   OrbSpinRotation       Sts2OrbSpinFold       divides the energy/star orb layers' accumulated rotation out
//   EndTurnGlowPulse      Sts2EndTurnGlowFold   pins the glow's scale+alpha to the loop's start + names the loop
//   SpineAnchorTransform  Sts2SpineAnchorFold   withholds an idle creature emitter-anchor's transform entirely
//
// The end-turn glow runs EXACTLY while the combat is idle-waiting on the player, which is why the combat channels
// matter. Creature spine anchors are the one exception to the "analytic pin + replay token" shape below: an anchor
// paints nothing, so there is nothing to pin and nothing to replay, and the fold simply WITHHOLDS the transform
// while it provably cannot move a pixel. See Sts2SpineAnchorFold.
//
// NO "PIN AND FORGET". Freezing a node's whole local transform or alpha at its FIRST-EMITTED sample is wrong: the
// first emission happens before Godot's DEFERRED Control layout settles, so an icon frozen that way stays at a
// MID-LAYOUT pose and renders misplaced for the rest of the run (see Sts2TopBarFold), and freezing deletes the
// animation outright. Every channel below is therefore an ANALYTIC pin of ONE channel plus a declarative
// `RuntimeSceneNodeDelta.PinnedLoopAnim` token the client replays — the wire stays silent AND the motion comes back,
// and nothing is ever pinned to a sampled value that a later layout pass would have moved.
//
// SCOPE / SAFETY. Deliberately an EXPLICIT table, not a heuristic: a "fold anything that churns" rule would
// eventually eat a real animation. Each rule names exactly one channel of exactly one node, every fold is gated on
// the exact condition under which that animation runs, and everything else about these nodes (visible, modulate,
// texture, rect, z, position, scale, ...) streams untouched — so show/hide, add/remove, layout and every
// gameplay-meaningful change is unaffected.
internal static class Sts2DecorEmitSuppress
{
    [Flags]
    internal enum Channels
    {
        None = 0,

        // TOP-BAR ICON ROCK/SPIN (see Sts2TopBarFold). The watcher divides the icon's ROTATION back out of the
        // streamed transform (position + scale keep streaming) while the button's screen is open, and ships the
        // per-button `PinnedLoopAnim` token the client replays. Its own kill switch is SPIRECTL_TOPBAR_FOLD.
        TopBarRotation = 1,

        // PROCEED-BUTTON GLOW (see Sts2ProceedGlow). The watcher substitutes the loop's ANALYTIC alpha (0.75)
        // into self_modulate while the infinite glow tween runs — RGB keeps streaming, so a real colour/state change
        // still ships — and names the loop. Its own kill switch is SPIRECTL_PROCEED_FOLD.
        ProceedGlowAlpha = 2,

        // MAP-POINT PULSE (see Sts2MapPointPulse). The travelable affordance: the watcher divides the pulse's
        // uniform SCALE back out of the transform (rotation/placement keep streaming) AND ships a declarative
        // `PinnedLoopAnim` flag the client replays. Its own kill switch is SPIRECTL_MAPPOINT_FOLD.
        MapPointPulseScale = 4,

        // ENEMY-INTENT BOB (see Sts2IntentBobFold). The watcher subtracts the bob's per-frame sine POSITION
        // back out of the holder's local transform (basis untouched), so the client's path-keyed `bob` replay
        // composes to the exact on-screen motion. Its own kill switch is SPIRECTL_INTENT_BOB_FOLD.
        IntentBobPosition = 8,

        // ENERGY/STAR ORB SPIN (see Sts2OrbSpinFold). The watcher divides each `%RotationLayers` child's
        // accumulated ROTATION back out of the transform (position/scale keep streaming); the client's path-keyed
        // `rotate` replay spins it. Its own kill switch is SPIRECTL_ORB_SPIN_FOLD.
        OrbSpinRotation = 16,

        // ENEMY-INTENT FLIP-BOOK (see Sts2IntentGlyphFold). The watcher substitutes FRAME 0 of the frame set it
        // already streams on this node (`IntentFrames`) for the 15fps per-frame crop — which the mirror client
        // discards anyway (`applyIntentFrame0`). Its own kill switch is SPIRECTL_INTENT_GLYPH_FOLD.
        IntentGlyphTexture = 32,

        // END-TURN GLOW (see Sts2EndTurnGlowFold). While the infinite `GlowPulse` loop runs — which is exactly
        // while the combat is idle-waiting on the player — the watcher pins the loop's two analytic start values
        // (uniform scale 0.5, `modulate.a` 0.4) and names the loop. Its own kill switch is
        // SPIRECTL_ENDTURN_GLOW_FOLD.
        EndTurnGlowPulse = 64,

        // CREATURE SPINE ANCHOR, emitter-parent arm (see Sts2SpineAnchorFold). A `SpineSlotNode`/`SpineBoneNode`
        // whose whole authored subtree is particle emitters: while every one of them is idle the watcher WITHHOLDS
        // the anchor's transform (and, through the depth sentinel, its subtree's) instead of substituting anything —
        // there is no analytic rest to pin and nothing for the client to replay. Its own kill switch is
        // SPIRECTL_SPINE_ANCHOR_FOLD. NOTE the fold's OTHER two arms deliberately carry no channel: a CHILDLESS
        // anchor (arm A) and an anchor whose every descendant is inert or locally hidden (arm C, its own switch
        // SPIRECTL_SPINE_ANCHOR_HIDDEN_FOLD) are proved structurally, per tick, for every scene in the game rather
        // than by this table.
        SpineAnchorTransform = 128,
    }

    // Scene-relative path of an instanced-scene ROOT (matches the presentation catalog's "." convention).
    internal const string RootRelPath = ".";

    // How far below a watched scene root a rule can sit. The five rules are 1-2 segments deep; the cap bounds the
    // per-node path building to the (tiny) watched subtrees and stops it dead in anything deeper.
    internal const int MaxRelDepth = 3;

    // scene file -> (scene-relative node path -> folded channels). The scene paths and rel paths are owned by the
    // fold that handles them, so a rule and its math can never drift apart.
    private static readonly Dictionary<string, Dictionary<string, Channels>> Rules = new(StringComparer.Ordinal)
    {
        // The three top-bar buttons: `Control/Icon` is the rocked/spun icon. All three share the `NTopBarButton`
        // base (and the same scene-relative path) while running three DIFFERENT loops, which is why the fold names
        // the loop per SCENE — see Sts2TopBarFold.LoopAnimFor.
        [Sts2TopBarFold.DeckButtonScene] = new(StringComparer.Ordinal)
        {
            [Sts2TopBarFold.IconRelPath] = Channels.TopBarRotation,
        },
        [Sts2TopBarFold.MapButtonScene] = new(StringComparer.Ordinal)
        {
            [Sts2TopBarFold.IconRelPath] = Channels.TopBarRotation,
        },
        [Sts2TopBarFold.SettingsButtonScene] = new(StringComparer.Ordinal)
        {
            [Sts2TopBarFold.IconRelPath] = Channels.TopBarRotation,
        },
        // The shared Proceed button (treasure / card reward / rest / event / shop ...). `Image/Outline` is an
        // additive TextureRect whose self_modulate alpha pulses forever; its transform is a constant identity.
        [Sts2ProceedGlow.SceneFile] = new(StringComparer.Ordinal)
        {
            [Sts2ProceedGlow.OutlineRelPath] = Channels.ProceedGlowAlpha,
        },
        // The map point whose icon group pulses while the node is TRAVELABLE.
        [Sts2MapPointPulse.SceneFile] = new(StringComparer.Ordinal)
        {
            [Sts2MapPointPulse.IconContainerRelPath] = Channels.MapPointPulseScale,
        },
        // The enemy intent. ONE scene, TWO folds, because an intent animates two ways at once: the holder's
        // sine POSITION and the glyph's 15fps flip-book. See Sts2IntentBobFold / Sts2IntentGlyphFold.
        [Sts2IntentBobFold.SceneFile] = new(StringComparer.Ordinal)
        {
            [Sts2IntentBobFold.HolderRelPath] = Channels.IntentBobPosition,
            [Sts2IntentGlyphFold.GlyphRelPath] = Channels.IntentGlyphTexture,
        },
        // The end-turn button's `GlowVfx`, whose infinite scale+alpha pulse runs precisely while the combat
        // is idle-waiting on the player. See Sts2EndTurnGlowFold.
        [Sts2EndTurnGlowFold.SceneFile] = new(StringComparer.Ordinal)
        {
            [Sts2EndTurnGlowFold.GlowVfxRelPath] = Channels.EndTurnGlowPulse,
        },
    };

    // The six energy/star counter scenes, whose `%RotationLayers` children spin forever. Built from
    // Sts2OrbSpinFold's own table (one entry per spinning layer) so the scene paths live in ONE place, and merged
    // into `Rules` by the static constructor below rather than spelled out here: the layer sets differ per counter
    // (necrobinder has a single layer; the star counter nests + names its two differently), and duplicating that
    // here is exactly the drift the fold-owns-its-paths convention exists to prevent.
    static Sts2DecorEmitSuppress()
    {
        Merge(Sts2OrbSpinFold.LayerPathsByScene, Channels.OrbSpinRotation);
        // The creature spine anchors that position ONLY particle emitters, from Sts2SpineAnchorFold's own
        // table for the same single-source reason (41 creature scenes, 87 anchors, derived mechanically from the
        // authored `.tscn` files — see the fold for the derivation predicate and why childless anchors are absent).
        Merge(Sts2SpineAnchorFold.EmitterAnchorPathsByScene, Channels.SpineAnchorTransform);
    }

    private static void Merge(IReadOnlyDictionary<string, IReadOnlyList<string>> pathsByScene, Channels channel)
    {
        foreach (var (sceneFile, relPaths) in pathsByScene)
        {
            if (!Rules.TryGetValue(sceneFile, out var byPath))
            {
                byPath = new Dictionary<string, Channels>(StringComparer.Ordinal);
                Rules[sceneFile] = byPath;
            }

            foreach (var relPath in relPaths)
            {
                byPath[relPath] = byPath.TryGetValue(relPath, out var existing)
                    ? existing | channel
                    : channel;
            }
        }
    }

    /// <summary>
    /// True when <paramref name="sceneFilePath"/> is an instanced-scene root the table has rules for. The watcher
    /// calls this at every scene root so the (allocating) scene-relative path build below only ever runs inside one
    /// of the handful of tiny watched subtrees.
    /// </summary>
    internal static bool IsWatchedScene(string? sceneFilePath)
        => sceneFilePath is not null && Rules.ContainsKey(sceneFilePath);

    /// <summary>Channels folded for one node of a watched scene; <see cref="Channels.None"/> when unlisted.</summary>
    internal static Channels Lookup(string? sceneFilePath, string? relPath)
        => sceneFilePath is not null
           && relPath is not null
           && Rules.TryGetValue(sceneFilePath, out var byPath)
           && byPath.TryGetValue(relPath, out var channels)
            ? channels
            : Channels.None;

    /// <summary>
    /// Compose a child's scene-relative path from its parent's. Returns null past <see cref="MaxRelDepth"/> (the
    /// caller then treats the node as unwatched, which also stops the recursion from building deeper strings).
    /// </summary>
    internal static string? ChildRelPath(string? parentRelPath, string childName)
    {
        if (parentRelPath is null)
        {
            return null;
        }

        var composed = parentRelPath == RootRelPath ? childName : parentRelPath + "/" + childName;
        // Segment count == slashes + 1 (the root "." is depth 0 and never counted).
        var segments = 1;
        for (var i = 0; i < composed.Length; i++)
        {
            if (composed[i] == '/')
            {
                segments++;
            }
        }

        return segments <= MaxRelDepth ? composed : null;
    }
}
