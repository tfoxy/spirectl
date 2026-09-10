using Godot;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2;
using Spirectl.Sts2.Live.EncounterVisuals;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Spirectl.Sts2.Live;

public sealed partial class Sts2AssetExtractProvider
{
    // `phase` names which PARKED phase these frames belong to (Sts2RenderPhaseProfile): every caller waits on the
    // same signal, but a warmup, a post-resize settle and a per-batch capture wait are different costs to a
    // reader. The signal wait is parked (the game runs its own frame during it); the ForceDraw that closes each
    // one is blocking, so the two are recorded apart rather than as one "waited for frames" bar.
    private async Task AwaitRenderWarmupFramesAsync(
        Viewport rootViewport,
        int frameCount,
        string phase = Sts2RenderPhaseProfile.Phase.WarmupWait)
    {
        if (frameCount <= 0)
        {
            return;
        }

        var tree = rootViewport.GetTree();
        if (tree is null)
        {
            for (var index = 0; index < frameCount; index += 1)
            {
                using var drawScope = Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.WarmupDraw);
                RenderingServer.ForceDraw();
            }
            return;
        }

        for (var index = 0; index < frameCount; index += 1)
        {
            using (Sts2RenderPhaseProfile.Measure(phase, blocking: false))
            {
                await rootViewport.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }

            Sts2RenderPhaseProfile.Count(Sts2RenderPhaseProfile.Counter.FramesWaited);
            using var drawScope = Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.WarmupDraw);
            RenderingServer.ForceDraw();
        }
    }

    private sealed record StandaloneSkeletonPreviewDefaults(
        string AnimationName,
        string SkinName,
        Vector2 Scale,
        string MaterialDescription,
        Rect2 Bounds,
        bool UsesSyntheticBounds);

    // Internal rather than private because FrameFromBoundsFitted (below) is now called by the geoclip baker,
    // which needs the same framing the raster clip lane uses; a private return type would be less accessible
    // than the method.
    internal readonly record struct SceneRenderFrame(
        Vector2I ViewportSize,
        Vector2 NodePosition,
        Vector2 NodeScale,
        Vector2 PivotOffset,
        Vector2 ControlSize,
        bool PreserveControlSize = false)
    {
        public SceneRenderFrame(Vector2I viewportSize, Vector2 nodePosition)
            : this(viewportSize, nodePosition, Vector2.One, Vector2.Zero, Vector2.Zero)
        {
        }
    }

    private static StandaloneSkeletonPreviewDefaults ResolveStandaloneSkeletonPreviewDefaults(Resource skeletonData)
    {
        var animationName = PreferName(
            ReadStringCandidates(skeletonData, ["animation_names", "AnimationNames", "animations", "Animations", "GetAnimationNames"]),
            ["idle_loop", "idle", "Idle"]);
        if (string.IsNullOrWhiteSpace(animationName))
        {
            animationName = "idle_loop";
        }

        var skinName = PreferName(
            ReadStringCandidates(skeletonData, ["skin_names", "SkinNames", "skins", "Skins", "GetSkinNames", "GetSkins"]),
            ["default", "Default"]);
        var bounds = ResolveStandaloneSkeletonBounds(skeletonData, out var usesSyntheticBounds);

        return new StandaloneSkeletonPreviewDefaults(
            AnimationName: animationName,
            SkinName: skinName,
            Scale: Vector2.One,
            MaterialDescription: "runtime/default",
            Bounds: bounds,
            UsesSyntheticBounds: usesSyntheticBounds);
    }

    private static Rect2 ResolveStandaloneSkeletonBounds(Resource skeletonData, out bool synthetic)
    {
        if (TryGetRectValue(skeletonData, "bounds", out var bounds)
            || TryGetRectValue(skeletonData, "Bounds", out bounds)
            || TryGetRectValue(skeletonData, "aabb", out bounds)
            || TryGetRectValue(skeletonData, "Aabb", out bounds))
        {
            synthetic = false;
            return bounds;
        }

        synthetic = true;
        var half = SkeletonPreviewBoundsSize / 2f;
        return new Rect2(-half, -half, SkeletonPreviewBoundsSize, SkeletonPreviewBoundsSize);
    }

    private bool TryCreateStandaloneSkeletonPreviewNode(
        Resource skeletonData,
        AssetExtractRequestSnapshot request,
        out Node previewNode,
        out StandaloneSkeletonPreviewDefaults defaults,
        out IReadOnlyList<string> notes)
    {
        previewNode = null!;
        defaults = ResolveStandaloneSkeletonPreviewDefaults(skeletonData);

        // When the model alias resolution identified an authoritative skin (e.g. an act
        // chest's normal skin), bake only that skin so the stroke/outline halo is excluded
        // and the bounds stay tight. Record it on defaults so the post-attach re-apply
        // (which rebuilds on _ready) uses the same skin.
        if (!string.IsNullOrWhiteSpace(request.PreferredSkinName))
        {
            defaults = defaults with { SkinName = request.PreferredSkinName! };
        }
        var preferredSkinName = string.IsNullOrWhiteSpace(request.PreferredSkinName) ? null : request.PreferredSkinName;

        var noteList = new List<string>
        {
            "Rendered standalone skeleton data through a synthetic Spine preview node; composed scenes remain the preferred exact in-game visual path.",
        };

        if (TryInstantiateSpineSpriteNode(noteList) is not Node node)
        {
            notes = noteList;
            return false;
        }

        node.Name = "SpirectlSkeletonPreview";
        if (node is Node2D node2D)
        {
            node2D.Scale = defaults.Scale;
        }

        // Drive the skeleton through the game's own MegaSpine bindings: apply the
        // skins (a spine with no skin has no slot attachments and renders fully
        // transparent — the treasure chest's failure mode) and pin the setup pose.
        // Fall back to the legacy dynamic-property preview path only if that fails.
        if (TryApplySpineSkinAndPose(node, skeletonData, noteList, out var realBounds, preferredSkinName))
        {
            if (realBounds.Size.X > 1f && realBounds.Size.Y > 1f)
            {
                defaults = defaults with { Bounds = realBounds, UsesSyntheticBounds = false };
            }
        }
        else
        {
            if (!TrySetDynamicValue(node, "skeleton_data_res", skeletonData))
            {
                node.QueueFree();
                notes = noteList;
                return false;
            }

            TrySetDynamicValue(node, "preview_frame", true);
            TrySetDynamicValue(node, "preview_time", 0d);
            TrySetDynamicValue(node, "preview_animation", defaults.AnimationName);
            TrySetDynamicValue(node, "preview_skin", defaults.SkinName);
            TrySetDynamicValue(node, "skin", defaults.SkinName);
            TrySetDynamicValue(node, "visible", true);
            TryInvokeAnimationMethod(node, defaults.AnimationName);
            TryInvokeSkinMethod(node, defaults.SkinName);
        }

        if (node is CanvasItem canvasItem)
        {
            canvasItem.Visible = true;
            canvasItem.QueueRedraw();
        }

        noteList.Add(
            $"Applied deterministic skeleton preview defaults: skin '{DisplayDefault(defaults.SkinName)}', animation '{defaults.AnimationName}', scale {defaults.Scale.X:0.###}x{defaults.Scale.Y:0.###}, material '{defaults.MaterialDescription}', bounds {defaults.Bounds}.");
        if (defaults.UsesSyntheticBounds)
        {
            noteList.Add(
                "Skeleton data alone does not define authored scene placement; preview bounds are synthetic and may differ from composed in-game scenes.");
        }

        previewNode = node;
        notes = noteList;
        return true;
    }

    private static SceneRenderFrame ResolveSceneFrame(Node node)
    {
        return TryFindAuthoredBounds(node, out var bounds)
            ? FrameFromBounds(bounds)
            : new SceneRenderFrame(
                ResolveViewportSize(node),
                new Vector2(Padding, Padding));
    }

    private static SceneRenderFrame FrameFromBounds(Rect2 bounds)
    {
        var width = (int)MathF.Ceiling(Math.Clamp(bounds.Size.X, 1, MaxSceneSize)) + (Padding * 2);
        var height = (int)MathF.Ceiling(Math.Clamp(bounds.Size.Y, 1, MaxSceneSize)) + (Padding * 2);
        return new SceneRenderFrame(
            new Vector2I(width, height),
            new Vector2(Padding - bounds.Position.X, Padding - bounds.Position.Y));
    }

    // Like FrameFromBounds, but for content larger than MaxSceneSize it uniformly scales the
    // node down to fit (via NodeScale) instead of clamping each dimension independently.
    // The per-dimension clamp keeps a fixed-size viewport, so a large rig (e.g. the treasure
    // chest, ~4791x2340 with a large negative offset) overflows the right/bottom and is
    // CROPPED; scaling to fit captures the whole skeleton. Scoped to the standalone-skeleton
    // bake so other render paths keep their existing framing.
    //
    // A THIN Rect2 WRAPPER over Sts2SceneFitFrame.Fit, which holds the arithmetic. INTERNAL because the geoclip
    // baker calls it too: a geoclip states its placement in its manifest, and the only way that placement can be
    // trusted to agree with the raster clip's is for both to come out of this one function on the same bounds.
    internal static SceneRenderFrame FrameFromBoundsFitted(Rect2 bounds)
    {
        var fitted = Sts2SceneFitFrame.Fit(bounds.Position.X, bounds.Position.Y, bounds.Size.X, bounds.Size.Y);
        return new SceneRenderFrame(
            new Vector2I(fitted.ViewportWidth, fitted.ViewportHeight),
            new Vector2(fitted.NodePositionX, fitted.NodePositionY),
            new Vector2(fitted.NodeScale, fitted.NodeScale),
            Vector2.Zero,
            Vector2.Zero);
    }

    // The placement arithmetic lives in Sts2EventBackgroundFrameMath (Godot-free, unit-tested — the
    // Sts2SceneFitFrame precedent). `mirrorCentered` selects the EXPLICIT-SIZE composition: the 16:9 reference
    // frame translated to the requested viewport's center, which is what the web mirror composes on a widened
    // stage — the default path keeps the game's own aspect lerp and stays byte-identical.
    private static bool TryResolveEventBackgroundFrame(
        AssetExtractRequestSnapshot request,
        Vector2I rootViewportSize,
        bool mirrorCentered,
        out SceneRenderFrame frame)
    {
        var sourcePath = NormalizeResourcePath(request.SourcePath);
        var loadPath = NormalizeResourcePath(request.LoadPath);
        if (!IsEventBackgroundScenePath(sourcePath) && !IsEventBackgroundScenePath(loadPath))
        {
            frame = default;
            return false;
        }

        var viewportSize = new Vector2I(
            Math.Max(1, rootViewportSize.X),
            Math.Max(1, rootViewportSize.Y));
        var viewport = new Vector2(viewportSize.X, viewportSize.Y);
        // A probed LIVE frame (request.EventBackgroundFrame) outranks the reference lerp: the lerp PREDICTS the
        // game's placement and the shipped constants have drifted from it, while the caller's probe MEASURED it.
        var resolved = mirrorCentered
            ? Sts2EventBackgroundFrameMath.CenterFrame(
                Sts2EventBackgroundFrameMath.TryParseFrameSpec(request.EventBackgroundFrame)
                    ?? Sts2EventBackgroundFrameMath.Resolve(
                        Sts2EventBackgroundFrameMath.ReferenceWidth,
                        Sts2EventBackgroundFrameMath.ReferenceHeight),
                viewport.X,
                viewport.Y)
            : Sts2EventBackgroundFrameMath.Resolve(viewport.X, viewport.Y);
        frame = new SceneRenderFrame(
            viewportSize,
            new Vector2(resolved.PositionX, resolved.PositionY),
            new Vector2(resolved.Scale, resolved.Scale),
            Vector2.Zero,
            viewport);
        return true;
    }

    private static bool IsEventBackgroundScenePath(string normalizedResourcePath)
    {
        if (!normalizedResourcePath.StartsWith(
            EventBackgroundScenePrefix,
            StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return normalizedResourcePath.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase)
            || normalizedResourcePath.EndsWith(".escn", StringComparison.OrdinalIgnoreCase)
            || normalizedResourcePath.EndsWith(".scn", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCharacterSelectBackgroundScenePath(string normalizedResourcePath)
    {
        const string prefix = "res://scenes/screens/char_select/char_select_bg_";
        if (!normalizedResourcePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return normalizedResourcePath.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase)
            || normalizedResourcePath.EndsWith(".escn", StringComparison.OrdinalIgnoreCase)
            || normalizedResourcePath.EndsWith(".scn", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveCharacterSelectBackgroundFrame(
        AssetExtractRequestSnapshot request,
        Vector2I rootViewportSize,
        out SceneRenderFrame frame)
    {
        var sourcePath = NormalizeResourcePath(request.SourcePath);
        var loadPath = NormalizeResourcePath(request.LoadPath);
        if (!IsCharacterSelectBackgroundScenePath(sourcePath) && !IsCharacterSelectBackgroundScenePath(loadPath))
        {
            frame = default;
            return false;
        }

        var viewportSize = new Vector2I(
            Math.Max(1, rootViewportSize.X),
            Math.Max(1, rootViewportSize.Y));
        frame = new SceneRenderFrame(
            viewportSize,
            Vector2.Zero,
            Vector2.One,
            Vector2.Zero,
            new Vector2(viewportSize.X, viewportSize.Y));
        return true;
    }

    private static void HideNodesOutsideCapturedSubtree(Node root, Node capturedSubtree)
    {
        foreach (var node in EnumerateNodes(root))
        {
            if (node is not CanvasItem canvasItem)
            {
                continue;
            }

            if (IsNodeWithin(node, capturedSubtree) || IsNodeAncestorOf(capturedSubtree, node))
            {
                continue;
            }

            canvasItem.Visible = false;
        }
    }

    private static bool TryInvokeDynamicMethod(
        object target,
        IReadOnlyList<string> methodNames,
        object?[] arguments,
        out object? result)
    {
        foreach (var methodName in methodNames)
        {
            var method = target.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, methodName, StringComparison.OrdinalIgnoreCase)
                    && TryConvertDynamicArguments(arguments, candidate.GetParameters(), out _));
            if (method is null)
            {
                continue;
            }

            try
            {
                TryConvertDynamicArguments(arguments, method.GetParameters(), out var converted);
                result = method.Invoke(target, converted);
                return true;
            }
            catch
            {
            }
        }

        if (TryInvokeGodotObjectMethod(target as GodotObject, methodNames, arguments, out result))
        {
            return true;
        }

        if (TryGetDynamicValue(target, "BoundObject", out var boundObject)
            && TryInvokeGodotObjectMethod(boundObject as GodotObject, methodNames, arguments, out result))
        {
            return true;
        }

        result = null;
        return false;
    }

    private static bool TryInvokeGodotObjectMethod(
        GodotObject? godotObject,
        IReadOnlyList<string> methodNames,
        object?[] arguments,
        out object? result)
    {
        if (godotObject is null)
        {
            result = null;
            return false;
        }

        foreach (var methodName in methodNames)
        {
            if (!godotObject.HasMethod(methodName))
            {
                continue;
            }

            try
            {
                var variant = godotObject.Call(methodName, arguments.Select(ToVariant).ToArray());
                result = ToObject(variant);
                return true;
            }
            catch
            {
            }
        }

        result = null;
        return false;
    }

    private static bool TryConvertDynamicArguments(
        IReadOnlyList<object?> arguments,
        IReadOnlyList<ParameterInfo> parameters,
        out object?[] converted)
    {
        converted = new object?[arguments.Count];
        if (arguments.Count != parameters.Count)
        {
            return false;
        }

        for (var index = 0; index < arguments.Count; index += 1)
        {
            if (!TryConvertDynamicValue(arguments[index], parameters[index].ParameterType, out converted[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static object? ToObject(Variant variant)
        => variant.VariantType switch
        {
            Variant.Type.Nil => null,
            Variant.Type.String => variant.AsString().ToString(),
            Variant.Type.StringName => variant.AsStringName().ToString(),
            Variant.Type.Bool => variant.AsBool(),
            Variant.Type.Float => variant.AsDouble(),
            Variant.Type.Int => variant.AsInt64(),
            Variant.Type.Object => variant.AsGodotObject(),
            _ => variant.ToString(),
        };

    private static int HideCharacterSelectBgSpineStillEffectNodes(Node root)
    {
        var hiddenCount = 0;
        foreach (var node in EnumerateNodes(root))
        {
            if (!IsParticleOrLightEffectNode(node))
            {
                continue;
            }

            TrySetDynamicValue(node, "emitting", false);
            if (node is CanvasItem canvasItem)
            {
                canvasItem.Visible = false;
            }

            hiddenCount += 1;
        }

        return hiddenCount;
    }

    private static bool IsParticleOrLightEffectNode(Node node)
    {
        var typeName = node.GetType().Name;
        return typeName.Contains("Particles2D", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Light2D", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNodeAncestorOf(Node descendant, Node possibleAncestor)
    {
        for (Node? current = descendant.GetParent(); current is not null; current = current.GetParent())
        {
            if (ReferenceEquals(current, possibleAncestor))
            {
                return true;
            }
        }

        return false;
    }

    private static int CountCpuParticlesOutside(Node root, Node capturedSubtree)
        => EnumerateNodes(root).Count(node => node is CpuParticles2D && !IsNodeWithin(node, capturedSubtree));

    private static bool IsNodeWithin(Node node, Node ancestor)
    {
        for (Node? current = node; current is not null; current = current.GetParent())
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveCharacterSelectBgSpineStillFrame(
        Node spineNode,
        out SceneRenderFrame frame,
        out Rect2 localBounds,
        out Rect2 globalBounds,
        out string boundsSource)
    {
        if (TryFindAuthoredBounds(spineNode, out localBounds))
        {
            boundsSource = "authored";
        }
        else if (TryGetRectValue(spineNode, "bounds", out localBounds)
            || TryGetRectValue(spineNode, "Bounds", out localBounds)
            || TryGetRectValue(spineNode, "aabb", out localBounds)
            || TryGetRectValue(spineNode, "Aabb", out localBounds))
        {
            boundsSource = "spine";
        }
        else
        {
            var half = SkeletonPreviewBoundsSize / 2f;
            localBounds = new Rect2(-half, -half, SkeletonPreviewBoundsSize, SkeletonPreviewBoundsSize);
            boundsSource = "diagnostic";
        }

        globalBounds = spineNode is CanvasItem canvasItem
            ? TransformRect(canvasItem.GetGlobalTransform(), localBounds)
            : localBounds;
        if (TryResolveSubtreeCanvasBounds(spineNode, out var subtreeLocalBounds, out var subtreeGlobalBounds))
        {
            localBounds = UnionRects(localBounds, subtreeLocalBounds);
            globalBounds = UnionRects(globalBounds, subtreeGlobalBounds);
        }

        frame = FrameFromBounds(localBounds);
        return true;
    }

    private static bool TryResolveSubtreeCanvasBounds(Node root, out Rect2 localBounds, out Rect2 globalBounds)
    {
        var rootInverse = root is CanvasItem rootCanvasItem
            ? rootCanvasItem.GetGlobalTransform().AffineInverse()
            : Transform2D.Identity;
        Rect2? localUnion = null;
        Rect2? globalUnion = null;
        foreach (var node in EnumerateNodes(root))
        {
            if (!TryResolveCanvasGlobalBounds(node, out var nodeGlobalBounds))
            {
                continue;
            }

            var nodeLocalBounds = TransformRect(rootInverse, nodeGlobalBounds);
            localUnion = localUnion is null ? nodeLocalBounds : UnionRects(localUnion.Value, nodeLocalBounds);
            globalUnion = globalUnion is null ? nodeGlobalBounds : UnionRects(globalUnion.Value, nodeGlobalBounds);
        }

        localBounds = localUnion ?? default;
        globalBounds = globalUnion ?? default;
        return localUnion is not null && globalUnion is not null;
    }

    private static bool TryResolveCanvasGlobalBounds(Node node, out Rect2 bounds)
    {
        switch (node)
        {
            case Control control when control.Size.X > 0 && control.Size.Y > 0:
                bounds = TransformRect(control.GetGlobalTransform(), new Rect2(Vector2.Zero, control.Size));
                return true;
            case Sprite2D { Texture: not null } sprite:
                var spriteSize = sprite.Texture.GetSize();
                var spriteRect = sprite.Centered
                    ? new Rect2(sprite.Offset - (spriteSize / 2f), spriteSize)
                    : new Rect2(sprite.Offset, spriteSize);
                bounds = TransformRect(sprite.GetGlobalTransform(), spriteRect);
                return true;
            default:
                bounds = default;
                return false;
        }
    }

    private static Rect2 UnionRects(Rect2 left, Rect2 right)
    {
        var minX = MathF.Min(left.Position.X, right.Position.X);
        var minY = MathF.Min(left.Position.Y, right.Position.Y);
        var maxX = MathF.Max(left.Position.X + left.Size.X, right.Position.X + right.Size.X);
        var maxY = MathF.Max(left.Position.Y + left.Size.Y, right.Position.Y + right.Size.Y);
        return new Rect2(minX, minY, MathF.Max(0, maxX - minX), MathF.Max(0, maxY - minY));
    }

    private static Rect2 TransformRect(Transform2D transform, Rect2 rect)
    {
        var p0 = transform * rect.Position;
        var p1 = transform * new Vector2(rect.Position.X + rect.Size.X, rect.Position.Y);
        var p2 = transform * new Vector2(rect.Position.X, rect.Position.Y + rect.Size.Y);
        var p3 = transform * (rect.Position + rect.Size);
        var minX = MathF.Min(MathF.Min(p0.X, p1.X), MathF.Min(p2.X, p3.X));
        var minY = MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y));
        var maxX = MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X));
        var maxY = MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y));
        return new Rect2(minX, minY, MathF.Max(0, maxX - minX), MathF.Max(0, maxY - minY));
    }

    private static string NodePathWithin(Node root, Node target)
    {
        var names = new Stack<string>();
        for (Node? current = target; current is not null; current = current.GetParent())
        {
            names.Push(current.Name.ToString());
            if (ReferenceEquals(current, root))
            {
                return "/" + string.Join("/", names);
            }
        }

        return target.GetPath().ToString();
    }

    private static Control CreateCharacterSelectBackgroundRenderRoot(Node backgroundSceneRoot, Vector2I viewportSize)
    {
        var viewport = new Vector2(Math.Max(1, viewportSize.X), Math.Max(1, viewportSize.Y));
        var root = new Control
        {
            Name = "SpirectlCharacterSelectBackgroundRoot",
            Size = viewport,
            CustomMinimumSize = viewport,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };

        var container = new Control
        {
            Name = "AnimatedBg",
            Position = new Vector2(CharacterSelectBackgroundOffsetX, CharacterSelectBackgroundOffsetY),
            Size = new Vector2(viewport.X + 640f, viewport.Y + 120f),
            Scale = new Vector2(CharacterSelectBackgroundScale, CharacterSelectBackgroundScale),
            PivotOffset = new Vector2(CharacterSelectBackgroundWidth * 0.5f, CharacterSelectBackgroundHeight * 0.5f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };

        root.AddChild(container);
        container.AddChild(backgroundSceneRoot);
        return root;
    }

    // R10 CHAR-SELECT OVERSCAN EXTENT — the authored full-bleed rect of a character-select background, expressed
    // in the SYNTHETIC render root's own space (the space CreateCharacterSelectBackgroundRenderRoot builds, which
    // reproduces the live screen 1:1: the container's global transform there is origin(-516,-140) scale 1.1,
    // exactly what `dev scene tree --computed-transform` reports for the live `AnimatedBg`).
    //
    // It is the rendered rect of that container: a Control maps p -> position + pivot + scale*(p - pivot), so
    //   min  = position + pivot*(1 - scale)      = (-516, -140) at a 1920x1080 root viewport
    //   size = size * scale                      = (2816, 1320)
    //
    // WHY THIS EXISTS. The lane used to capture at the bare ROOT VIEWPORT (1920x1080), i.e. a strict SUBSET of
    // the authored rig: ~516px of art on the left and ~380px on the right are simply not in the raster. At 16:9
    // that is invisible (the game clips there too), but a mirror stage wider than 16:9 re-centres the background
    // subtree by the same +0.55*delta the game's own anchors apply, and a 1920-wide still then leaves bare stage
    // on BOTH sides — no client-side placement can conjure pixels the bake never took. Capture over this rect
    // and the client (which re-applies the streamed node transform to the node-LOCAL placement) shows exactly
    // the pixels the game would draw at that width.
    //
    // Computed ANALYTICALLY from the values the render root was built with, not read back off the node: the
    // capture frame has to be known before the tree is mounted, and a freshly instantiated Control's anchor-driven
    // Size/Position are not laid out until it enters the tree.
    private static Rect2 CharacterSelectBackgroundOverscanExtent(Vector2I viewportSize)
    {
        var viewport = new Vector2(Math.Max(1, viewportSize.X), Math.Max(1, viewportSize.Y));
        var containerSize = new Vector2(viewport.X + 640f, viewport.Y + 120f);
        var pivot = new Vector2(CharacterSelectBackgroundWidth * 0.5f, CharacterSelectBackgroundHeight * 0.5f);
        var scale = new Vector2(CharacterSelectBackgroundScale, CharacterSelectBackgroundScale);
        var position = new Vector2(CharacterSelectBackgroundOffsetX, CharacterSelectBackgroundOffsetY);
        var min = position + new Vector2(pivot.X * (1f - scale.X), pivot.Y * (1f - scale.Y));
        return new Rect2(min, new Vector2(containerSize.X * scale.X, containerSize.Y * scale.Y));
    }

    private static bool IsCombatBackgroundScenePath(string normalizedResourcePath)
    {
        if (!normalizedResourcePath.StartsWith(
            CombatBackgroundScenePrefix,
            StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!normalizedResourcePath.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase)
            && !normalizedResourcePath.EndsWith(".escn", StringComparison.OrdinalIgnoreCase)
            && !normalizedResourcePath.EndsWith(".scn", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = normalizedResourcePath[CombatBackgroundScenePrefix.Length..];
        var slash = remainder.IndexOf('/');
        if (slash <= 0 || slash == remainder.Length - 1)
        {
            return false;
        }

        var id = remainder[..slash];
        var fileName = remainder[(slash + 1)..];
        return string.Equals(fileName, $"{id}_background.tscn", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, $"{id}_background.escn", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, $"{id}_background.scn", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveCombatBackgroundFrame(
        AssetExtractRequestSnapshot request,
        Vector2I rootViewportSize,
        out SceneRenderFrame frame)
    {
        var sourcePath = NormalizeResourcePath(request.SourcePath);
        var loadPath = NormalizeResourcePath(request.LoadPath);
        if (!IsCombatBackgroundScenePath(sourcePath) && !IsCombatBackgroundScenePath(loadPath))
        {
            frame = default;
            return false;
        }

        var viewportSize = new Vector2I(
            Math.Max(1, rootViewportSize.X),
            Math.Max(1, rootViewportSize.Y));
        var viewport = new Vector2(viewportSize.X, viewportSize.Y);
        frame = new SceneRenderFrame(
            viewportSize,
            new Vector2((viewport.X * 0.5f) + CombatBackgroundContainerOffsetX, viewport.Y * 0.5f),
            new Vector2(CombatBackgroundContainerScale, CombatBackgroundContainerScale),
            Vector2.Zero,
            Vector2.Zero,
            PreserveControlSize: true);
        return true;
    }

    private static bool ShouldTrimTransparentBounds(string renderMode, bool trimTransparentBounds)
    {
        _ = renderMode;
        return trimTransparentBounds;
    }

    private static int ResolveTransparentCropPadding(string renderMode)
    {
        if (string.Equals(renderMode, ComposedCombatBackgroundRenderMode, StringComparison.Ordinal))
        {
            return 0;
        }

        return SpineCropPadding;
    }

    private static Vector2I ResolveRootViewportSize()
    {
        var rootViewport = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (rootViewport is null)
        {
            return new Vector2I(1920, 1080);
        }

        var size = rootViewport.GetVisibleRect().Size;
        if (size.X <= 0 || size.Y <= 0)
        {
            return rootViewport.Size;
        }

        return new Vector2I(
            Math.Max(1, (int)MathF.Round(size.X)),
            Math.Max(1, (int)MathF.Round(size.Y)));
    }

    private static SceneRenderFrame EncounterViewportFrame(AssetEncounterCameraSnapshot camera)
    {
        var viewportSize = ResolveRootViewportSize();
        var viewport = new Vector2(viewportSize.X, viewportSize.Y);
        var offset = camera.Offset is null
            ? Vector2.Zero
            : new Vector2((float)camera.Offset.X, (float)camera.Offset.Y);
        return new SceneRenderFrame(
            viewportSize,
            (viewport * 0.5f) + offset,
            new Vector2((float)camera.Scale, (float)camera.Scale),
            Vector2.Zero,
            viewport,
            PreserveControlSize: true);
    }

    private static SceneRenderFrame AuthoredViewportFrame()
    {
        var viewportSize = ResolveRootViewportSize();
        var viewport = new Vector2(viewportSize.X, viewportSize.Y);
        return new SceneRenderFrame(
            viewportSize,
            Vector2.Zero,
            Vector2.One,
            Vector2.Zero,
            viewport,
            PreserveControlSize: true);
    }

    // Build a capture frame that covers an authored scene-space rect (e.g. the
    // event background's overscanning cave layers) instead of the bare viewport.
    // The render node is shifted so the extent's top-left maps to the SubViewport
    // origin and the SubViewport is sized to the full extent, so any content below
    // the viewport bottom (a waterfall Spine still) is captured rather than clipped.
    // The container down-scale stays out of the capture; the presentation catalog
    // applies it later by placing the still at the same authored rect.
    private static SceneRenderFrame SpineStillOverscanFrame(Rect2 extent)
    {
        var width = (int)MathF.Ceiling(Math.Max(1f, extent.Size.X));
        var height = (int)MathF.Ceiling(Math.Max(1f, extent.Size.Y));
        var viewportSize = new Vector2I(width, height);
        return new SceneRenderFrame(
            viewportSize,
            -extent.Position,
            Vector2.One,
            Vector2.Zero,
            new Vector2(width, height));
    }

    // Resolve the authored background extent of an event background scene from its
    // overscanning cave layers (the full-rect `TextureRect` Controls). Their authored
    // offsets reach past the viewport on every edge, so a Spine still captured over
    // this extent — and placed at the same rect by the catalog — shares the cave's
    // post-scale coverage and reaches the viewport bottom.
    private static bool TryResolveBackgroundOverscanExtent(Node sceneRoot, out Rect2 extent)
    {
        var hasExtent = false;
        extent = default;
        foreach (var child in sceneRoot.GetChildren())
        {
            if (child is not TextureRect { Texture: not null } textureRect)
            {
                continue;
            }

            var rect = new Rect2(
                textureRect.OffsetLeft,
                textureRect.OffsetTop,
                textureRect.OffsetRight - textureRect.OffsetLeft,
                textureRect.OffsetBottom - textureRect.OffsetTop);
            if (rect.Size.X <= 0 || rect.Size.Y <= 0)
            {
                continue;
            }

            extent = hasExtent ? extent.Merge(rect) : rect;
            hasExtent = true;
        }

        return hasExtent;
    }

    private static string NormalizeResourcePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        if (normalized.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
        {
            return $"res://{normalized["res://".Length..].TrimStart('/')}";
        }

        return $"res://{normalized.TrimStart('/')}";
    }

    private static IReadOnlyList<string> CollectTextureReferences(Node root)
    {
        var refs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var node in EnumerateNodes(root))
        {
            switch (node)
            {
                case Sprite2D { Texture: not null } sprite:
                    AddResourcePath(refs, sprite.Texture.ResourcePath);
                    break;
                case TextureRect { Texture: not null } textureRect:
                    AddResourcePath(refs, textureRect.Texture.ResourcePath);
                    break;
            }
        }

        return refs.ToList();
    }

    private static void AddResourcePath(ISet<string> refs, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            refs.Add(NormalizeResourcePath(path));
        }
    }

    private static Vector2I ResolveViewportSize(Node node)
    {
        var desired = new Vector2(DefaultSceneSize, DefaultSceneSize);

        if (node is Control control)
        {
            desired = control.Size;
            if (desired.X <= 0 || desired.Y <= 0)
            {
                desired = control.GetCombinedMinimumSize();
            }
        }
        else if (node is Sprite2D sprite && sprite.Texture is not null)
        {
            desired = sprite.Texture.GetSize();
        }
        else if (node is TextureRect textureRect && textureRect.Texture is not null)
        {
            desired = textureRect.Texture.GetSize();
        }

        var width = (int)MathF.Ceiling(Math.Clamp(desired.X, 1, MaxSceneSize)) + (Padding * 2);
        var height = (int)MathF.Ceiling(Math.Clamp(desired.Y, 1, MaxSceneSize)) + (Padding * 2);
        return new Vector2I(width, height);
    }

    private static Rect2I ClampRegionToImage(Rect2 region, int imageWidth, int imageHeight)
    {
        var left = Math.Clamp((int)MathF.Floor(region.Position.X), 0, imageWidth);
        var top = Math.Clamp((int)MathF.Floor(region.Position.Y), 0, imageHeight);
        var right = Math.Clamp((int)MathF.Ceiling(region.Position.X + region.Size.X), 0, imageWidth);
        var bottom = Math.Clamp((int)MathF.Ceiling(region.Position.Y + region.Size.Y), 0, imageHeight);
        return new Rect2I(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private async Task<AssetExtractOperationResult?> ExtractSceneTextureAsync(
        Texture2D texture,
        AssetExtractRequestSnapshot request)
    {
        return texture is AtlasTexture atlasTexture
            ? await ExtractAtlasTextureAsync(atlasTexture, request) with
            {
                RenderMode = "flattened-scene-texture",
                Notes = ["Rendered a simple scene preview from its root texture."],
            }
            : await ExtractTextureAsync(
                texture,
                request,
                renderMode: "flattened-scene-texture",
                notes:
                [
                    "Rendered a simple scene preview from its root texture.",
                ]);
    }

    private async Task<AssetExtractOperationResult?> TryExtractSceneRootTextureAsync(
        Node node,
        AssetExtractRequestSnapshot request)
    {
        Texture2D? texture = node switch
        {
            TextureRect textureRect => textureRect.Texture,
            Sprite2D sprite => sprite.Texture,
            _ => null,
        };

        if (texture is not null)
        {
            return await ExtractSceneTextureAsync(texture, request);
        }

        return null;
    }

    private static void PositionCanvasItem(Node node)
    {
        PositionCanvasItem(node, new Vector2(Padding, Padding));
    }

    private static void PositionCanvasItem(Node node, Vector2 position)
    {
        switch (node)
        {
            case Control control:
                if (control.Size.X <= 0 || control.Size.Y <= 0)
                {
                    var minimum = control.GetCombinedMinimumSize();
                    control.Size = minimum.X > 0 && minimum.Y > 0
                        ? minimum
                        : new Vector2(DefaultSceneSize, DefaultSceneSize);
                }
                control.Position = position;
                break;
            case Node2D node2D:
                node2D.Position = position;
                break;
        }
    }

    private static void PositionCanvasItem(Node node, SceneRenderFrame frame)
    {
        switch (node)
        {
            case Control control:
                if (frame.PreserveControlSize)
                {
                    // Combat background roots are intentionally zero-sized Control nodes at runtime.
                }
                else if (frame.ControlSize.X > 0 && frame.ControlSize.Y > 0)
                {
                    control.Size = frame.ControlSize;
                }
                else if (control.Size.X <= 0 || control.Size.Y <= 0)
                {
                    var minimum = control.GetCombinedMinimumSize();
                    control.Size = minimum.X > 0 && minimum.Y > 0
                        ? minimum
                        : new Vector2(DefaultSceneSize, DefaultSceneSize);
                }

                control.Position = frame.NodePosition;
                control.PivotOffset = frame.PivotOffset;
                control.Scale = frame.NodeScale;
                break;
            case Node2D node2D:
                node2D.Position = frame.NodePosition;
                node2D.Scale = frame.NodeScale;
                break;
        }
    }

    private static bool TryFindAuthoredBounds(Node node, out Rect2 bounds)
    {
        if (string.Equals(node.Name.ToString(), "Bounds", StringComparison.OrdinalIgnoreCase)
            && TryReadBoundsNode(node, out bounds))
        {
            return true;
        }

        foreach (var child in node.GetChildren())
        {
            if (child is not Node childNode)
            {
                continue;
            }

            if (TryFindAuthoredBounds(childNode, out bounds))
            {
                return true;
            }
        }

        bounds = default;
        return false;
    }

    private static AssetCompositionRectSnapshot ToSnapshot(Rect2 rect)
    {
        return new AssetCompositionRectSnapshot(
            rect.Position.X,
            rect.Position.Y,
            rect.Size.X,
            rect.Size.Y);
    }

    private static AssetCompositionRectSnapshot ToSnapshot(Rect2I rect)
    {
        return new AssetCompositionRectSnapshot(
            rect.Position.X,
            rect.Position.Y,
            rect.Size.X,
            rect.Size.Y);
    }

    private static bool TryConfigureVfxPreview(Node node)
    {
        var configured = false;
        foreach (var current in EnumerateNodes(node))
        {
            if (current is Line2D line)
            {
                configured = true;
                if (line.GetPointCount() == 0)
                {
                    line.AddPoint(new Vector2(96, 320));
                    line.AddPoint(new Vector2(260, 150));
                    line.AddPoint(new Vector2(440, 250));
                    line.AddPoint(new Vector2(640, 96));
                }

                line.Visible = true;
                line.QueueRedraw();
            }

            if (current is CpuParticles2D particles)
            {
                configured = true;
                particles.Visible = true;
                particles.Emitting = true;
                particles.Restart();
            }

            if (LooksLikeVfxNode(current))
            {
                configured = true;
                if (current is CanvasItem canvasItem)
                {
                    canvasItem.Visible = true;
                    canvasItem.QueueRedraw();
                }
            }
        }

        return configured;
    }

    private static bool TryPrepareCombatBackgroundLayers(
        Node root,
        out IReadOnlyList<string> notes,
        out string unsupportedLayerTypes)
    {
        var supportedLayerCount = 0;
        var unsupported = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var node in EnumerateNodes(root))
        {
            if (ReferenceEquals(node, root))
            {
                continue;
            }

            if (node is CanvasItem canvasItem)
            {
                canvasItem.Visible = true;
                canvasItem.QueueRedraw();
                if (node is Sprite2D sprite && sprite.Texture is not null)
                {
                    supportedLayerCount += 1;
                }
                else if (node is TextureRect textureRect && textureRect.Texture is not null)
                {
                    supportedLayerCount += 1;
                }
                else if (LooksLikeCombatBackgroundLayer(node))
                {
                    supportedLayerCount += 1;
                }

                continue;
            }

            if (LooksLikeCombatBackgroundLayer(node))
            {
                unsupported.Add(node.GetType().Name);
            }
        }

        unsupportedLayerTypes = string.Join(", ", unsupported);
        if (supportedLayerCount == 0)
        {
            notes = unsupported.Count > 0
                ? [$"Unsupported combat background layer node types: {unsupportedLayerTypes}."]
                : ["No supported visible CanvasItem layers were found in the combat background scene."];
            return false;
        }

        notes =
        [
            $"Prepared {supportedLayerCount} visible combat background layer node(s) for authored layer-stack rendering.",
        ];
        return true;
    }

    private static IReadOnlyList<string> GetCombatBackgroundPlaceholderNames(Node root)
    {
        var placeholders = new List<string>();
        foreach (var placeholder in EnumerateNodes(root))
        {
            if (ReferenceEquals(placeholder, root)
                || !TryResolveCombatBackgroundPlaceholder(placeholder, out var layerName))
            {
                continue;
            }

            placeholders.Add(layerName);
        }

        return placeholders;
    }

    private static bool TryComposeCombatBackgroundAliasLayers(
        Node root,
        CombatBackgroundCompositionPlan plan,
        Func<string, PackedScene?> loadLayerScene,
        out int composedLayerCount,
        out IReadOnlyList<AssetCompositionSelectedLayerSnapshot> selectedLayers,
        out IReadOnlyList<string> notes)
    {
        composedLayerCount = 0;
        var missingLayerCount = 0;
        var noteList = new List<string>();
        var selectedLayerList = new List<AssetCompositionSelectedLayerSnapshot>();
        var placeholders = new List<(Node Node, string LayerName)>();

        foreach (var placeholder in EnumerateNodes(root))
        {
            if (ReferenceEquals(placeholder, root)
                || !TryResolveCombatBackgroundPlaceholder(placeholder, out var layerName))
            {
                continue;
            }

            placeholders.Add((
                placeholder,
                layerName));
        }

        var plans = plan.LayerGroups
            .Where(group => !string.IsNullOrWhiteSpace(group.SelectedPath))
            .ToList();
        for (var index = 0; index < plans.Count; index += 1)
        {
            var placeholder = placeholders
                .First(candidate => string.Equals(candidate.LayerName, plans[index].Placeholder, StringComparison.Ordinal))
                .Node;
            var layerPath = plans[index].SelectedPath!;
            var layerScene = loadLayerScene(layerPath);
            if (layerScene is null)
            {
                missingLayerCount += 1;
                selectedLayerList.Add(new AssetCompositionSelectedLayerSnapshot(
                    Placeholder: plans[index].Placeholder,
                    Path: layerPath,
                    Order: plans[index].Order,
                    LoadStatus: "missing",
                    TextureRefs: [],
                    LocalBounds: null,
                    VisibleBounds: null));
                continue;
            }

            var layer = layerScene.Instantiate();
            if (layer is null)
            {
                missingLayerCount += 1;
                selectedLayerList.Add(new AssetCompositionSelectedLayerSnapshot(
                    Placeholder: plans[index].Placeholder,
                    Path: layerPath,
                    Order: plans[index].Order,
                    LoadStatus: "missing",
                    TextureRefs: [],
                    LocalBounds: null,
                    VisibleBounds: null));
                continue;
            }

            if (layer is CanvasItem canvasItem)
            {
                canvasItem.Visible = true;
                canvasItem.QueueRedraw();
            }

            var textureRefs = CollectTextureReferences(layer);
            var bounds = TryFindAuthoredBounds(layer, out var authoredBounds)
                ? ToSnapshot(authoredBounds)
                : null;
            placeholder.AddChild(layer);
            composedLayerCount += 1;
            selectedLayerList.Add(new AssetCompositionSelectedLayerSnapshot(
                Placeholder: plans[index].Placeholder,
                Path: layerPath,
                Order: plans[index].Order,
                LoadStatus: "loaded",
                TextureRefs: textureRefs,
                LocalBounds: bounds,
                VisibleBounds: bounds));
        }

        if (composedLayerCount == 0)
        {
            selectedLayers = selectedLayerList;
            notes =
            [
                "No combat background layer scenes were found for the requested alias.",
            ];
            return false;
        }

        noteList.Add(
            $"Injected {composedLayerCount} deterministic combat background layer scene(s).");
        noteList.Add($"Composed combat background layer paths: {string.Join(", ", plans.Select(plan => plan.SelectedPath))}.");
        if (missingLayerCount > 0)
        {
            noteList.Add(
                $"Skipped {missingLayerCount} combat background layer placeholder(s) without matching layer scenes.");
        }

        selectedLayers = selectedLayerList;
        notes = noteList;
        return true;
    }

    private static IReadOnlyList<string> StabilizeCombatBackgroundAliasPreview(Node root)
        => StabilizeBackgroundParticles(root, "combat background alias");

    // Shared by the combat-background alias lane and the explicit-size event-backdrop still lane: a still that
    // may live forever under an immutable URL must not depend on where each emitter's simulation happened to be
    // at capture time, so every Particles2D is silenced and hidden. `context` only labels the note.
    private static IReadOnlyList<string> StabilizeBackgroundParticles(Node root, string context)
    {
        var particleCount = 0;
        foreach (var node in EnumerateNodes(root))
        {
            if (!TryStabilizeBackgroundParticleNode(node))
            {
                continue;
            }

            if (node is CanvasItem canvasItem)
            {
                canvasItem.Visible = false;
            }

            particleCount += 1;
        }

        return particleCount > 0
            ? [$"Disabled {particleCount} particle node(s) for deterministic {context} rendering."]
            : [];
    }

    private static bool TryStabilizeBackgroundParticleNode(object node)
    {
        if (!node.GetType().Name.Contains("Particles2D", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TrySetDynamicValue(node, "emitting", false);
    }

    private static AssetCompositionActiveSceneSnapshot ObserveActiveCombatBackgroundScene(
        CombatBackgroundAliasRequest request,
        IReadOnlyList<string> selectedLayerPaths)
    {
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (root is null)
        {
            return new AssetCompositionActiveSceneSnapshot("not-active", false, [], []);
        }

        var backgroundPrefix = $"res://scenes/backgrounds/{request.BackgroundId}/";
        var observed = new SortedSet<string>(StringComparer.Ordinal);
        var matchedRoot = false;
        foreach (var node in EnumerateNodes(root))
        {
            if (!TryReadNodeSceneFilePath(node, out var path))
            {
                continue;
            }

            path = NormalizeResourcePath(path);
            if (string.Equals(path, request.RootScenePath, StringComparison.Ordinal))
            {
                matchedRoot = true;
            }

            if (path.StartsWith(backgroundPrefix, StringComparison.Ordinal)
                && !string.Equals(path, request.RootScenePath, StringComparison.Ordinal))
            {
                observed.Add(path);
            }
        }

        if (!matchedRoot && observed.Count == 0)
        {
            return new AssetCompositionActiveSceneSnapshot("not-active", false, [], []);
        }

        var selected = selectedLayerPaths
            .Select(NormalizeResourcePath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        var observedList = observed.ToList();
        if (selected.SequenceEqual(observedList, StringComparer.Ordinal))
        {
            return new AssetCompositionActiveSceneSnapshot("matched", matchedRoot, observedList, []);
        }

        var differences = new List<string>();
        var missing = selected.Except(observedList, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            differences.Add($"Selected layer(s) not observed in active scene: {string.Join(", ", missing)}.");
        }

        var extra = observedList.Except(selected, StringComparer.Ordinal).ToList();
        if (extra.Count > 0)
        {
            differences.Add($"Observed active layer(s) not selected deterministically: {string.Join(", ", extra)}.");
        }

        return new AssetCompositionActiveSceneSnapshot("different", matchedRoot, observedList, differences);
    }

    private static bool TryReadNodeSceneFilePath(Node node, out string path)
    {
        path = string.Empty;
        if (!string.IsNullOrWhiteSpace(node.SceneFilePath))
        {
            path = node.SceneFilePath;
            return true;
        }

        if (TryGetDynamicValue(node, "scene_file_path", out var raw)
            && raw is not null
            && !string.IsNullOrWhiteSpace(raw.ToString()))
        {
            path = raw.ToString()!;
            return true;
        }

        return false;
    }

    private static IReadOnlyList<CombatBackgroundLayerPlan> BuildCombatBackgroundLayerPlans(
        IReadOnlyList<string> placeholders,
        IReadOnlyList<string> discoveredLayerPaths)
    {
        return BuildCombatBackgroundCompositionPlan("unknown", placeholders, discoveredLayerPaths)
            .LayerGroups
            .Where(group => !string.IsNullOrWhiteSpace(group.SelectedPath))
            .Select(group => new CombatBackgroundLayerPlan(group.Placeholder, group.SelectedPath!))
            .ToList();
    }

    private static CombatBackgroundCompositionPlan BuildCombatBackgroundCompositionPlan(
        string backgroundId,
        IReadOnlyList<string> placeholders,
        IReadOnlyList<string> discoveredLayerPaths)
    {
        var candidates = discoveredLayerPaths
            .Select(path => TryParseCombatBackgroundLayerCandidate(path, out var candidate) ? candidate : (CombatBackgroundLayerCandidate?)null)
            .Where(candidate => candidate.HasValue)
            .Select(candidate => candidate!.Value)
            .ToList();
        var backgroundLayersByIndex = candidates
            .Where(candidate => candidate.BackgroundLayerIndex.HasValue)
            .GroupBy(candidate => candidate.BackgroundLayerIndex!.Value)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(candidate => candidate.LayerPath)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToList());
        var foregroundLayers = candidates
            .Where(candidate => candidate.IsForeground)
            .Select(candidate => candidate.LayerPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        var placeholderPlans = new List<CombatBackgroundPlaceholderPlan>(placeholders.Count);
        var layerGroups = new List<CombatBackgroundLayerGroupPlan>(placeholders.Count);
        var warnings = new List<AssetCompositionWarningSnapshot>();
        for (var order = 0; order < placeholders.Count; order += 1)
        {
            var layerName = placeholders[order];
            IReadOnlyList<string> layerCandidates = [];
            if (string.Equals(layerName, "Foreground", StringComparison.Ordinal))
            {
                layerCandidates = foregroundLayers;
            }
            else if (TryParseCombatBackgroundPlaceholderIndex(layerName, out var layerIndex)
                && backgroundLayersByIndex.TryGetValue(layerIndex, out var backgroundLayerCandidates))
            {
                layerCandidates = backgroundLayerCandidates;
            }

            var selectedPath = layerCandidates.FirstOrDefault();
            placeholderPlans.Add(new CombatBackgroundPlaceholderPlan(
                Name: layerName,
                NodePath: layerName,
                Order: order,
                Matched: !string.IsNullOrWhiteSpace(selectedPath)));
            layerGroups.Add(new CombatBackgroundLayerGroupPlan(
                Name: layerName,
                Placeholder: layerName,
                Candidates: layerCandidates,
                SelectedPath: selectedPath,
                SelectionSource: !string.IsNullOrWhiteSpace(selectedPath) ? "deterministic-first-sorted" : string.Empty,
                Order: order));

            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                warnings.Add(new AssetCompositionWarningSnapshot(
                    Code: "missing-layer",
                    Severity: "warning",
                    Message: $"Placeholder {layerName} did not have a matching deterministic layer scene.",
                    Details: new Dictionary<string, string>
                    {
                        ["placeholder"] = layerName,
                    }));
            }
        }

        var allMatchedCandidates = layerGroups
            .SelectMany(group => group.Candidates)
            .ToHashSet(StringComparer.Ordinal);
        var unusedCandidates = candidates
            .Select(candidate => candidate.LayerPath)
            .Where(path => !allMatchedCandidates.Contains(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        if (unusedCandidates.Count > 0)
        {
            warnings.Add(new AssetCompositionWarningSnapshot(
                Code: "unused-layer-candidates",
                Severity: "info",
                Message: "Discovered deterministic layer candidates that do not match root placeholders.",
                Details: new Dictionary<string, string>
                {
                    ["candidateCount"] = unusedCandidates.Count.ToString(),
                    ["candidates"] = string.Join(", ", unusedCandidates),
                }));
        }

        return new CombatBackgroundCompositionPlan(
            BackgroundId: backgroundId,
            Placeholders: placeholderPlans,
            LayerGroups: layerGroups,
            Warnings: warnings);
    }

    private static bool TryParseCombatBackgroundLayerCandidate(
        string layerPath,
        out CombatBackgroundLayerCandidate candidate)
    {
        var fileName = layerPath.Replace('\\', '/').Split('/').Last();
        if (fileName.Contains("_fg_", StringComparison.OrdinalIgnoreCase))
        {
            candidate = new CombatBackgroundLayerCandidate(null, true, layerPath);
            return true;
        }

        var bgMarker = fileName.LastIndexOf("_bg_", StringComparison.OrdinalIgnoreCase);
        if (bgMarker < 0)
        {
            candidate = default;
            return false;
        }

        var indexStart = bgMarker + "_bg_".Length;
        var indexEnd = fileName.IndexOf('_', indexStart);
        if (indexEnd <= indexStart
            || !int.TryParse(fileName[indexStart..indexEnd], out var layerIndex))
        {
            candidate = default;
            return false;
        }

        candidate = new CombatBackgroundLayerCandidate(layerIndex, false, layerPath);
        return true;
    }

    private static bool TryResolveCombatBackgroundPlaceholder(Node node, out string layerName)
    {
        layerName = node.Name.ToString();
        if (string.Equals(layerName, "Foreground", StringComparison.Ordinal))
        {
            return true;
        }

        if (!layerName.StartsWith("Layer_", StringComparison.Ordinal)
            || layerName.Length != "Layer_00".Length)
        {
            return false;
        }

        return TryParseCombatBackgroundPlaceholderIndex(layerName, out _);
    }

    private static bool TryParseCombatBackgroundPlaceholderIndex(string layerName, out int layerIndex)
    {
        layerIndex = default;
        if (!layerName.StartsWith("Layer_", StringComparison.Ordinal)
            || layerName.Length != "Layer_00".Length)
        {
            return false;
        }

        return int.TryParse(layerName["Layer_".Length..], out layerIndex);
    }

    private static bool LooksLikeCombatBackgroundLayer(Node node)
    {
        var name = node.Name.ToString();
        return name.Contains("background", StringComparison.OrdinalIgnoreCase)
            || name.Contains("bg", StringComparison.OrdinalIgnoreCase)
            || name.Contains("layer", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<Node> EnumerateNodes(Node node)
    {
        yield return node;
        foreach (var child in node.GetChildren())
        {
            if (child is not Node childNode)
            {
                continue;
            }

            foreach (var nested in EnumerateNodes(childNode))
            {
                yield return nested;
            }
        }
    }

    private static void QueueRedrawCanvasItems(Node node, bool makeVisible = true)
    {
        foreach (var current in EnumerateNodes(node))
        {
            if (current is CanvasItem canvasItem)
            {
                if (makeVisible)
                {
                    canvasItem.Visible = true;
                }

                canvasItem.QueueRedraw();
            }
        }
    }

    private static bool LooksLikeVfxNode(Node node)
    {
        if (node is Line2D or CpuParticles2D)
        {
            return true;
        }

        if (node.GetType().Name.Contains("Vfx", StringComparison.OrdinalIgnoreCase)
            || node.Name.ToString().Contains("Vfx", StringComparison.OrdinalIgnoreCase)
            || node.Name.ToString().Contains("Trail", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return node is CanvasItem { Material: CanvasItemMaterial material }
            && material.BlendMode == CanvasItemMaterial.BlendModeEnum.Add;
    }

    private static bool TryReadBoundsNode(Node node, out Rect2 bounds)
    {
        if (TryGetFloatValue(node, "offset_left", out var left)
            && TryGetFloatValue(node, "offset_top", out var top)
            && TryGetFloatValue(node, "offset_right", out var right))
        {
            TryGetFloatValue(node, "offset_bottom", out var bottom);
            var width = right - left;
            var height = bottom - top;
            if (width > 0 && height > 0)
            {
                bounds = new Rect2(left, top, width, height);
                return true;
            }
        }

        if (node is Control control)
        {
            var size = control.Size;
            if (size.X <= 0 || size.Y <= 0)
            {
                size = control.GetCombinedMinimumSize();
            }

            if (size.X > 0 && size.Y > 0)
            {
                bounds = new Rect2(control.Position, size);
                return true;
            }
        }

        bounds = default;
        return false;
    }

    private static bool TryGetFloatValue(object target, string memberName, out float value)
    {
        if (TryGetDynamicValue(target, memberName, out var raw))
        {
            switch (raw)
            {
                case float single:
                    value = single;
                    return true;
                case double number:
                    value = (float)number;
                    return true;
                case int integer:
                    value = integer;
                    return true;
                case long integer:
                    value = integer;
                    return true;
                case string text when float.TryParse(text, out var parsed):
                    value = parsed;
                    return true;
            }
        }

        value = 0f;
        return false;
    }

    private bool TryConfigureSpineFirstFramePreview(object target, out string animationName)
    {
        animationName = string.Empty;
        var spineNode = target is Node node
            ? FindFirstSpinePreviewNode(node)
            : LooksLikeSpinePreviewNode(target) ? target : null;
        if (spineNode is null)
        {
            return false;
        }

        animationName = ResolveSpinePreviewAnimation(spineNode);
        TrySetDynamicValue(spineNode, "preview_frame", true);
        TrySetDynamicValue(spineNode, "preview_time", 0d);
        TrySetDynamicValue(spineNode, "visible", true);

        if (!string.IsNullOrWhiteSpace(animationName))
        {
            TrySetDynamicValue(spineNode, "preview_animation", animationName);
            TryInvokeAnimationMethod(spineNode, animationName);
        }

        if (spineNode is CanvasItem canvasItem)
        {
            canvasItem.QueueRedraw();
        }

        return true;
    }

    private bool TryStartSpinePreviewAnimation(object target, out string animationName)
    {
        animationName = string.Empty;
        var spineNode = target is Node node
            ? FindFirstSpinePreviewNode(node)
            : LooksLikeSpinePreviewNode(target) ? target : null;
        if (spineNode is null)
        {
            return false;
        }

        animationName = ResolveSpinePreviewAnimation(spineNode);
        TrySetDynamicValue(spineNode, "preview_frame", false);
        TrySetDynamicValue(spineNode, "visible", true);

        if (!string.IsNullOrWhiteSpace(animationName))
        {
            TrySetDynamicValue(spineNode, "preview_animation", animationName);
            TryInvokeAnimationMethod(spineNode, animationName);
        }

        if (spineNode is CanvasItem canvasItem)
        {
            canvasItem.QueueRedraw();
        }

        return true;
    }

    private static object? FindFirstSpinePreviewNode(Node node)
    {
        if (LooksLikeSpinePreviewNode(node))
        {
            return node;
        }

        foreach (var child in node.GetChildren())
        {
            if (child is not Node childNode)
            {
                continue;
            }

            var nested = FindFirstSpinePreviewNode(childNode);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static bool LooksLikeSpinePreviewNode(object target)
    {
        if (target.GetType().Name.Contains("SpineSprite", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TryGetDynamicValue(target, "skeleton_data_res", out var skeletonData)
            && skeletonData is not null;
    }

    private static string ResolveSpinePreviewAnimation(object target)
    {
        if (TryGetDynamicValue(target, "preview_animation", out var previewAnimation)
            && previewAnimation is string text
            && !string.IsNullOrWhiteSpace(text)
            && !string.Equals(text, "-- Empty --", StringComparison.Ordinal))
        {
            return text;
        }

        return "idle_loop";
    }

    // Construct a host node for a standalone Spine skeleton. `SpineSprite` ships as a
    // GDExtension class with no managed binding type, so `Activator.CreateInstance`
    // over AppDomain reflection cannot build it (live SpineSprite nodes even report as
    // `Godot.Node2D`). Godot's `ClassDB` can instantiate GDExtension-registered classes,
    // so prefer it and fall back to managed reflection only for a real C# Spine type.
    private static Node? TryInstantiateSpineSpriteNode(List<string> noteList)
    {
        if (ClassDB.ClassExists("SpineSprite") && ClassDB.CanInstantiate("SpineSprite")
            && ClassDB.Instantiate("SpineSprite").AsGodotObject() is Node classDbNode)
        {
            noteList.Add("Instantiated Spine preview host via ClassDB.Instantiate(\"SpineSprite\").");
            return classDbNode;
        }

        var spineType = FindSpineSpriteNodeType();
        if (spineType is not null && Activator.CreateInstance(spineType) is Node reflectedNode)
        {
            return reflectedNode;
        }

        noteList.Add("Could not instantiate a SpineSprite host node via ClassDB or managed reflection.");
        return null;
    }

    // Wire a freshly-instantiated SpineSprite the way the game does (see NTreasureRoom:
    // SetSkeletonDataRes -> FindSkin -> SetSlotsToSetupPose -> animation "animation").
    // Applying a skin is mandatory: a skin-less spine has no slot attachments and renders
    // fully transparent. A union of all skins keeps a static preview visible without
    // needing the act-specific skin name; the setup pose plus the first frame of the
    // primary animation gives a deterministic, closed-chest-style pose.
    private static bool TryApplySpineSkinAndPose(Node node, Resource skeletonData, List<string> noteList, string? preferredSkinName = null)
        => TryApplySpineSkinAndPose(node, skeletonData, noteList, out _, preferredSkinName);

    // SPIRECTL_SPINE_SKEL_DEFAULT_SKIN: escape hatch for the R9 "keep the skeleton's own default skin when it
    // renders" preference in the STANDALONE (&skel=) lane. Default ON; `0`/`false`/`off`/`no` restores the
    // unconditional skin union (round-8 behaviour, halo and all). Read once — env vars are process-stable.
    private static readonly bool PreferSkeletonDefaultSkin =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SPINE_SKEL_DEFAULT_SKIN") ?? string.Empty)
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // Does the skeleton, wearing whatever skin it currently has (the default, straight off SetSkeletonDataRes),
    // actually draw something? Poses it exactly the way the caller will (setup pose + the primary animation's
    // first frame) and asks for real bounds. A skin-less rig reports a degenerate box, which is the signal to fall
    // back to the union. Best-effort: any failure answers "no" so the union path (today's behaviour) runs.
    private static bool PosedBoundsAreRenderable(MegaSkeleton skeleton, MegaSprite sprite, MegaSkeletonDataResource data)
    {
        try
        {
            skeleton.SetSlotsToSetupPose();
            var animationState = sprite.GetAnimationState();
            animationState.Apply(skeleton);

            var animationName = ResolvePrimarySpineAnimationName(data);
            if (!string.IsNullOrEmpty(animationName))
            {
                animationState.SetAnimation(animationName, loop: false);
                animationState.Apply(skeleton);
            }

            var probed = skeleton.GetBounds();
            return probed.Size.X > 1f && probed.Size.Y > 1f;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryApplySpineSkinAndPose(Node node, Resource skeletonData, List<string> noteList, out Rect2 bounds, string? preferredSkinName = null)
    {
        bounds = default;
        try
        {
            var sprite = new MegaSprite(node);
            sprite.SetSkeletonDataRes(new MegaSkeletonDataResource(skeletonData));

            var skeleton = sprite.GetSkeleton();
            if (skeleton is null)
            {
                noteList.Add("MegaSprite could not build a skeleton from the standalone skeleton data.");
                return false;
            }

            var data = skeleton.GetData();

            // Prefer a single authoritative skin (e.g. an act chest's normal skin) when the
            // model resolution provided it: that avoids the stroke/outline skin's white halo
            // and yields tighter bounds. Fall back to a union of all skins otherwise, since a
            // skin-less spine renders fully transparent.
            var appliedNamedSkin = false;
            if (!string.IsNullOrWhiteSpace(preferredSkinName) && data.FindSkin(preferredSkinName) is { } namedSkin)
            {
                // FindSkin already returns a MegaSkin; apply it directly (no union → no
                // stroke/outline halo, tighter bounds).
                skeleton.SetSkin(namedSkin);
                noteList.Add($"Applied the model-resolved skin '{preferredSkinName}' in isolation (no skin union).");
                appliedNamedSkin = true;
            }

            // R9: try the skeleton's OWN default skin first. A fresh MegaSprite already wears it after
            // SetSkeletonDataRes, and it is exactly what the game renders for a node that never calls SetSkin —
            // the boss map point never does, so the union below was adding a stroke/outline skin the game never
            // shows (a thick white halo around the boss map node:
            // the game frame has ZERO pixels brighter than the parchment, the union bake had ~7% pure white).
            // Only when that default renders NOTHING (a rig whose attachments all live in named skins — the
            // Fossil Stalker class the union exists for) do we fall back to the union. Kill switch:
            // SPIRECTL_SPINE_SKEL_DEFAULT_SKIN=0 restores the unconditional union.
            var appliedDefaultSkin = false;
            if (!appliedNamedSkin && PreferSkeletonDefaultSkin && PosedBoundsAreRenderable(skeleton, sprite, data))
            {
                appliedDefaultSkin = true;
                noteList.Add("Kept the skeleton's own default skin (it renders); no skin union, so no stroke/outline halo.");
            }

            if (!appliedNamedSkin && !appliedDefaultSkin)
            {
                var skins = data.GetSkins();
                if (skins.Count > 0)
                {
                    var combined = sprite.NewSkin("spirectl_preview");
                    foreach (var skin in skins)
                    {
                        combined.AddSkin(new MegaSkin(skin));
                    }

                    skeleton.SetSkin(combined);
                    noteList.Add($"Applied a union of {skins.Count} skeleton skin(s) so attachment slots render.");
                }
            }

            skeleton.SetSlotsToSetupPose();

            var animationState = sprite.GetAnimationState();
            animationState.Apply(skeleton);

            var animationName = ResolvePrimarySpineAnimationName(data);
            if (!string.IsNullOrEmpty(animationName))
            {
                animationState.SetAnimation(animationName, loop: false);
                noteList.Add($"Pinned the standalone skeleton to the first frame of animation '{animationName}'.");
            }

            animationState.SetTimeScale(0f);

            // The posed skeleton now exposes its real local bounds. The synthetic
            // defaults assume a 1024² box at origin, which leaves the actual (offset,
            // larger) chest content outside the captured frame; feed the real bounds
            // back so FrameFromBounds sizes/positions the SubViewport correctly.
            bounds = skeleton.GetBounds();
            return true;
        }
        catch (Exception ex)
        {
            noteList.Add(
                $"MegaSpine skin/pose setup failed ({ex.GetType().Name}: {ex.Message}); falling back to generic preview defaults.");
            return false;
        }
    }

    private static string ResolvePrimarySpineAnimationName(MegaSkeletonDataResource data)
    {
        // The treasure chest's pose animation is literally named "animation"; prefer it,
        // otherwise fall back to the first declared animation.
        if (data.HasAnimation("animation"))
        {
            return "animation";
        }

        var animations = data.GetAnimationNames();
        return animations.Count > 0 ? animations[0] : string.Empty;
    }

    private static Type? FindSpineSpriteNodeType()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(assembly =>
            {
                try
                {
                    return assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    return ex.Types.Where(type => type is not null)!;
                }
            })
            .Where(type => type is not null && typeof(Node).IsAssignableFrom(type))
            .Cast<Type>()
            .OrderByDescending(type => string.Equals(type.Name, "SpineSprite", StringComparison.OrdinalIgnoreCase))
            .ThenBy(type => type.FullName, StringComparer.Ordinal)
            .FirstOrDefault(type => type.Name.Contains("SpineSprite", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> ReadStringCandidates(object target, IReadOnlyList<string> memberNames)
    {
        foreach (var memberName in memberNames)
        {
            if (TryGetDynamicValue(target, memberName, out var raw))
            {
                var values = NormalizeStringCollection(raw).ToList();
                if (values.Count > 0)
                {
                    return values;
                }
            }

            var method = target.GetType().GetMethod(
                memberName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase,
                null,
                Type.EmptyTypes,
                null);
            if (method is not null)
            {
                var values = NormalizeStringCollection(method.Invoke(target, null)).ToList();
                if (values.Count > 0)
                {
                    return values;
                }
            }
        }

        return [];
    }

    private static IEnumerable<string> NormalizeStringCollection(object? raw)
    {
        if (raw is null)
        {
            yield break;
        }

        if (raw is string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                yield return text;
            }
            yield break;
        }

        if (raw is Godot.Collections.Array godotArray)
        {
            foreach (var item in godotArray)
            {
                var textValue = item.ToString();
                if (!string.IsNullOrWhiteSpace(textValue))
                {
                    yield return textValue;
                }
            }
            yield break;
        }

        if (raw is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                var textValue = item?.ToString();
                if (!string.IsNullOrWhiteSpace(textValue))
                {
                    yield return textValue;
                }
            }
        }
    }

    private static string PreferName(IReadOnlyList<string> values, IReadOnlyList<string> preferred)
    {
        foreach (var wanted in preferred)
        {
            var match = values.FirstOrDefault(value => string.Equals(value, wanted, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }

        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static bool TryGetRectValue(object target, string memberName, out Rect2 value)
    {
        if (TryGetDynamicValue(target, memberName, out var raw))
        {
            if (raw is Rect2 rect && rect.Size.X > 0 && rect.Size.Y > 0)
            {
                value = rect;
                return true;
            }

            if (raw is Rect2I rectI && rectI.Size.X > 0 && rectI.Size.Y > 0)
            {
                value = new Rect2(
                    new Vector2(rectI.Position.X, rectI.Position.Y),
                    new Vector2(rectI.Size.X, rectI.Size.Y));
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryInvokeAnimationMethod(object target, string animationName)
    {
        if (string.IsNullOrWhiteSpace(animationName))
        {
            return false;
        }

        foreach (var methodName in new[] { "SetAnimation", "set_animation" })
        {
            var method = target.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, methodName, StringComparison.OrdinalIgnoreCase)
                    && candidate.GetParameters() is var parameters
                    && parameters.Length == 1
                    && parameters[0].ParameterType == typeof(string));
            if (method is not null)
            {
                method.Invoke(target, [animationName]);
                return true;
            }
        }

        if (target is GodotObject godotObject)
        {
            foreach (var methodName in new[] { "SetAnimation", "set_animation" })
            {
                if (godotObject.HasMethod(methodName))
                {
                    godotObject.Call(methodName, animationName);
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryInvokeSkinMethod(object target, string skinName)
    {
        if (string.IsNullOrWhiteSpace(skinName))
        {
            return false;
        }

        foreach (var methodName in new[] { "SetSkin", "set_skin" })
        {
            var method = target.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, methodName, StringComparison.OrdinalIgnoreCase)
                    && candidate.GetParameters() is var parameters
                    && parameters.Length == 1
                    && parameters[0].ParameterType == typeof(string));
            if (method is not null)
            {
                method.Invoke(target, [skinName]);
                return true;
            }
        }

        if (target is GodotObject godotObject)
        {
            foreach (var methodName in new[] { "SetSkin", "set_skin" })
            {
                if (godotObject.HasMethod(methodName))
                {
                    godotObject.Call(methodName, skinName);
                    return true;
                }
            }
        }

        return false;
    }

    private static string DisplayDefault(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "runtime/default" : value;
    }

    private static bool TryGetDynamicValue(object target, string memberName, out object? value)
    {
        foreach (var candidate in CandidateMemberNames(memberName))
        {
            var property = target.GetType().GetProperty(
                candidate,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (property is not null && property.GetIndexParameters().Length == 0)
            {
                value = property.GetValue(target);
                return true;
            }

            var field = target.GetType().GetField(
                candidate,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (field is not null)
            {
                value = field.GetValue(target);
                return true;
            }
        }

        if (target is GodotObject godotObject)
        {
            foreach (var candidate in CandidateMemberNames(memberName))
            {
                var variant = godotObject.Get(candidate);
                if (variant.VariantType == Variant.Type.Nil)
                {
                    continue;
                }

                value = variant.VariantType switch
                {
                    Variant.Type.String => variant.AsString().ToString(),
                    Variant.Type.StringName => variant.AsStringName().ToString(),
                    Variant.Type.Bool => variant.AsBool(),
                    Variant.Type.Float => variant.AsDouble(),
                    Variant.Type.Int => variant.AsInt32(),
                    Variant.Type.Object => variant.AsGodotObject(),
                    _ => variant.ToString(),
                };
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool TrySetDynamicValue(object target, string memberName, object? value)
    {
        foreach (var candidate in CandidateMemberNames(memberName))
        {
            var property = target.GetType().GetProperty(
                candidate,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (property is not null
                && property.CanWrite
                && TryConvertDynamicValue(value, property.PropertyType, out var converted))
            {
                property.SetValue(target, converted);
                return true;
            }

            var field = target.GetType().GetField(
                candidate,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (field is not null && TryConvertDynamicValue(value, field.FieldType, out var convertedField))
            {
                field.SetValue(target, convertedField);
                return true;
            }
        }

        if (target is GodotObject godotObject)
        {
            foreach (var candidate in CandidateMemberNames(memberName))
            {
                switch (value)
                {
                    case null:
                        return false;
                    case bool boolean:
                        godotObject.Set(candidate, boolean);
                        break;
                    case double number:
                        godotObject.Set(candidate, number);
                        break;
                    case float single:
                        godotObject.Set(candidate, single);
                        break;
                    case int integer:
                        godotObject.Set(candidate, integer);
                        break;
                    case string text:
                        godotObject.Set(candidate, text);
                        break;
                    case Color color:
                        godotObject.Set(candidate, color);
                        break;
                    case GodotObject godotValue:
                        godotObject.Set(candidate, godotValue);
                        break;
                    default:
                        return false;
                }
                return true;
            }
        }

        return false;
    }

    private static bool TryConvertDynamicValue(object? value, Type targetType, out object? converted)
    {
        if (value is null)
        {
            converted = null;
            return !targetType.IsValueType || Nullable.GetUnderlyingType(targetType) is not null;
        }

        var destinationType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (destinationType.IsInstanceOfType(value))
        {
            converted = value;
            return true;
        }

        if (destinationType == typeof(StringName) && value is string stringName)
        {
            converted = new StringName(stringName);
            return true;
        }

        try
        {
            converted = Convert.ChangeType(value, destinationType);
            return true;
        }
        catch
        {
            converted = null;
            return false;
        }
    }

    private static IEnumerable<string> CandidateMemberNames(string memberName)
    {
        yield return memberName;
        if (memberName.StartsWith('_'))
        {
            var trimmed = memberName.TrimStart('_');
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                yield return trimmed;
            }
        }

        yield return memberName.Replace("_", string.Empty, StringComparison.Ordinal);
        yield return ToPascalCase(memberName);
    }

    private static string ToPascalCase(string value)
    {
        return string.Concat(
            value
                .Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => char.ToUpperInvariant(segment[0]) + segment[1..]));
    }

    private static bool HasVisiblePixels(Image image)
    {
        var inspected = EnsureRgba8(image);

        return HasVisiblePixels(inspected.GetData());
    }

    private static Image EnsureRgba8(Image image)
    {
        if (image.GetFormat() == Image.Format.Rgba8)
        {
            return image;
        }

        var inspected = (Image)image.Duplicate();
        // Image.Convert hard-errors on GPU-compressed formats; decompress to a CPU format first.
        if (inspected.IsCompressed())
        {
            inspected.Decompress();
        }

        inspected.Convert(Image.Format.Rgba8);
        return inspected;
    }

    private static Image CropToVisiblePixels(Image image, int padding)
        => CropToVisiblePixels(image, padding, out _);

    // WS5 overload: also report WHERE the kept raster sits inside the captured viewport. A lane that expresses
    // its raster as a placement rect (the char-select background still) must account for the trim, or it would
    // claim the whole capture rect for an image that only covers part of it.
    private static Image CropToVisiblePixels(Image image, int padding, out Rect2I region)
    {
        var inspected = image;
        if (inspected.GetFormat() != Image.Format.Rgba8)
        {
            inspected = (Image)image.Duplicate();
            if (inspected.IsCompressed())
            {
                inspected.Decompress();
            }

            inspected.Convert(Image.Format.Rgba8);
        }

        if (!TryFindVisiblePixelBounds(
            inspected.GetData(),
            inspected.GetWidth(),
            inspected.GetHeight(),
            padding,
            out var rect))
        {
            region = new Rect2I(0, 0, image.GetWidth(), image.GetHeight());
            return image;
        }

        region = rect;
        return inspected.GetRegion(rect);
    }

    private static Image NormalizePreviewAlphaFromColor(Image image)
    {
        var normalized = (Image)image.Duplicate();
        if (normalized.GetFormat() != Image.Format.Rgba8)
        {
            if (normalized.IsCompressed())
            {
                normalized.Decompress();
            }

            normalized.Convert(Image.Format.Rgba8);
        }

        for (var y = 0; y < normalized.GetHeight(); y += 1)
        {
            for (var x = 0; x < normalized.GetWidth(); x += 1)
            {
                var color = normalized.GetPixel(x, y);
                if (color.A > 0)
                {
                    continue;
                }

                var intensity = Math.Max(color.R, Math.Max(color.G, color.B));
                if (intensity <= 0)
                {
                    continue;
                }

                var alpha = Math.Clamp(intensity, 0.05f, 1f);
                normalized.SetPixel(x, y, new Color(color.R, color.G, color.B, alpha));
            }
        }

        return normalized;
    }

    private static bool TryFindVisiblePixelBounds(
        byte[] rgbaBytes,
        int width,
        int height,
        int padding,
        out Rect2I rect)
    {
        var minX = width;
        var minY = height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < height; y += 1)
        {
            for (var x = 0; x < width; x += 1)
            {
                var alphaIndex = ((y * width) + x) * 4 + 3;
                if (alphaIndex >= rgbaBytes.Length || rgbaBytes[alphaIndex] == 0)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < minX || maxY < minY)
        {
            rect = default;
            return false;
        }

        var left = Math.Max(0, minX - padding);
        var top = Math.Max(0, minY - padding);
        var right = Math.Min(width, maxX + 1 + padding);
        var bottom = Math.Min(height, maxY + 1 + padding);
        rect = new Rect2I(left, top, right - left, bottom - top);
        return true;
    }

    private static bool HasVisiblePixels(byte[] rgbaBytes)
    {
        for (var index = 3; index < rgbaBytes.Length; index += 4)
        {
            if (rgbaBytes[index] != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static double ComputeTransparentPixelRatio(byte[] rgbaBytes, int width, int height)
    {
        var pixelCount = Math.Max(0, width) * Math.Max(0, height);
        if (pixelCount == 0)
        {
            return 1d;
        }

        var transparent = 0;
        for (var index = 3; index < rgbaBytes.Length; index += 4)
        {
            if (rgbaBytes[index] == 0)
            {
                transparent += 1;
            }
        }

        return Math.Clamp((double)transparent / pixelCount, 0d, 1d);
    }

    private static EncounterRgbEvidence ComputeRgbEvidence(byte[] rgbaBytes, int width, int height)
    {
        var pixelCount = Math.Max(0, width) * Math.Max(0, height);
        if (pixelCount == 0)
        {
            return new EncounterRgbEvidence(0, 0d);
        }

        var nonzero = 0;
        for (var index = 0; index + 2 < rgbaBytes.Length; index += 4)
        {
            if (rgbaBytes[index] != 0 || rgbaBytes[index + 1] != 0 || rgbaBytes[index + 2] != 0)
            {
                nonzero += 1;
            }
        }

        return new EncounterRgbEvidence(nonzero, Math.Clamp((double)nonzero / pixelCount, 0d, 1d));
    }

#pragma warning disable CS0618 // TileMap support is retained for legacy TileSet preview compatibility.
    private static bool TryAssignDeterministicTile(TileSet tileSet, TileMap tileMap, out string note)
    {
        note = string.Empty;
        for (var index = 0; index < tileSet.GetSourceCount(); index += 1)
        {
            var sourceId = tileSet.GetSourceId(index);
            var source = tileSet.GetSource(sourceId);
            if (source is null)
            {
                continue;
            }

            if (TryAssignAtlasTile(tileMap, source, sourceId, out note))
            {
                return true;
            }

            if (TryAssignSceneTile(tileMap, source, sourceId, out note))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryAssignAtlasTile(TileMap tileMap, object source, int sourceId, out string note)
    {
        note = string.Empty;
        var getTilesIds = source.GetType().GetMethod("GetTilesIds", Type.EmptyTypes);
        if (getTilesIds?.Invoke(source, null) is not System.Collections.IEnumerable tiles)
        {
            return false;
        }

        foreach (var tile in tiles)
        {
            if (tile is not Vector2I atlasCoords)
            {
                continue;
            }

            var alternativeId = 0;
            var getAlternativesCount = source.GetType().GetMethod("GetAlternativeTilesCount", [typeof(Vector2I)]);
            if (getAlternativesCount?.Invoke(source, [atlasCoords]) is int alternativesCount && alternativesCount <= 0)
            {
                alternativeId = 0;
            }

            tileMap.SetCell(0, Vector2I.Zero, sourceId, atlasCoords, alternativeId);
            note = $"Rendered a representative TileSet preview using source {sourceId} atlas tile {atlasCoords}.";
            return true;
        }

        return false;
    }

    private static bool TryAssignSceneTile(TileMap tileMap, object source, int sourceId, out string note)
    {
        note = string.Empty;
        var getSceneTilesCount = source.GetType().GetMethod("GetSceneTilesCount", Type.EmptyTypes);
        var getSceneTileId = source.GetType().GetMethod("GetSceneTileId", [typeof(int)]);
        if (getSceneTilesCount?.Invoke(source, null) is not int sceneTilesCount || sceneTilesCount <= 0 || getSceneTileId is null)
        {
            return false;
        }

        var alternativeId = (int)getSceneTileId.Invoke(source, [0])!;
        tileMap.SetCell(0, Vector2I.Zero, sourceId, new Vector2I(0, 0), alternativeId);
        note = $"Rendered a representative TileSet preview using source {sourceId} scene tile {alternativeId}.";
        return true;
    }
#pragma warning restore CS0618

    private AssetExtractOperationResult UnsupportedResourceTypeFailure(
        AssetExtractRequestSnapshot request,
        Resource resource,
        string note)
    {
        var resourceType = resource.GetType().FullName ?? resource.GetType().Name;
        return Failure(
            request,
            "resource_type",
            resourceType,
            $"Resource type '{resourceType}' {note} Live host rendering cannot report success for unsupported resource shapes.");
    }

    private AssetExtractOperationResult Failure(
        AssetExtractRequestSnapshot request,
        string field,
        string value,
        string note,
        IReadOnlyList<string>? notes = null,
        IReadOnlyDictionary<string, object?>? diagnostic = null)
    {
        return AssetExtractOperationResult.Failure(
            requestId: request.RequestId,
            source: DataSourceKind.Live,
            provisional: false,
            code: AssetExtractFailureCode.RuntimeFailure,
            message: "The live bridge could not render the requested asset.",
            details:
            [
                new AssetExtractDetail(
                    Field: field,
                    Value: value,
                    Note: note,
                    Diagnostic: diagnostic),
            ]) with
        {
            Notes = notes ?? [],
        };
    }
}
