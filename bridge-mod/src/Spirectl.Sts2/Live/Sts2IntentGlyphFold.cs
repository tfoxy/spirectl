using System;
using Spirectl.Sts2.Core.SceneInspection;

namespace Spirectl.Sts2.Live;

// R14 producer ENEMY-INTENT GLYPH FLIP-BOOK FOLD — PURE and Godot-free (like Sts2IntentBobFold, whose scene it
// shares) so the substitution is unit-testable without the Godot-coupled Sts2RuntimeSceneWatcher.
//
// WHY. An enemy intent animates TWO ways at once: the holder bobs (Sts2IntentBobFold) and the glyph itself runs a
// 15fps flip-book, cycling through the frames of its current intent animation forever. Every frame swap is a
// same-page atlas texture with a different crop, so on the wire it is a `TextureRegion` (+ Margin) delta ~15x/s per
// enemy. Folding the bob without folding this would leave the intent subtree churning anyway.
//
// THIS FOLD EXTENDS THE EXISTING INTENT-FRAMES CONTRACT — it does not invent one. The producer ALREADY streams the
// whole ordered frame set for the current animation on the glyph node (`RuntimeSceneNodeDelta.IntentFrames`, built
// by Sts2IntentFramesInspector off the intent's current animation name and frame list, re-shipped only when the
// ANIMATION changes) precisely because a headless host freezes the flip-book and a single streamed texture would
// stick. The mirror client consumes that set: it force-overrides the node's `textureUrl`/`textureRegion`/
// `textureMargin` with FRAME 0 (`applyIntentFrame0` in `sceneTree.ts`) and cycles the rest itself, today through a
// pre-rendered strip canvas stepped by a compositor-only CSS `steps(N)` animation (`intentStrip.ts`).
//
// So the per-frame texture the producer streams for such a glyph is already DISCARDED client-side: it is pure wire
// and pure change-detector noise. The fold makes the producer say what the client already computes — substitute
// FRAME 0 of the set it is streaming anyway.
//
// WHY FRAME 0 IS ANALYTIC, NOT A SAMPLE. It is the first frame of the intent animation currently set on the node —
// the frame the flip-book starts every animation on, and the one it shows at t = 0. It changes when, and only when,
// the animation changes, which is exactly the
// event that re-ships the frame set. Nothing is remembered across ticks.
//
// GATED ON HAVING A FRAME SET, which doubles as the fold's own safety: with no set (a glyph whose intent has not
// resolved yet, a non-intent Sprite2D) the live texture streams untouched, and a client that ignores `IntentFrames`
// still sees a valid — if static — glyph rather than a blank one.
//
// KNOWN COST, accepted (kill switches: SPIRECTL_INTENT_GLYPH_FOLD=0, or the master SPIRECTL_DECOR_EMIT_SUPPRESS=0):
// a client that does not consume `IntentFrames` renders each intent's first frame instead of the flip-book. Note it
// was ALREADY effectively frozen for such a client on a headless host (the flip-book does not advance there), and
// on a live host it now freezes at a defined frame rather than a race-dependent one.
internal static class Sts2IntentGlyphFold
{
    /// <summary>The instanced enemy-intent scene — the same one the bob fold matches.</summary>
    internal const string SceneFile = Sts2IntentBobFold.SceneFile;

    /// <summary>
    /// Scene-relative path of the flip-book glyph. It is a CHILD of the bobbing holder, so the two folds sit at
    /// depth 1 and 2 of the same watched scene.
    /// </summary>
    internal const string GlyphRelPath = Sts2IntentBobFold.HolderRelPath + "/Intent";

    /// <summary>The rate the flip-book cycles at when it is not folded or frozen.</summary>
    internal const int AnimationFps = 15;

    /// <summary>True when this scene identity is the enemy-intent flip-book glyph.</summary>
    internal static bool IsGlyph(string? sceneFilePath, string? relPath)
        => string.Equals(sceneFilePath, SceneFile, StringComparison.Ordinal)
           && string.Equals(relPath, GlyphRelPath, StringComparison.Ordinal);

    /// <summary>
    /// Substitute FRAME 0 of <paramref name="frames"/> for the glyph's live per-frame crop. Returns the inputs
    /// unchanged (no allocation) when there is no usable frame set, when the live texture is absent, or when the
    /// live crop already IS frame 0 — the steady state once the fold has taken, i.e. most ticks.
    /// </summary>
    /// <param name="liveTexture">The texture ref as read this tick; only its <c>ResourcePath</c> is substituted, so
    /// the ref's field/type/name stay exactly what <c>ReadPrimaryTexture</c> produced.</param>
    internal static void PinFrameZero(
        RuntimeSceneIntentFramesSnapshot? frames,
        ref RuntimeSceneResourceRefSnapshot? liveTexture,
        ref RuntimeSceneRect2Snapshot? liveRegion,
        ref RuntimeSceneRect2Snapshot? liveMargin)
    {
        if (frames is null || frames.Frames.Count == 0 || liveTexture is null)
        {
            return;
        }

        var frame0 = frames.Frames[0];
        if (string.IsNullOrEmpty(frame0.AtlasPath))
        {
            return;
        }

        if (!string.Equals(liveTexture.ResourcePath, frame0.AtlasPath, StringComparison.Ordinal))
        {
            liveTexture = liveTexture with { ResourcePath = frame0.AtlasPath };
        }

        liveRegion = frame0.Region;
        liveMargin = frame0.Margin;
    }
}
