using System.Collections;
using System.Reflection;
using Godot;
using Spirectl.Sts2.Core.SceneInspection;

namespace Spirectl.Sts2.Live;

// Resolves an enemy-intent glyph's animation frame set for the live "mirror" (see
// RuntimeSceneNodeDelta.IntentFrames). The headless client FREEZES each NIntent (ProcessMode.Disabled) to save
// CPU, which stops the intent's per-frame tick — the ONLY thing that swaps the glyph Sprite2D's `.Texture` (a
// 15fps loop over a preloaded frame list). So a streamed single texture is stuck on whichever frame the node
// froze on AND never updates on intent change. Instead we stream the whole ordered frame set (as resolved
// whole-atlas crops) and the browser reproduces the animation off its own wall-clock.
//
// The current intent animation lives in NIntent's `_animationName`, and its resolved frames in the private
// `_animationFrames` (List<Texture2D>) — BOTH refreshed from a combat-state SIGNAL handler rather than from the
// per-frame tick (signal callbacks are NOT gated by ProcessMode), so they stay CURRENT even while the node is
// frozen. We read the already-loaded `_animationFrames` (AtlasTextures the host has already resolved) rather than
// re-loading the intent's frame resources by path — same ordered result, no ResourceLoader round-trip, and
// guaranteed to match what is on screen. Each AtlasTexture is emitted as its underlying atlas PAGE path + region/margin
// (identical resolution to the watcher's ReadPrimaryTexture), so the client crops from the whole page like every
// other atlas sprite. Generic: matched by node TYPE (NIntent), never a specific enemy/intent.
internal static class Sts2IntentFramesInspector
{
    // The flip-book's texture-swap rate, whether or not the node's per-frame tick is frozen.
    private const int IntentAnimationFps = 15;

    // Cached reflection for the private `_animationFrames` field. Every NIntent is the same managed Type, so one
    // lookup serves all instances. `_fieldResolved` guards the (possibly-null) result so a miss isn't re-probed.
    private static FieldInfo? _animationFramesField;
    private static bool _fieldResolved;

    public static bool IsIntentNode(Node node)
    {
        try
        {
            return node.GetType().Name == "NIntent";
        }
        catch
        {
            return false;
        }
    }

    // Resolve the intent glyph (the `%Intent` Sprite2D = NIntent._intentSprite) instance id and its current frame
    // set. Returns false when `node` isn't a readable intent (not an NIntent, no sprite, or a probe threw). Returns
    // true with `frames = null` when the glyph is known but there's no animation to stream yet (no `_animationName`
    // / empty frame list) — the caller stashes nothing in that case.
    public static bool TryRead(Node node, out ulong glyphInstanceId, out RuntimeSceneIntentFramesSnapshot? frames)
    {
        glyphInstanceId = 0;
        frames = null;
        try
        {
            // The glyph Sprite2D whose `.Texture` the flip-book cycles — the flat-DOM node the client animates.
            var spriteVar = node.Get("_intentSprite");
            if (spriteVar.VariantType != Variant.Type.Object
                || spriteVar.AsGodotObject() is not { } sprite
                || !GodotObject.IsInstanceValid(sprite))
            {
                return false;
            }
            glyphInstanceId = sprite.GetInstanceId();

            var animVar = node.Get("_animationName");
            var animName = animVar.VariantType == Variant.Type.String ? animVar.AsString() : null;
            if (string.IsNullOrEmpty(animName))
            {
                return true; // glyph resolved, but no intent animation set yet
            }

            var frameTextures = ReadAnimationFrames(node);
            if (frameTextures is null || frameTextures.Count == 0)
            {
                return true;
            }

            var resolved = new List<RuntimeSceneIntentFrameSnapshot>(frameTextures.Count);
            foreach (var texture in frameTextures)
            {
                if (ResolveFrame(texture) is { } frame)
                {
                    resolved.Add(frame);
                }
            }
            if (resolved.Count == 0)
            {
                return true;
            }

            frames = new RuntimeSceneIntentFramesSnapshot(animName!, IntentAnimationFps, resolved);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // The current animation's ordered frame textures, read from NIntent's private `_animationFrames` list (already
    // loaded + kept current by UpdateVisuals off the CombatStateChanged signal, so valid while the node is frozen).
    private static IReadOnlyList<Texture2D>? ReadAnimationFrames(Node node)
    {
        if (!_fieldResolved)
        {
            _fieldResolved = true;
            _animationFramesField = node.GetType()
                .GetField("_animationFrames", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        if (_animationFramesField?.GetValue(node) is not IEnumerable enumerable)
        {
            return null;
        }

        var frames = new List<Texture2D>();
        foreach (var item in enumerable)
        {
            if (item is Texture2D texture)
            {
                frames.Add(texture);
            }
        }
        return frames;
    }

    // Resolve one frame texture to a whole-atlas crop, mirroring Sts2RuntimeSceneWatcher.ReadPrimaryTexture: an
    // AtlasTexture becomes its underlying atlas PAGE path + region/margin (the client crops that page); a plain
    // texture (unexpected for intents) falls back to the whole image with no region. Null when unusable.
    private static RuntimeSceneIntentFrameSnapshot? ResolveFrame(Texture2D texture)
    {
        if (texture is AtlasTexture { Atlas: { } atlas } atlasTexture
            && !string.IsNullOrWhiteSpace(atlas.ResourcePath))
        {
            return new RuntimeSceneIntentFrameSnapshot(
                AtlasPath: atlas.ResourcePath,
                Region: ToRect(atlasTexture.Region),
                Margin: ToRect(atlasTexture.Margin));
        }

        if (!string.IsNullOrWhiteSpace(texture.ResourcePath))
        {
            return new RuntimeSceneIntentFrameSnapshot(texture.ResourcePath, Region: null, Margin: null);
        }

        return null;
    }

    private static RuntimeSceneRect2Snapshot ToRect(Rect2 rect) => new(
        new RuntimeSceneVector2Snapshot(rect.Position.X, rect.Position.Y),
        new RuntimeSceneVector2Snapshot(rect.Size.X, rect.Size.Y));
}
