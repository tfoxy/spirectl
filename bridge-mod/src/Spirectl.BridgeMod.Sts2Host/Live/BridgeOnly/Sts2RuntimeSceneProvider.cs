using Godot;
using MegaCrit.Sts2.Core.Runs;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Common;
using System.Reflection;
using System.Security.Cryptography;

namespace Spirectl.Sts2.Live;



public sealed class Sts2RuntimeSceneProvider : IRuntimeSceneProvider
{
    private const string HoverTipPanelTexture = "res://images/ui/hover_tip.png";

    // Nested scroll containers take one pass each, and an inner one may have to settle before the
    // outer one can aim at the moved control. Six is well past any nesting the game's UI has, and
    // bounds a request that would otherwise loop on a container that never converges.
    private const int EnsureVisibleMaxPasses = 6;

    // Long enough to clear the frame the scroll was queued on at 60fps. The pass loop re-measures
    // either way, so this only affects how many passes convergence takes, never correctness.
    private const int EnsureVisibleSettleMs = 24;

    private readonly Sts2ScreenLocator _screenLocator;
    private readonly ILogStream _logStream;

    public Sts2RuntimeSceneProvider(
        Sts2ScreenLocator screenLocator,
        ILogStream logStream)
    {
        _screenLocator = screenLocator;
        _logStream = logStream;
    }

    public RuntimeSceneTreeResult GetTree(RuntimeSceneQuery request)
    {
        try
        {
            return Sts2MainThreadDispatcher.Invoke(() => GetTreeOnMainThread(request));
        }
        catch (Exception ex)
        {
            _logStream.Write(BridgeLogLevel.Error, "bridge.runtime-scene", $"Runtime scene-tree inspection failed: {ex}");
            return RuntimeSceneTreeResult.Failure(
                source: DataSourceKind.Live,
                provisional: false,
                code: RuntimeSceneFailureCode.RuntimeFailure,
                message: "failed to inspect the live runtime scene tree.",
                details:
                [
                    new RuntimeSceneDetail("exception", ex.GetType().Name, ex.Message),
                ]);
        }
    }

    public RuntimeSceneNodeResult GetNode(RuntimeSceneQuery request)
    {
        try
        {
            return Sts2MainThreadDispatcher.Invoke(() => GetNodeOnMainThread(request));
        }
        catch (Exception ex)
        {
            _logStream.Write(BridgeLogLevel.Error, "bridge.runtime-scene", $"Runtime scene-node inspection failed: {ex}");
            return RuntimeSceneNodeResult.Failure(
                source: DataSourceKind.Live,
                provisional: false,
                code: RuntimeSceneFailureCode.RuntimeFailure,
                message: "failed to inspect the requested live runtime scene node.",
                details:
                [
                    new RuntimeSceneDetail("exception", ex.GetType().Name, ex.Message),
                ]);
        }
    }

    public RuntimeSceneSetVisibleResult SetVisible(RuntimeSceneSetVisibleRequestSnapshot request)
    {
        try
        {
            return Sts2MainThreadDispatcher.Invoke(() => SetVisibleOnMainThread(request));
        }
        catch (Exception ex)
        {
            _logStream.Write(BridgeLogLevel.Error, "bridge.runtime-scene", $"Runtime scene-node visibility mutation failed: {ex}");
            return RuntimeSceneSetVisibleResult.Failure(
                source: DataSourceKind.Live,
                provisional: false,
                code: RuntimeSceneFailureCode.RuntimeFailure,
                message: "failed to mutate the requested live runtime scene node visibility.",
                details:
                [
                    new RuntimeSceneDetail("exception", ex.GetType().Name, ex.Message),
                ]);
        }
    }

    public RuntimeSceneControlHoverResult HoverControl(RuntimeSceneControlHoverRequestSnapshot request)
    {
        string? resolvedNodePath = null;
        RuntimeSceneVector2Snapshot? hoverPosition = null;
        try
        {
            // Scrolling happens BEFORE the hover, in its own main-thread passes: a ScrollContainer
            // applies a new offset by queueing a child sort, so the child's rect is still the old one
            // for the rest of the frame that moved it. Each pass therefore scrolls at most one
            // container and then yields, and the next pass re-measures.
            IReadOnlyList<RuntimeSceneHoverScrollSnapshot> scrolled = request.EnsureVisible
                ? RunEnsureVisiblePasses(request.NodePath)
                : [];

            return Sts2MainThreadDispatcher.Invoke(() => HoverControlOnMainThread(
                request,
                scrolled,
                (path, position) =>
                {
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        resolvedNodePath = path;
                    }

                    if (position is not null)
                    {
                        hoverPosition = position;
                    }
                }));
        }
        catch (Exception ex)
        {
            _logStream.Write(BridgeLogLevel.Error, "bridge.runtime-scene", $"Runtime scene control hover failed: {ex}");
            return RuntimeSceneControlHoverResult.Failure(
                source: DataSourceKind.Live,
                provisional: false,
                code: RuntimeSceneFailureCode.RuntimeFailure,
                message: "failed to hover the requested live runtime scene control.",
                details: HoverRuntimeFailureDetails(request, ex, resolvedNodePath, hoverPosition));
        }
    }

    public RuntimeSceneControlUnhoverResult UnhoverControl(RuntimeSceneControlUnhoverRequestSnapshot request)
    {
        try
        {
            return Sts2MainThreadDispatcher.Invoke(() => UnhoverControlOnMainThread(request));
        }
        catch (Exception ex)
        {
            _logStream.Write(BridgeLogLevel.Error, "bridge.runtime-scene", $"Runtime scene control unhover failed: {ex}");
            return RuntimeSceneControlUnhoverResult.Failure(
                source: DataSourceKind.Live,
                provisional: false,
                code: RuntimeSceneFailureCode.RuntimeFailure,
                message: "failed to unhover the requested live runtime scene control.",
                details:
                [
                    new RuntimeSceneDetail("exception", ex.GetType().Name, ex.Message),
                ]);
        }
    }

    public RuntimeTransitionStatusResult GetTransitionStatus(RuntimeTransitionStatusRequestSnapshot request)
    {
        try
        {
            return Sts2MainThreadDispatcher.Invoke(GetTransitionStatusOnMainThread);
        }
        catch (Exception ex)
        {
            _logStream.Write(BridgeLogLevel.Error, "bridge.runtime-scene", $"Runtime transition status inspection failed: {ex}");
            return RuntimeTransitionStatusResult.Failure(
                source: DataSourceKind.Live,
                provisional: false,
                code: RuntimeSceneFailureCode.RuntimeFailure,
                message: "failed to inspect live runtime transition status.",
                details:
                [
                    new RuntimeSceneDetail("exception", ex.GetType().Name, ex.Message),
                ]);
        }
    }

    public IReadOnlyDictionary<string, RuntimeSceneTextPropertiesSnapshot?> GetTextProperties(
        IReadOnlyCollection<string> nodePaths)
    {
        try
        {
            return Sts2MainThreadDispatcher.Invoke(() => GetTextPropertiesOnMainThread(nodePaths));
        }
        catch (Exception ex)
        {
            _logStream.Write(BridgeLogLevel.Error, "bridge.runtime-scene", $"Runtime scene text-property batch inspection failed: {ex}");
            return nodePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(path => path, _ => (RuntimeSceneTextPropertiesSnapshot?)null, StringComparer.Ordinal);
        }
    }

    private RuntimeSceneTreeResult GetTreeOnMainThread(RuntimeSceneQuery request)
    {
        var screen = _screenLocator.Locate();
        var root = ResolveRootNode();
        if (root.Error is not null)
        {
            return RuntimeSceneTreeResult.Failure(
                source: DataSourceKind.Live,
                provisional: false,
                code: root.Error.Code,
                message: root.Error.Message,
                details: root.Error.Details);
        }

        var normalizedNodePath = NormalizeRequestedNodePath(request.NodePath);
        var target = ResolveTargetNode(root.Node!, normalizedNodePath);
        if (target.Error is not null)
        {
            return RuntimeSceneTreeResult.Failure(
                source: DataSourceKind.Live,
                provisional: false,
                code: target.Error.Code,
                message: target.Error.Message,
                details: target.Error.Details);
        }

        var nodes = EnumerateSubtree(target.Node!)
            .Select(node => DescribeNode(screen.ScreenInstanceId, node, request))
            .ToArray();

        _logStream.Write(
            BridgeLogLevel.Info,
            "bridge.runtime-scene",
            $"Inspected runtime scene subtree '{normalizedNodePath}' on screen '{screen.ScreenType}' ({nodes.Length} nodes).");

        return RuntimeSceneTreeResult.Success(
            source: DataSourceKind.Live,
            provisional: false,
            screenType: screen.ScreenType,
            screenTitle: screen.ScreenTitle,
            screenInstanceId: screen.ScreenInstanceId,
            rootNodePath: normalizedNodePath,
            nodes: nodes,
            notes: []);
    }

    private RuntimeSceneNodeResult GetNodeOnMainThread(RuntimeSceneQuery request)
    {
        var screen = _screenLocator.Locate();
        var root = ResolveRootNode();
        if (root.Error is not null)
        {
            return RuntimeSceneNodeResult.Failure(
                source: DataSourceKind.Live,
                provisional: false,
                code: root.Error.Code,
                message: root.Error.Message,
                details: root.Error.Details);
        }

        var normalizedNodePath = NormalizeRequestedNodePath(request.NodePath);
        var target = ResolveTargetNode(root.Node!, normalizedNodePath);
        if (target.Error is not null)
        {
            return RuntimeSceneNodeResult.Failure(
                source: DataSourceKind.Live,
                provisional: false,
                code: target.Error.Code,
                message: target.Error.Message,
                details: target.Error.Details);
        }

        var node = DescribeNode(screen.ScreenInstanceId, target.Node!, request);
        var children = EnumerateChildren(target.Node!)
            .Select(child => DescribeNode(screen.ScreenInstanceId, child, request))
            .ToArray();

        _logStream.Write(
            BridgeLogLevel.Info,
            "bridge.runtime-scene",
            $"Inspected runtime scene node '{normalizedNodePath}' on screen '{screen.ScreenType}' ({children.Length} children).");

        return RuntimeSceneNodeResult.Success(
            source: DataSourceKind.Live,
            provisional: false,
            screenType: screen.ScreenType,
            screenTitle: screen.ScreenTitle,
            screenInstanceId: screen.ScreenInstanceId,
            node: node,
            children: children,
            notes: []);
    }

    private IReadOnlyDictionary<string, RuntimeSceneTextPropertiesSnapshot?> GetTextPropertiesOnMainThread(
        IReadOnlyCollection<string> nodePaths)
    {
        var requestedPaths = nodePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var results = requestedPaths.ToDictionary(
            path => path,
            _ => (RuntimeSceneTextPropertiesSnapshot?)null,
            StringComparer.Ordinal);
        if (requestedPaths.Length == 0)
        {
            return results;
        }

        var root = ResolveRootNode();
        if (root.Error is not null || root.Node is null)
        {
            return results;
        }

        foreach (var requestedPath in requestedPaths)
        {
            var normalizedNodePath = NormalizeRequestedNodePath(requestedPath);
            var target = ResolveTargetNode(root.Node, normalizedNodePath);
            if (target.Error is not null || target.Node is null)
            {
                continue;
            }

            results[requestedPath] = DescribeTextProperties(target.Node);
        }

        return results;
    }

    private RuntimeSceneSetVisibleResult SetVisibleOnMainThread(RuntimeSceneSetVisibleRequestSnapshot request)
    {
        var screen = _screenLocator.Locate();
        var root = ResolveRootNode();
        if (root.Error is not null)
        {
            return RuntimeSceneSetVisibleResult.Failure(
                source: DataSourceKind.Live,
                provisional: false,
                code: root.Error.Code,
                message: root.Error.Message,
                details: root.Error.Details);
        }

        var normalizedNodePath = NormalizeRequestedNodePath(request.NodePath);
        var target = ResolveTargetNode(root.Node!, normalizedNodePath);
        if (target.Error is not null)
        {
            return RuntimeSceneSetVisibleResult.Failure(
                source: DataSourceKind.Live,
                provisional: false,
                code: target.Error.Code,
                message: target.Error.Message,
                details: target.Error.Details);
        }

        if (target.Node is not CanvasItem canvasItem)
        {
            return RuntimeSceneSetVisibleResult.Failure(
                source: DataSourceKind.Live,
                provisional: false,
                code: RuntimeSceneFailureCode.InvalidNodePath,
                message: $"Live runtime scene node '{normalizedNodePath}' does not support visibility mutation.",
                details:
                [
                    new RuntimeSceneDetail(
                        "node_type",
                        target.Node!.GetType().FullName ?? target.Node.GetType().Name,
                        "Only Godot CanvasItem nodes expose runtime visibility."),
                ]);
        }

        var previousVisible = canvasItem.Visible;
        canvasItem.Visible = request.Visible;
        var node = DescribeNode(
            screen.ScreenInstanceId,
            target.Node!,
            new RuntimeSceneQuery(
                request.NodePath,
                IncludeProperties: true,
                IncludeComputedTransform: request.IncludeComputedTransform));

        _logStream.Write(
            BridgeLogLevel.Info,
            "bridge.runtime-scene",
            $"Set runtime scene node '{normalizedNodePath}' visibility from '{previousVisible}' to '{request.Visible}' on screen '{screen.ScreenType}'.");

        return RuntimeSceneSetVisibleResult.Success(
            source: DataSourceKind.Live,
            provisional: false,
            screenType: screen.ScreenType,
            screenTitle: screen.ScreenTitle,
            screenInstanceId: screen.ScreenInstanceId,
            node: node,
            previousVisible: previousVisible,
            requestedVisible: request.Visible,
            changed: previousVisible != request.Visible,
            notes: []);
    }

    private RuntimeSceneControlHoverResult HoverControlOnMainThread(
        RuntimeSceneControlHoverRequestSnapshot request,
        IReadOnlyList<RuntimeSceneHoverScrollSnapshot> scrolled,
        Action<string?, RuntimeSceneVector2Snapshot?>? recordProgress = null)
    {
        var screen = _screenLocator.Locate();
        var root = ResolveRootNode();
        if (root.Error is not null)
        {
            return RuntimeSceneControlHoverResult.Failure(
                source: DataSourceKind.Live,
                provisional: false,
                code: root.Error.Code,
                message: root.Error.Message,
                details: root.Error.Details);
        }

        var normalizedNodePath = NormalizeRequestedNodePath(request.NodePath);
        recordProgress?.Invoke(normalizedNodePath, null);
        var target = ResolveTargetNode(root.Node!, normalizedNodePath);
        if (target.Error is not null)
        {
            return RuntimeSceneControlHoverResult.Failure(
                source: DataSourceKind.Live,
                provisional: false,
                code: target.Error.Code,
                message: target.Error.Message,
                details: target.Error.Details);
        }

        if (target.Node is not Control control)
        {
            return RuntimeSceneControlHoverResult.Failure(
                source: DataSourceKind.Live,
                provisional: false,
                code: RuntimeSceneFailureCode.InvalidNodePath,
                message: $"Live runtime scene node '{normalizedNodePath}' is not a hoverable Godot Control.",
                details:
                [
                    new RuntimeSceneDetail(
                        "node_type",
                        target.Node!.GetType().FullName ?? target.Node.GetType().Name,
                        "Only Godot Control nodes expose pointer hover geometry for dev scene hover."),
                ]);
        }

        if (!control.IsVisibleInTree())
        {
            return RuntimeSceneControlHoverResult.Failure(
                source: DataSourceKind.Live,
                provisional: false,
                code: RuntimeSceneFailureCode.InvalidNodePath,
                message: $"Live runtime scene control '{normalizedNodePath}' is not visible in the current scene tree.",
                details:
                [
                    new RuntimeSceneDetail("node_path", normalizedNodePath, "Only visible Control nodes can be hovered."),
                ]);
        }

        var rect = control.GetGlobalRect();
        if (rect.Size.X <= 0 || rect.Size.Y <= 0)
        {
            return RuntimeSceneControlHoverResult.Failure(
                source: DataSourceKind.Live,
                provisional: false,
                code: RuntimeSceneFailureCode.InvalidNodePath,
                message: $"Live runtime scene control '{normalizedNodePath}' does not expose a non-empty hover rect.",
                details:
                [
                    new RuntimeSceneDetail("node_path", normalizedNodePath, "The control global rect must have positive width and height."),
                ]);
        }

        // A node path resolves regardless of where the node has been scrolled to, so the old
        // "centre of the global rect" rule happily produced a coordinate outside the viewport, or
        // inside a scroll container but beyond its clip. Warping there hovers whatever IS painted
        // at that coordinate, and the caller has no way to tell. Decide reachability first.
        var plan = RuntimeSceneHoverGeometry.Plan(ToSnapshot(rect), CollectHoverClips(control), scrolled);
        if (!plan.Reachable && !request.AllowOffscreen)
        {
            return RuntimeSceneControlHoverResult.Failure(
                source: DataSourceKind.Live,
                provisional: false,
                code: RuntimeSceneFailureCode.InvalidNodePath,
                message: RuntimeSceneHoverGeometry.DescribeRefusal(normalizedNodePath, plan),
                details: RuntimeSceneHoverGeometry.RefusalDetails(normalizedNodePath, plan));
        }

        var notes = DescribeHoverVisibilityNotes(plan, request.AllowOffscreen);
        var hoverPosition = plan.HoverPosition is { } resolved
            ? new Vector2((float)resolved.X, (float)resolved.Y)
            : rect.Position + rect.Size / 2;
        recordProgress?.Invoke(normalizedNodePath, ToSnapshot(hoverPosition));
        Input.WarpMouse(hoverPosition);
        Input.ParseInputEvent(new InputEventMouseMotion
        {
            Position = hoverPosition,
            GlobalPosition = hoverPosition,
            Relative = Vector2.Zero,
        });
        Sts2LiveIntrospection.TryInvokeParameterlessMethod(control, "OnFocus");

        if (request.SettleMs > 0)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(request.SettleMs, 5_000u)));
        }

        var node = DescribeNode(
            screen.ScreenInstanceId,
            target.Node,
            new RuntimeSceneQuery(
                request.NodePath,
                IncludeProperties: true,
                IncludeComputedTransform: true));
        var hoverTip = request.IncludeHoverTip ? InspectHoverTip(root.Node!) : null;

        _logStream.Write(
            BridgeLogLevel.Info,
            "bridge.runtime-scene",
            $"Hovered runtime scene control '{normalizedNodePath}' on screen '{screen.ScreenType}'.");

        return RuntimeSceneControlHoverResult.Success(
            source: DataSourceKind.Live,
            provisional: false,
            screenType: screen.ScreenType,
            screenTitle: screen.ScreenTitle,
            screenInstanceId: screen.ScreenInstanceId,
            node: node,
            resolvedNodePath: normalizedNodePath,
            presentationElementId: request.PresentationElementId,
            hovered: true,
            hoverPosition: ToSnapshot(hoverPosition),
            hoverTip: hoverTip,
            notes: notes,
            visibility: plan.Visibility);
    }

    // One ensure-visible pass: measure, and if the target is not inside every clip that governs it,
    // scroll the innermost ancestor scroll container that still has somewhere to go. Returns the
    // container it moved, or null when there was nothing left to move (settled, or unscrollable).
    private static RuntimeSceneHoverScrollSnapshot? EnsureVisibleOnMainThread(string requestedNodePath)
    {
        var root = ResolveRootNode();
        if (root.Error is not null)
        {
            return null;
        }

        var target = ResolveTargetNode(root.Node!, NormalizeRequestedNodePath(requestedNodePath));
        // Every real failure (bad path, non-Control, hidden) is reported by the hover itself, with
        // its own message. A pass that cannot measure simply has nothing to scroll.
        if (target.Error is not null || target.Node is not Control control || !control.IsVisibleInTree())
        {
            return null;
        }

        var clips = CollectHoverClips(control);
        if (RuntimeSceneHoverGeometry.IsFullyVisible(ToSnapshot(control.GetGlobalRect()), clips))
        {
            return null;
        }

        for (Node? node = control.GetParent(); node is not null; node = node.GetParent())
        {
            if (node is not ScrollContainer container)
            {
                continue;
            }

            var previousHorizontal = container.ScrollHorizontal;
            var previousVertical = container.ScrollVertical;
            container.EnsureControlVisible(control);
            if (container.ScrollHorizontal == previousHorizontal
                && container.ScrollVertical == previousVertical)
            {
                // Already showing as much of the target as it can; try the next container out.
                continue;
            }

            return new RuntimeSceneHoverScrollSnapshot(
                NodePath: container.GetPath().ToString(),
                PreviousHorizontal: previousHorizontal,
                PreviousVertical: previousVertical,
                Horizontal: container.ScrollHorizontal,
                Vertical: container.ScrollVertical);
        }

        return null;
    }

    private static IReadOnlyList<RuntimeSceneHoverScrollSnapshot> RunEnsureVisiblePasses(string requestedNodePath)
    {
        var scrolled = new List<RuntimeSceneHoverScrollSnapshot>();
        for (var pass = 0; pass < EnsureVisibleMaxPasses; pass++)
        {
            var step = Sts2MainThreadDispatcher.Invoke(() => EnsureVisibleOnMainThread(requestedNodePath));
            if (step is null)
            {
                break;
            }

            scrolled.Add(step);

            // Yield the frame the scroll was queued on, so the next pass measures the moved rect
            // rather than the stale one.
            Thread.Sleep(EnsureVisibleSettleMs);
        }

        return scrolled;
    }

    // Every rectangle the target has to survive to be reachable by a click: each ancestor that
    // clips its children, then the viewport. Ancestors are innermost-first so the reported
    // "clipped by" list reads from the nearest container outwards.
    private static IReadOnlyList<RuntimeSceneHoverClipSnapshot> CollectHoverClips(Control control)
    {
        var clips = new List<RuntimeSceneHoverClipSnapshot>();
        var sceneRoot = (Engine.GetMainLoop() as SceneTree)?.Root;
        var isInRootViewport = false;
        for (Node? node = control.GetParent(); node is not null; node = node.GetParent())
        {
            if (node is Viewport viewport)
            {
                // A control inside a SubViewport measures in that viewport's own space, which the
                // root window's visible rect cannot be compared against. Stop, and leave the
                // viewport clip off rather than refusing a target on a bogus comparison.
                isInRootViewport = ReferenceEquals(viewport, sceneRoot);
                break;
            }

            if (node is not Control ancestor)
            {
                continue;
            }

            var isScrollContainer = ancestor is ScrollContainer;
            if (!isScrollContainer && !ancestor.ClipContents)
            {
                continue;
            }

            clips.Add(new RuntimeSceneHoverClipSnapshot(
                NodePath: ancestor.GetPath().ToString(),
                NodeType: ancestor.GetType().FullName ?? ancestor.GetType().Name,
                Reason: isScrollContainer
                    ? RuntimeSceneHoverClipReasons.ScrollContainer
                    : RuntimeSceneHoverClipReasons.ClipContents,
                Rect: ToSnapshot(ancestor.GetGlobalRect())));
        }

        if (isInRootViewport && sceneRoot is not null)
        {
            clips.Add(new RuntimeSceneHoverClipSnapshot(
                NodePath: sceneRoot.GetPath().ToString(),
                NodeType: sceneRoot.GetType().FullName ?? sceneRoot.GetType().Name,
                Reason: RuntimeSceneHoverClipReasons.Viewport,
                Rect: ToSnapshot(sceneRoot.GetVisibleRect())));
        }

        return clips;
    }

    private static IReadOnlyList<string> DescribeHoverVisibilityNotes(
        RuntimeSceneHoverPlan plan,
        bool allowOffscreen)
    {
        var notes = new List<string>();
        foreach (var scroll in plan.Visibility.Scrolled)
        {
            notes.Add(
                $"ensure-visible scrolled '{scroll.NodePath}' from ({scroll.PreviousHorizontal:0}, "
                + $"{scroll.PreviousVertical:0}) to ({scroll.Horizontal:0}, {scroll.Vertical:0}).");
        }

        if (!plan.Reachable && allowOffscreen)
        {
            notes.Add(
                "Hovered an unreachable target because --allow-offscreen was set: the position is "
                + "outside every clip, so the pointer landed on whatever else is painted there.");
        }
        else if (!plan.Visibility.FullyVisible)
        {
            var by = string.Join(
                ", ",
                plan.Visibility.ClippedBy.Select(clip => $"'{clip.NodePath}' ({clip.Reason})"));
            notes.Add(
                $"Target is only partly visible, clipped by {by}; hovered the centre of the visible part.");
        }

        return notes;
    }

    // Inverse of HoverControlOnMainThread: move the pointer to a neutral point (so Godot's own mouse
    // tracking exits whatever was hovered and dismisses tooltips) and, when a target is supplied,
    // invoke that control's unfocus hook so its focus visuals clear symmetrically.
    private RuntimeSceneControlUnhoverResult UnhoverControlOnMainThread(RuntimeSceneControlUnhoverRequestSnapshot request)
    {
        var screen = _screenLocator.Locate();
        var root = ResolveRootNode();
        if (root.Error is not null)
        {
            return RuntimeSceneControlUnhoverResult.Failure(
                source: DataSourceKind.Live,
                provisional: false,
                code: root.Error.Code,
                message: root.Error.Message,
                details: root.Error.Details);
        }

        var notes = new List<string>();
        var hasTarget = !string.IsNullOrWhiteSpace(request.NodePath);
        RuntimeSceneNodeSnapshot? node = null;
        var resolvedNodePath = string.Empty;
        Control? target = null;

        if (hasTarget)
        {
            var normalizedNodePath = NormalizeRequestedNodePath(request.NodePath!);
            resolvedNodePath = normalizedNodePath;
            var resolved = ResolveTargetNode(root.Node!, normalizedNodePath);
            if (resolved.Error is not null)
            {
                return RuntimeSceneControlUnhoverResult.Failure(
                    source: DataSourceKind.Live,
                    provisional: false,
                    code: resolved.Error.Code,
                    message: resolved.Error.Message,
                    details: resolved.Error.Details);
            }

            if (resolved.Node is Control control)
            {
                target = control;
            }
            else
            {
                notes.Add($"Runtime scene node '{normalizedNodePath}' is not a Godot Control; skipped its unfocus hook.");
            }

            node = DescribeNode(
                screen.ScreenInstanceId,
                resolved.Node!,
                new RuntimeSceneQuery(
                    request.NodePath!,
                    IncludeProperties: true,
                    IncludeComputedTransform: true));
        }

        // Pick a neutral pointer position that is guaranteed to lie outside the target's rect so the
        // move actually crosses the control's boundary and fires its mouse-exit. Viewport origin works
        // for the overwhelming majority of controls; only step outside when a full-screen rect covers it.
        var neutral = Vector2.Zero;
        if (target is not null)
        {
            var rect = target.GetGlobalRect();
            if (rect.HasPoint(neutral))
            {
                neutral = rect.End + Vector2.One;
            }
        }

        Input.WarpMouse(neutral);
        Input.ParseInputEvent(new InputEventMouseMotion
        {
            Position = neutral,
            GlobalPosition = neutral,
            Relative = Vector2.Zero,
        });

        // Drive the control's own unfocus state where it exposes the counterpart to the OnFocus hook
        // used by hover. TryInvokeParameterlessMethod no-ops safely when none of the names match.
        if (target is not null
            && !Sts2LiveIntrospection.TryInvokeParameterlessMethod(target, "OnUnfocus", "OnBlur", "OnUnhover", "OnMouseExit"))
        {
            notes.Add($"Runtime scene control '{resolvedNodePath}' exposes no parameterless unfocus hook; relied on pointer move to clear hover.");
        }

        _logStream.Write(
            BridgeLogLevel.Info,
            "bridge.runtime-scene",
            hasTarget
                ? $"Unhovered runtime scene control '{resolvedNodePath}' on screen '{screen.ScreenType}'."
                : $"Cleared runtime scene hover on screen '{screen.ScreenType}'.");

        return RuntimeSceneControlUnhoverResult.Success(
            source: DataSourceKind.Live,
            provisional: false,
            screenType: screen.ScreenType,
            screenTitle: screen.ScreenTitle,
            screenInstanceId: screen.ScreenInstanceId,
            node: node,
            resolvedNodePath: resolvedNodePath,
            presentationElementId: request.PresentationElementId,
            hovered: false,
            pointerPosition: ToSnapshot(neutral),
            notes: notes);
    }

    private RuntimeTransitionStatusResult GetTransitionStatusOnMainThread()
    {
        var screen = _screenLocator.Locate();
        var blockers = new List<RuntimeTransitionBlockerSnapshot>();
        var notes = new List<string>();

        // Wait for the room/scene entrance transition (screen stops being black).
        InspectGameTransition(blockers);

        var root = ResolveRootNode();
        if (root.Error is not null)
        {
            return RuntimeTransitionStatusResult.Failure(
                source: DataSourceKind.Live,
                provisional: false,
                code: root.Error.Code,
                message: root.Error.Message,
                details: root.Error.Details);
        }

        // Wait for known one-shot intro animations (combat "Your turn", ancient
        // title+epithet). Ambient perpetual VFX are not on the allowlist, so they
        // never block quiescence.
        InspectIntroAnimations(root.Node!, blockers);

        return RuntimeTransitionStatusResult.Success(
            source: DataSourceKind.Live,
            provisional: false,
            screenType: screen.ScreenType,
            screenTitle: screen.ScreenTitle,
            screenInstanceId: screen.ScreenInstanceId,
            quiescent: blockers.Count == 0,
            blockingCount: blockers.Count,
            ignoredInfiniteCount: 0,
            blockers: blockers,
            notes: notes);
    }

    private static void InspectIntroAnimations(Node root, ICollection<RuntimeTransitionBlockerSnapshot> blockers)
    {
        foreach (var node in EnumerateSubtree(root))
        {
            var typeName = node.GetType().FullName ?? node.GetType().Name;
            if (!RuntimeTransitionWorkClassifier.IsIntroAnimationNodeType(typeName))
            {
                continue;
            }

            // Transient banners (e.g. combat "Your turn") animate their own modulate alpha
            // 0 -> 1 -> 0 and free themselves, so alpha is a good "done" signal for them. But
            // banners like NAncientNameBanner play their intro and then persist on screen at
            // full alpha, only fading their child labels — alpha never reports them as done.
            // When the banner drives its intro through a tween it stores, trust that tween's
            // running state instead; otherwise fall back to the alpha heuristic.
            var canvasItem = node as CanvasItem;
            var visibleInTree = canvasItem?.IsVisibleInTree() ?? true;
            var modulateAlpha = canvasItem is null ? 1.0 : canvasItem.Modulate.A;
            var introTweenRunning = TryReadIntroAnimationTweenRunning(node);

            var classification = RuntimeTransitionWorkClassifier.ClassifyIntroAnimation(
                visibleInTree,
                modulateAlpha,
                introTweenRunning);
            if (!classification.Blocks)
            {
                continue;
            }

            blockers.Add(TransitionBlocker(
                kind: "intro-animation",
                node: node,
                name: node.Name.ToString(),
                status: "animating",
                reason: classification.Reason,
                running: true,
                infinite: false));
        }
    }

    // Returns whether any Tween stored on the node is currently running, or null when the
    // node exposes no Tween fields at all (so the caller falls back to the alpha heuristic).
    // Banners that persist on screen drive their intro through a tween they keep a handle to;
    // a finished CreateTween() goes invalid, so IsInstanceValid + IsRunning is the "still
    // playing" signal even after the banner stops fading its own alpha.
    private static bool? TryReadIntroAnimationTweenRunning(Node node)
    {
        var foundTweenField = false;
        var anyRunning = false;

        for (var type = node.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            foreach (var field in type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!typeof(Tween).IsAssignableFrom(field.FieldType))
                {
                    continue;
                }

                foundTweenField = true;
                if (field.GetValue(node) is Tween tween && GodotObject.IsInstanceValid(tween) && tween.IsRunning())
                {
                    anyRunning = true;
                }
            }
        }

        return foundTweenField ? anyRunning : null;
    }

    private static void InspectGameTransition(ICollection<RuntimeTransitionBlockerSnapshot> blockers)
    {
        var game = ResolveNGameInstance();
        var transition = Sts2LiveIntrospection.GetMemberValue(game, "Transition");
        var classification = RuntimeTransitionWorkClassifier.ClassifyGameTransition(ReadBoolMember(transition, "InTransition"));
        if (!classification.Blocks)
        {
            return;
        }

        blockers.Add(TransitionBlocker(
            kind: "game-transition",
            node: transition as Node,
            name: "NGame.Transition",
            status: "in-transition",
            reason: classification.Reason,
            running: true,
            infinite: classification.Infinite));
    }

    private static object? ResolveNGameInstance()
    {
        foreach (var typeName in new[]
                 {
                     "NGame, sts2",
                     "MegaCrit.Sts2.Core.Nodes.NGame, sts2",
                 })
        {
            var type = Type.GetType(typeName);
            var instance = type?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(null);
            if (instance is not null)
            {
                return instance;
            }
        }

        return null;
    }

    private static RuntimeTransitionBlockerSnapshot TransitionBlocker(
        string kind,
        Node? node,
        string name,
        string status,
        string reason,
        string? animationName = null,
        int? loopsLeft = null,
        bool? running = null,
        bool? infinite = null)
        => new(
            Kind: kind,
            NodePath: node is null ? string.Empty : NormalizeResolvedNodePath(node.GetPath().ToString()),
            NodeType: node?.GetType().FullName ?? node?.GetType().Name ?? string.Empty,
            Name: node?.Name.ToString() ?? name,
            Status: status,
            Reason: reason,
            AnimationName: animationName,
            LoopsLeft: loopsLeft,
            Running: running,
            Infinite: infinite);

    private static bool? ReadBoolMember(object? target, string memberName)
        => Sts2LiveIntrospection.GetMemberValue(target, memberName) switch
        {
            bool value => value,
            _ => null,
        };

    private static IReadOnlyList<RuntimeSceneDetail> HoverRuntimeFailureDetails(
        RuntimeSceneControlHoverRequestSnapshot request,
        Exception exception,
        string? resolvedNodePath,
        RuntimeSceneVector2Snapshot? hoverPosition)
    {
        var root = UnwrapInvocationException(exception);
        var details = new List<RuntimeSceneDetail>
        {
            new("outer_exception_type", exception.GetType().FullName ?? exception.GetType().Name, "Exception type caught by the hover dispatcher."),
            new("outer_exception_message", exception.Message, "Exception message caught by the hover dispatcher."),
            new("root_exception_type", root.GetType().FullName ?? root.GetType().Name, "Innermost non-TargetInvocationException type."),
            new("root_exception_message", root.Message, "Innermost non-TargetInvocationException message."),
        };

        var frame = FirstUsefulStackFrame(root) ?? FirstUsefulStackFrame(exception);
        if (!string.IsNullOrWhiteSpace(frame))
        {
            details.Add(new RuntimeSceneDetail("first_stack_frame", frame!, "First stack frame outside reflection/invocation wrappers."));
        }

        if (!string.IsNullOrWhiteSpace(request.NodePath))
        {
            details.Add(new RuntimeSceneDetail("requested_node_path", request.NodePath, "Node path requested by dev scene hover."));
        }

        if (!string.IsNullOrWhiteSpace(request.PresentationElementId))
        {
            details.Add(new RuntimeSceneDetail("presentation_element_id", request.PresentationElementId!, "Presentation element id requested by dev scene hover."));
        }

        if (!string.IsNullOrWhiteSpace(resolvedNodePath))
        {
            details.Add(new RuntimeSceneDetail("resolved_node_path", resolvedNodePath!, "Normalized node path resolved before the hover failure."));
        }

        if (hoverPosition is not null)
        {
            details.Add(new RuntimeSceneDetail(
                "hover_position",
                FormattableString.Invariant($"{hoverPosition.X},{hoverPosition.Y}"),
                "Pointer position computed before the hover failure."));
        }

        return details;
    }

    private static Exception UnwrapInvocationException(Exception exception)
    {
        var current = exception;
        while (current is TargetInvocationException { InnerException: not null } invocation)
        {
            current = invocation.InnerException;
        }

        return current;
    }

    private static string? FirstUsefulStackFrame(Exception exception)
        => exception.StackTrace?
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(frame =>
                !frame.Contains("System.Reflection", StringComparison.Ordinal)
                && !frame.Contains("RuntimeMethodHandle.InvokeMethod", StringComparison.Ordinal)
                && !frame.Contains("TargetInvocationException", StringComparison.Ordinal));

    private static RootResolution ResolveRootNode()
    {
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (root is null)
        {
            return RootResolution.Failure(
                RuntimeSceneFailureCode.BridgeNotAttached,
                "the live bridge host could not resolve the Godot scene tree root.",
                [
                    new RuntimeSceneDetail(
                        "root",
                        "/root",
                        "Engine.GetMainLoop() did not expose a SceneTree root viewport."),
                ]);
        }

        return RootResolution.Success(root);
    }

    private static TargetResolution ResolveTargetNode(Node root, string normalizedNodePath)
    {
        if (string.Equals(normalizedNodePath, "/root", StringComparison.Ordinal))
        {
            return TargetResolution.Success(root);
        }

        var node = root.GetNodeOrNull(new NodePath(normalizedNodePath));
        if (node is null)
        {
            return TargetResolution.Failure(
                RuntimeSceneFailureCode.InvalidNodePath,
                $"No live runtime scene node was found at '{normalizedNodePath}'.",
                [
                    new RuntimeSceneDetail(
                        "node_path",
                        normalizedNodePath,
                        "Use a normalized Godot node path rooted at /root."),
                ]);
        }

        return TargetResolution.Success(node);
    }

    private static IEnumerable<Node> EnumerateSubtree(Node root)
    {
        yield return root;
        foreach (var child in EnumerateChildren(root))
        {
            foreach (var descendant in EnumerateSubtree(child))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<Node> EnumerateChildren(Node node)
    {
        for (var index = 0; index < node.GetChildCount(); index++)
        {
            if (node.GetChild(index) is Node child)
            {
                yield return child;
            }
        }
    }

    private static RuntimeSceneNodeSnapshot DescribeNode(string screenInstanceId, Node node, RuntimeSceneQuery request)
    {
        var nodePath = NormalizeResolvedNodePath(node.GetPath().ToString());
        var parentPath = node.GetParent() is { } parent
            ? NormalizeResolvedNodePath(parent.GetPath().ToString())
            : null;
        var ownerPath = node.Owner is { } owner
            ? NormalizeResolvedNodePath(owner.GetPath().ToString())
            : null;
        var sceneFilePath = typeof(Node).GetProperty("SceneFilePath")?.GetValue(node) as string;
        var script = DescribeAttachedScript(node);

        return new RuntimeSceneNodeSnapshot(
            NodeId: $"node:{screenInstanceId}:{nodePath}",
            NodePath: nodePath,
            Name: node.Name.ToString(),
            NodeType: node.GetType().FullName ?? node.GetType().Name,
            ParentNodePath: parentPath,
            OwnerPath: ownerPath,
            SceneFilePath: string.IsNullOrWhiteSpace(sceneFilePath) ? null : sceneFilePath,
            AttachedScriptPath: script.Path,
            AttachedScriptType: script.Type,
            ChildCount: node.GetChildCount(),
            Notes: [],
            Properties: request.IncludeProperties ? DescribeProperties(node, request.LeanProperties) : null,
            ComputedTransform: request.IncludeComputedTransform ? DescribeComputedTransform(node) : null,
            NativeNodeType: DescribeNativeNodeType(node));
    }

    private static string? DescribeNativeNodeType(Node node)
    {
        try
        {
            var nativeType = node.GetClass().ToString();
            return string.IsNullOrWhiteSpace(nativeType) ? null : nativeType;
        }
        catch
        {
            return null;
        }
    }

    private static ScriptInfo DescribeAttachedScript(Node node)
    {
        try
        {
            var getScript = typeof(Node).GetMethod("GetScript", Type.EmptyTypes);
            var script = getScript?.Invoke(node, null);
            if (script is null)
            {
                return ScriptInfo.Empty;
            }

            var scriptType = script.GetType();
            var resourcePath = scriptType.GetProperty("ResourcePath")?.GetValue(script) as string;
            return new ScriptInfo(
                Path: string.IsNullOrWhiteSpace(resourcePath) ? null : resourcePath,
                Type: scriptType.FullName ?? scriptType.Name);
        }
        catch
        {
            return ScriptInfo.Empty;
        }
    }

    internal static RuntimeSceneNodePropertiesSnapshot DescribeProperties(Node node, bool lean = false)
    {
        var notices = new List<RuntimeScenePropertyNoticeSnapshot>();
        var textureRefs = DescribeTextureRefs(node, notices, lean);

        bool? visible = null;
        bool? effectiveVisible = null;
        int? zIndex = null;
        RuntimeSceneColorSnapshot? modulate = null;
        RuntimeSceneColorSnapshot? selfModulate = null;
        RuntimeSceneColorSnapshot? effectiveModulate = null;
        bool? clipContents = null;
        int? clipChildrenMode = null;
        bool? showBehindParent = null;
        bool? zAsRelative = null;
        RuntimeSceneVector2Snapshot? position = null;
        RuntimeSceneVector2Snapshot? globalPosition = null;
        RuntimeSceneVector2Snapshot? scale = null;
        double? rotationRadians = null;
        RuntimeSceneVector2Snapshot? size = null;
        RuntimeSceneVector2Snapshot? pivotOffset = null;
        RuntimeSceneAnchorsSnapshot? anchors = null;
        RuntimeSceneOffsetsSnapshot? offsets = null;
        var text = DescribeTextProperties(node, lean);
        var ninePatch = DescribeNinePatchProperties(node, textureRefs, lean);
        // Expensive diagnostics the mirror never renders — skipped in lean to keep the main-thread walk cheap.
        var textureRect = lean ? null : DescribeTextureRectProperties(node);
        var material = lean ? null : DescribeMaterialProperties(node, notices);
        var layout = lean ? null : DescribeLayoutProperties(node);

        if (node is CanvasItem canvasItem)
        {
            visible = canvasItem.Visible;
            effectiveVisible = canvasItem.IsVisibleInTree();
            zIndex = canvasItem.ZIndex;
            showBehindParent = ReadBoolProperty(canvasItem, "ShowBehindParent");
            zAsRelative = ReadBoolProperty(canvasItem, "ZAsRelative");
            clipChildrenMode = ReadIntProperty(canvasItem, "ClipChildrenMode")
                ?? ReadIntProperty(canvasItem, "ClipChildren");
            modulate = ToSnapshot(canvasItem.Modulate);
            selfModulate = ToSnapshot(canvasItem.SelfModulate);
            effectiveModulate = ToSnapshot(EffectiveDrawModulate(canvasItem));
        }
        else
        {
            notices.Add(NotApplicable("properties.visible", "visible fields require a CanvasItem node."));
        }

        if (node is Node2D node2D)
        {
            position = ToSnapshot(node2D.Position);
            globalPosition = ToSnapshot(node2D.GlobalPosition);
            scale = ToSnapshot(node2D.Scale);
            rotationRadians = node2D.Rotation;
        }
        else if (node is Control control)
        {
            position = ToSnapshot(control.Position);
            globalPosition = ToSnapshot(control.GlobalPosition);
            scale = ToSnapshot(control.Scale);
            rotationRadians = control.Rotation;
            size = ToSnapshot(control.Size);
            pivotOffset = ToSnapshot(control.PivotOffset);
            anchors = new RuntimeSceneAnchorsSnapshot(
                control.AnchorLeft,
                control.AnchorTop,
                control.AnchorRight,
                control.AnchorBottom);
            offsets = new RuntimeSceneOffsetsSnapshot(
                control.OffsetLeft,
                control.OffsetTop,
                control.OffsetRight,
                control.OffsetBottom);
            clipContents = Sts2ControlClipContents.Read(control);
        }
        else
        {
            notices.Add(NotApplicable("properties.position", "position, scale, and rotation are available for Node2D and Control nodes."));
        }

        // Control-only pointer/focus policy, read with the same wrapped-Control fallback the live scene WATCHER
        // uses for `mouse_filter`: many STS2 scene nodes wear a C# script class that does not derive from
        // Godot.Control, so `node is Control` is false for them and a typed-only read would report null for a node
        // that really does carry the property. `ReadIntProperty` tries the managed property first and falls back to
        // the dynamic `mouse_filter`-style get. Null means "not a Control at all" — meaningfully different from a
        // Control that reports the enum's zero value, which is why the whole block is gated on Control-ness.
        int? mouseFilter = null;
        int? focusMode = null;
        int? mouseDefaultCursorShape = null;
        if (node is Control || node.IsClass("Control"))
        {
            mouseFilter = ReadIntProperty(node, "MouseFilter");
            focusMode = ReadIntProperty(node, "FocusMode");
            mouseDefaultCursorShape = ReadIntProperty(node, "MouseDefaultCursorShape");
        }

        return new RuntimeSceneNodePropertiesSnapshot(
            Visible: visible,
            EffectiveVisible: effectiveVisible,
            Position: position,
            GlobalPosition: globalPosition,
            Scale: scale,
            RotationRadians: rotationRadians,
            Size: size,
            PivotOffset: pivotOffset,
            Anchors: anchors,
            Offsets: offsets,
            ZIndex: zIndex,
            Modulate: modulate,
            SelfModulate: selfModulate,
            EffectiveModulate: effectiveModulate,
            ClipContents: clipContents,
            TextureRect: textureRect,
            Material: material,
            Layout: layout,
            TextureRefs: textureRefs,
            Text: text,
            NinePatch: ninePatch,
            Notices: notices,
            ShowBehindParent: showBehindParent,
            ZAsRelative: zAsRelative,
            ClipChildrenMode: clipChildrenMode,
            MouseFilter: mouseFilter,
            FocusMode: focusMode,
            MouseDefaultCursorShape: mouseDefaultCursorShape);
    }

    private static RuntimeSceneLayoutPropertiesSnapshot? DescribeLayoutProperties(Node node)
    {
        if (node is not Control control)
        {
            return null;
        }

        return new RuntimeSceneLayoutPropertiesSnapshot(
            MinimumSize: ToSnapshot(control.GetMinimumSize()),
            CombinedMinimumSize: ToSnapshot(control.GetCombinedMinimumSize()),
            CustomMinimumSize: ToSnapshot(control.CustomMinimumSize),
            SizeFlagsHorizontal: ReadIntProperty(control, "SizeFlagsHorizontal"),
            SizeFlagsVertical: ReadIntProperty(control, "SizeFlagsVertical"),
            SizeFlagsStretchRatio: ReadDoublePropertyNullable(control, "SizeFlagsStretchRatio"),
            LayoutDirection: FormatEnumToken(ReadProperty(control, "LayoutDirection"), string.Empty),
            ThemeTypeVariation: ReadProperty(control, "ThemeTypeVariation")?.ToString(),
            ContainerAlignment: DescribeContainerAlignment(control),
            FlowVertical: control is FlowContainer ? ReadBoolProperty(control, "Vertical") : null,
            ThemeConstants: LayoutThemeConstants(control));
    }

    private static int? DescribeContainerAlignment(Control control)
        => control switch
        {
            BoxContainer box => (int)box.Alignment,
            FlowContainer flow => (int)flow.Alignment,
            _ => ReadIntProperty(control, "Alignment"),
        };

    private static RuntimeSceneThemeConstantSnapshot[] LayoutThemeConstants(Control control)
        => [
            ThemeConstant(control, "separation"),
            ThemeConstant(control, "h_separation"),
            ThemeConstant(control, "v_separation"),
            ThemeConstant(control, "margin_left"),
            ThemeConstant(control, "margin_top"),
            ThemeConstant(control, "margin_right"),
            ThemeConstant(control, "margin_bottom"),
        ];

    private static RuntimeSceneThemeConstantSnapshot ThemeConstant(Control control, string name)
        => new(name, TryGetThemeConstant(control, name));

    private static double? TryGetThemeConstant(Control control, string name)
    {
        try
        {
            if (control.HasThemeConstant(name))
            {
                return control.GetThemeConstant(name);
            }
        }
        catch
        {
            // Theme APIs differ across Godot/MegaDot builds; omit unavailable constants.
        }

        return null;
    }

    private static RuntimeSceneMaterialPropertiesSnapshot? DescribeMaterialProperties(
        Node node,
        ICollection<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        if (node is not CanvasItem canvasItem)
        {
            return null;
        }

        var material = ReadProperty(canvasItem, "Material") as Resource;
        var shader = material is null ? null : ReadProperty(material, "Shader") as Resource;
        var shaderParameters = DescribeShaderParameters(material, shader, notices);
        if (material is null
            && shader is null
            && shaderParameters.Count == 0
            && ReadBoolProperty(canvasItem, "UseParentMaterial") is null)
        {
            return null;
        }

        return new RuntimeSceneMaterialPropertiesSnapshot(
            ResourceRef("Material", material),
            ReadBoolProperty(canvasItem, "UseParentMaterial"),
            ResourceRef("Shader", shader),
            shaderParameters,
            MaterialBlendMode(material));
    }

    private static IReadOnlyList<RuntimeSceneShaderParameterSnapshot> DescribeShaderParameters(
        Resource? material,
        Resource? shader,
        ICollection<RuntimeScenePropertyNoticeSnapshot> notices)
    {
        var result = Sts2ShaderMaterialInspector.Inspect(material, shader);
        foreach (var notice in result.Notices)
        {
            notices.Add(new RuntimeScenePropertyNoticeSnapshot(notice.Code, notice.Field, notice.Message));
        }

        return result.Parameters.Select(ToRuntimeSceneShaderParameter).ToArray();
    }

    private static RuntimeSceneShaderParameterSnapshot ToRuntimeSceneShaderParameter(
        Sts2ShaderParameterValue parameter)
        => new(
            parameter.Name,
            parameter.ValueKind,
            parameter.StringValue,
            parameter.NumberValue,
            parameter.BoolValue,
            parameter.ColorValue.HasValue ? ToSnapshot(parameter.ColorValue.Value) : null,
            parameter.Vector2Value.HasValue ? ToSnapshot(parameter.Vector2Value.Value) : null,
            ResourceRef(parameter.Name, parameter.ResourceValue));

    private static string? MaterialBlendMode(Resource? material)
    {
        if (material is null)
        {
            return null;
        }

        return FormatEnumToken(ReadProperty(material, "BlendMode"), string.Empty);
    }

    private static RuntimeSceneTextureRectPropertiesSnapshot? DescribeTextureRectProperties(Node node)
    {
        if (!Sts2LiveIntrospection.IsType(node, "Godot.TextureRect"))
        {
            return null;
        }

        return new RuntimeSceneTextureRectPropertiesSnapshot(
            FormatEnumToken(ReadProperty(node, "StretchMode"), "scale"),
            FormatEnumToken(ReadProperty(node, "ExpandMode"), "ignore-size"),
            ReadBoolProperty(node, "FlipH") ?? false,
            ReadBoolProperty(node, "FlipV") ?? false);
    }

    private static RuntimeSceneNinePatchPropertiesSnapshot? DescribeNinePatchProperties(
        Node node,
        IReadOnlyList<RuntimeSceneResourceRefSnapshot> textureRefs,
        bool lean = false)
    {
        if (node is not NinePatchRect ninePatch)
        {
            return null;
        }

        var modulate = ninePatch.Modulate;
        var selfModulate = ninePatch.SelfModulate;
        var effective = new Color(
            modulate.R * selfModulate.R,
            modulate.G * selfModulate.G,
            modulate.B * selfModulate.B,
            modulate.A * selfModulate.A);

        // The mirror needs only presence + the panel texture; the margins/axis-stretch are reflection
        // reads it never uses, so lean skips them (zeros/empty).
        return new RuntimeSceneNinePatchPropertiesSnapshot(
            Texture: textureRefs.FirstOrDefault(texture => string.Equals(texture.Field, "Texture", StringComparison.Ordinal)),
            DrawCenter: ninePatch.DrawCenter,
            PatchMargins: lean
                ? new RuntimeScenePatchMarginsSnapshot(0, 0, 0, 0)
                : new RuntimeScenePatchMarginsSnapshot(
                    ReadDoubleProperty(ninePatch, "PatchMarginLeft"),
                    ReadDoubleProperty(ninePatch, "PatchMarginTop"),
                    ReadDoubleProperty(ninePatch, "PatchMarginRight"),
                    ReadDoubleProperty(ninePatch, "PatchMarginBottom")),
            AxisStretchHorizontal: lean ? string.Empty : FormatAxisStretch(ReadProperty(ninePatch, "AxisStretchHorizontal")),
            AxisStretchVertical: lean ? string.Empty : FormatAxisStretch(ReadProperty(ninePatch, "AxisStretchVertical")),
            EffectiveModulate: ToSnapshot(effective));
    }

    private static RuntimeSceneTextPropertiesSnapshot? DescribeTextProperties(Node node, bool lean = false)
        => Sts2RuntimeSceneTextDiagnostics.Describe(node, lean);

    private static IReadOnlyList<RuntimeSceneResourceRefSnapshot> DescribeTextureRefs(
        Node node,
        List<RuntimeScenePropertyNoticeSnapshot> notices,
        bool lean = false)
    {
        // Lean: just the primary texture the mirror paints — `Texture` (Sprite2D/TextureRect/NinePatchRect)
        // and `TextureNormal` (TextureButton). The full sweep below probes 7 properties per node.
        string[] propertyNames = lean
            ?
            [
                "Texture",
                "TextureNormal",
            ]
            :
            [
                "Texture",
                "NormalTexture",
                "PressedTexture",
                "HoverTexture",
                "DisabledTexture",
                "FocusedTexture",
                "TextureUnder",
            ];

        var refs = new List<RuntimeSceneResourceRefSnapshot>();
        foreach (var propertyName in propertyNames)
        {
            try
            {
                var property = node.GetType().GetProperty(propertyName);
                if (property?.GetValue(node) is not Resource resource)
                {
                    continue;
                }

                refs.Add(ResourceRef(propertyName, resource)!);
            }
            catch (Exception ex)
            {
                notices.Add(new RuntimeScenePropertyNoticeSnapshot(
                    Code: "inaccessible",
                    Field: $"properties.textures.{propertyName}",
                    Message: ex.Message));
            }
        }

        return refs;
    }

    private static RuntimeSceneResourceRefSnapshot? ResourceRef(string field, Resource? resource)
        => resource is null
            ? null
            : new RuntimeSceneResourceRefSnapshot(
                Field: field,
                ResourcePath: string.IsNullOrWhiteSpace(resource.ResourcePath) ? string.Empty : resource.ResourcePath,
                ResourceType: resource.GetType().FullName ?? resource.GetType().Name,
                ResourceName: resource.ResourceName.ToString());

    internal static RuntimeSceneComputedTransformSnapshot DescribeComputedTransform(Node node)
    {
        var notices = new List<RuntimeScenePropertyNoticeSnapshot>();
        RuntimeSceneTransform2DSnapshot? localTransform = null;
        RuntimeSceneTransform2DSnapshot? globalTransform = null;
        RuntimeSceneRect2Snapshot? globalRect = null;
        RuntimeSceneRect2Snapshot? viewportClippedRect = null;

        if (node is CanvasItem canvasItem)
        {
            localTransform = ToSnapshot(canvasItem.GetTransform());
            globalTransform = ToSnapshot(canvasItem.GetGlobalTransform());
        }
        else
        {
            notices.Add(NotApplicable("computedTransform.localTransform", "transforms require a CanvasItem node."));
        }

        if (node is Control control)
        {
            var rect = control.GetGlobalRect();
            globalRect = ToSnapshot(rect);
            viewportClippedRect = ClipToViewport(rect);
        }
        else
        {
            notices.Add(NotApplicable(
                "computedTransform.globalRect",
                "global rect is only available for Control nodes and selected rect-backed CanvasItem nodes."));
        }

        return new RuntimeSceneComputedTransformSnapshot(
            LocalTransform: localTransform,
            GlobalTransform: globalTransform,
            GlobalRect: globalRect,
            ViewportClippedRect: viewportClippedRect,
            Notices: notices);
    }

    private static RuntimeSceneHoverTipSnapshot InspectHoverTip(Node root)
    {
        var candidates = EnumerateSubtree(root)
            .Where(IsHoverTipCandidate)
            .ToArray();
        var visibleHoverTips = candidates
            .Where(IsVisibleNode)
            .Select(candidate => new
            {
                Node = candidate,
                Labels = DescribeHoverTipLabels(candidate),
            })
            .ToArray();
        if (visibleHoverTips.Length == 0)
        {
            return new RuntimeSceneHoverTipSnapshot(
                Visible: false,
                NodePath: null,
                Title: null,
                Text: null,
                Labels: [],
                Notes: candidates.Length == 0
                    ? ["no-hover-tip-node-found"]
                    : ["hover-tip-node-not-visible"]);
        }

        var selected = visibleHoverTips.LastOrDefault(candidate => candidate.Labels.Length > 0)
            ?? visibleHoverTips[^1];
        var labels = selected.Labels;
        var title = labels.FirstOrDefault(IsTitleLabel) ?? labels.FirstOrDefault();
        var body = labels.FirstOrDefault(label => !ReferenceEquals(label, title) && IsBodyLabel(label))
            ?? labels.FirstOrDefault(label => !ReferenceEquals(label, title));

        return new RuntimeSceneHoverTipSnapshot(
            Visible: true,
            NodePath: NormalizeResolvedNodePath(selected.Node.GetPath().ToString()),
            Title: title?.Text,
            Text: body?.Text,
            Labels: labels,
            Notes: labels.Length == 0 ? ["hover-tip-visible-without-text-labels"] : []);
    }

    private static bool TryPresentationRect(Node node, out PresentationRectSnapshot rect)
    {
        Rect2? sourceRect = node switch
        {
            Control control when control.IsVisibleInTree() => control.GetGlobalRect(),
            CanvasItem canvasItem when canvasItem.IsVisibleInTree() => CanvasItemRect(canvasItem),
            _ => null,
        };
        if (sourceRect is not { } value || value.Size.X <= 0 || value.Size.Y <= 0)
        {
            rect = new PresentationRectSnapshot(0, 0, 0, 0);
            return false;
        }

        rect = new PresentationRectSnapshot(value.Position.X, value.Position.Y, value.Size.X, value.Size.Y);
        return true;
    }

    private static Rect2? CanvasItemRect(CanvasItem canvasItem)
    {
        if (canvasItem is not Node node)
        {
            return null;
        }

        var transform = canvasItem.GetGlobalTransform();
        var size = ReadProperty(node, "Size") switch
        {
            Vector2 vector => vector,
            _ => Vector2.Zero,
        };
        return size.X <= 0 || size.Y <= 0
            ? null
            : new Rect2(transform.Origin, size);
    }

    private static HoverTipRenderHintsSnapshot HoverTipRenderHints(Node node)
        => new(Visual: new PresentationVisualHintSnapshot(
            SourceNodePath: NormalizeResolvedNodePath(node.GetPath().ToString()),
            NodeType: node.GetType().FullName ?? node.GetType().Name,
            Texture: null,
            EffectiveModulate: node is CanvasItem canvasItem ? HtmlColor(EffectiveDrawModulate(canvasItem)) : null,
            ClipContents: Sts2ControlClipContents.Read(node),
            TextureRect: null,
            SourceKind: PresentationSourceKindSnapshot.RuntimeUi,
            Stability: PresentationStabilitySnapshot.ScreenInstance,
            Note: "Visible HoverTip overlay metadata is derived from the live runtime scene."));

    private static HoverTipRenderHintsSnapshot HoverTipPanelRenderHints(Node node)
    {
        var texture = HoverTipPanelTextureRef(node);
        return new HoverTipRenderHintsSnapshot(
            NinePatch: HoverTipNinePatchHint(node, texture),
            Visual: new PresentationVisualHintSnapshot(
                SourceNodePath: NormalizeResolvedNodePath(node.GetPath().ToString()),
                NodeType: node.GetType().FullName ?? node.GetType().Name,
                Texture: texture,
                EffectiveModulate: node is CanvasItem canvasItem ? HtmlColor(EffectiveDrawModulate(canvasItem)) : null,
                ClipContents: Sts2ControlClipContents.Read(node),
                TextureRect: null,
                SourceKind: PresentationSourceKindSnapshot.RuntimeUi,
                Stability: PresentationStabilitySnapshot.ScreenInstance,
                Note: "Visible HoverTip panel visual metadata is derived from the live runtime scene.",
                InheritedEffectiveModulate: node is CanvasItem inheritedCanvasItem ? HtmlColor(EffectiveDrawModulate(inheritedCanvasItem)) : null,
                ModulateMode: "multiply"));
    }

    private static LobbyPresentationNinePatchSnapshot? HoverTipNinePatchHint(
        Node node,
        PresentationVisualTextureSnapshot texture)
    {
        if (node is not NinePatchRect ninePatch)
        {
            return null;
        }

        return new LobbyPresentationNinePatchSnapshot(
            texture.ResourcePath,
            ninePatch.DrawCenter,
            new LobbyPresentationPatchMarginsSnapshot(
                ReadDoubleProperty(ninePatch, "PatchMarginLeft"),
                ReadDoubleProperty(ninePatch, "PatchMarginTop"),
                ReadDoubleProperty(ninePatch, "PatchMarginRight"),
                ReadDoubleProperty(ninePatch, "PatchMarginBottom")),
            FormatAxisStretch(ReadProperty(ninePatch, "AxisStretchHorizontal")),
            FormatAxisStretch(ReadProperty(ninePatch, "AxisStretchVertical")),
            HtmlColor(EffectiveDrawModulate(ninePatch)));
    }

    private static PresentationVisualTextureSnapshot HoverTipPanelTextureRef(Node node)
    {
        var resource = ResourceRef("Texture", ReadProperty(node, "Texture") as Resource);
        return new PresentationVisualTextureSnapshot(
            resource?.Field ?? "Texture",
            string.IsNullOrWhiteSpace(resource?.ResourcePath) ? HoverTipPanelTexture : resource!.ResourcePath,
            resource?.ResourceType,
            resource?.ResourceName);
    }

    private sealed record HoverTipRenderHintsSnapshot(
        LobbyPresentationNinePatchSnapshot? NinePatch = null,
        PresentationVisualHintSnapshot? Visual = null);

    private static string StablePathHash(string value)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..12];
    }

    private static RuntimeSceneHoverLabelSnapshot[] DescribeHoverTipLabels(Node hoverTip)
        => EnumerateSubtree(hoverTip)
            .Where(node => !ReferenceEquals(node, hoverTip))
            .Select(TryDescribeHoverTipLabel)
            .Where(label => label is not null)
            .Select(label => label!)
            .ToArray();

    private static bool IsHoverTipCandidate(Node node)
    {
        var nodeName = node.Name.ToString();
        if (string.Equals(nodeName, "HoverTip", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var nodeType = node.GetType().FullName ?? node.GetType().Name;
        return EndsWithHoverTipType(nodeType);
    }

    private static bool EndsWithHoverTipType(string? value)
        => value?.EndsWith(".NHoverTip", StringComparison.OrdinalIgnoreCase) == true
            || value?.EndsWith(".HoverTip", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsVisibleNode(Node node)
        => node is not CanvasItem canvasItem || canvasItem.IsVisibleInTree();

    private static RuntimeSceneHoverLabelSnapshot? TryDescribeHoverTipLabel(Node node)
    {
        if (!IsVisibleNode(node))
        {
            return null;
        }

        var text = Sts2RuntimeSceneTextDiagnostics.Describe(node);
        var value = text?.Text ?? text?.RawText;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return new RuntimeSceneHoverLabelSnapshot(
            NormalizeResolvedNodePath(node.GetPath().ToString()),
            node.Name.ToString(),
            value.Trim());
    }

    private static bool IsTitleLabel(RuntimeSceneHoverLabelSnapshot label)
        => label.Name.Contains("Title", StringComparison.OrdinalIgnoreCase)
            || label.Name.Contains("Header", StringComparison.OrdinalIgnoreCase)
            || label.Name.Contains("Name", StringComparison.OrdinalIgnoreCase);

    private static bool IsBodyLabel(RuntimeSceneHoverLabelSnapshot label)
        => label.Name.Contains("Body", StringComparison.OrdinalIgnoreCase)
            || label.Name.Contains("Description", StringComparison.OrdinalIgnoreCase)
            || label.Name.Contains("Text", StringComparison.OrdinalIgnoreCase);

    private static RuntimeSceneRect2Snapshot? ClipToViewport(Rect2 rect)
    {
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (root is null)
        {
            return null;
        }

        var viewport = root.GetVisibleRect();
        var left = Math.Max(rect.Position.X, viewport.Position.X);
        var top = Math.Max(rect.Position.Y, viewport.Position.Y);
        var right = Math.Min(rect.Position.X + rect.Size.X, viewport.Position.X + viewport.Size.X);
        var bottom = Math.Min(rect.Position.Y + rect.Size.Y, viewport.Position.Y + viewport.Size.Y);
        if (right < left || bottom < top)
        {
            return new RuntimeSceneRect2Snapshot(
                Position: new RuntimeSceneVector2Snapshot(left, top),
                Size: new RuntimeSceneVector2Snapshot(0, 0));
        }

        return new RuntimeSceneRect2Snapshot(
            Position: new RuntimeSceneVector2Snapshot(left, top),
            Size: new RuntimeSceneVector2Snapshot(right - left, bottom - top));
    }

    private static RuntimeSceneVector2Snapshot ToSnapshot(Vector2 value)
        => new(value.X, value.Y);

    private static RuntimeSceneColorSnapshot ToSnapshot(Color value)
        => new(value.R, value.G, value.B, value.A, HtmlColor(value));

    private static Color Multiply(Color left, Color right)
        => new(
            left.R * right.R,
            left.G * right.G,
            left.B * right.B,
            left.A * right.A);

    private static Color EffectiveDrawModulate(CanvasItem canvasItem)
    {
        var effective = Multiply(canvasItem.Modulate, canvasItem.SelfModulate);
        for (var parent = canvasItem.GetParent(); parent is not null; parent = parent.GetParent())
        {
            if (parent is CanvasItem parentCanvasItem)
            {
                effective = Multiply(parentCanvasItem.Modulate, effective);
            }
        }

        return effective;
    }

    private static string HtmlColor(Color value)
    {
        var html = value.ToHtml(includeAlpha: true);
        return html.StartsWith('#') ? html : $"#{html}";
    }

    private static object? ReadProperty(object source, string name)
    {
        try
        {
            var property = source.GetType().GetProperty(name);
            if (property is not null)
            {
                return property.GetValue(source);
            }

            if (source is GodotObject godotObject)
            {
                foreach (var candidate in CandidateGodotPropertyNames(name))
                {
                    var value = godotObject.Get(candidate);
                    if (value.VariantType == Variant.Type.Nil)
                    {
                        continue;
                    }

                    return value.VariantType switch
                    {
                        Variant.Type.String => value.AsString().ToString(),
                        Variant.Type.StringName => value.AsStringName().ToString(),
                        Variant.Type.Bool => value.AsBool(),
                        Variant.Type.Float => value.AsDouble(),
                        Variant.Type.Int => value.AsInt32(),
                        Variant.Type.Object => value.AsGodotObject(),
                        _ => value.ToString(),
                    };
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static IEnumerable<string> CandidateGodotPropertyNames(string name)
    {
        yield return name;
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var character = name[i];
            if (char.IsUpper(character) && i > 0)
            {
                builder.Append('_');
            }
            builder.Append(char.ToLowerInvariant(character));
        }
        yield return builder.ToString();
    }

    private static double ReadDoubleProperty(object source, string name)
    {
        var value = ReadProperty(source, name);
        return value switch
        {
            byte typed => typed,
            sbyte typed => typed,
            short typed => typed,
            ushort typed => typed,
            int typed => typed,
            uint typed => typed,
            long typed => typed,
            ulong typed => typed,
            float typed => typed,
            double typed => typed,
            decimal typed => (double)typed,
            _ => 0,
        };
    }

    private static double? ReadDoublePropertyNullable(object source, string name)
    {
        var value = ReadProperty(source, name);
        return value switch
        {
            byte typed => typed,
            sbyte typed => typed,
            short typed => typed,
            ushort typed => typed,
            int typed => typed,
            uint typed => typed,
            long typed => typed,
            ulong typed => typed,
            float typed => typed,
            double typed => typed,
            decimal typed => (double)typed,
            _ => null,
        };
    }

    private static int? ReadIntProperty(object source, string name)
    {
        var value = ReadProperty(source, name);
        return value switch
        {
            byte typed => typed,
            sbyte typed => typed,
            short typed => typed,
            ushort typed => typed,
            int typed => typed,
            uint typed => checked((int)typed),
            long typed => checked((int)typed),
            ulong typed => checked((int)typed),
            Enum typed => Convert.ToInt32(typed),
            _ => null,
        };
    }

    private static bool? ReadBoolProperty(object source, string name)
    {
        var value = ReadProperty(source, name);
        return value switch
        {
            bool typed => typed,
            _ => null,
        };
    }

    private static string FormatAxisStretch(object? value)
    {
        var raw = value?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "stretch";
        }

        return raw.Trim() switch
        {
            "TileFit" => "tile-fit",
            "Tile" => "tile",
            "Stretch" => "stretch",
            var other => other.Replace("_", "-", StringComparison.Ordinal).ToLowerInvariant(),
        };
    }

    private static string FormatEnumToken(object? value, string fallback)
    {
        var raw = value?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return ToKebabCase(raw.Trim());
    }

    private static string ToKebabCase(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            var character = value[i];
            if (character == '_')
            {
                builder.Append('-');
                continue;
            }

            if (char.IsUpper(character) && i > 0 && builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static RuntimeSceneTransform2DSnapshot ToSnapshot(Transform2D value)
        => new(
            XAxis: ToSnapshot(value.X),
            YAxis: ToSnapshot(value.Y),
            Origin: ToSnapshot(value.Origin));

    private static RuntimeSceneRect2Snapshot ToSnapshot(Rect2 value)
        => new(
            Position: ToSnapshot(value.Position),
            Size: ToSnapshot(value.Size));

    private static RuntimeScenePropertyNoticeSnapshot NotApplicable(string field, string message)
        => new(
            Code: "not_applicable",
            Field: field,
            Message: message);

    private static string NormalizeRequestedNodePath(string nodePath)
    {
        if (string.IsNullOrWhiteSpace(nodePath))
        {
            return "/root";
        }

        var trimmed = nodePath.Trim();
        if (!trimmed.StartsWith("/root", StringComparison.Ordinal))
        {
            return trimmed.StartsWith("/", StringComparison.Ordinal)
                ? $"/root{trimmed}"
                : $"/root/{trimmed}";
        }

        return trimmed;
    }

    private static string NormalizeResolvedNodePath(string nodePath)
    {
        return string.IsNullOrWhiteSpace(nodePath) ? "/root" : nodePath.Trim();
    }

    private sealed record RootResolution(Node? Node, RuntimeSceneFailure? Error)
    {
        public static RootResolution Success(Node node) => new(node, null);

        public static RootResolution Failure(RuntimeSceneFailureCode code, string message, IReadOnlyList<RuntimeSceneDetail> details)
            => new(null, new RuntimeSceneFailure(code, message, details));
    }

    private sealed record TargetResolution(Node? Node, RuntimeSceneFailure? Error)
    {
        public static TargetResolution Success(Node node) => new(node, null);

        public static TargetResolution Failure(RuntimeSceneFailureCode code, string message, IReadOnlyList<RuntimeSceneDetail> details)
            => new(null, new RuntimeSceneFailure(code, message, details));
    }

    private sealed record ScriptInfo(string? Path, string? Type)
    {
        public static ScriptInfo Empty { get; } = new(null, null);
    }

    private sealed record HoverTipLabelNode(Node Node, RuntimeSceneHoverLabelSnapshot Label);
}
