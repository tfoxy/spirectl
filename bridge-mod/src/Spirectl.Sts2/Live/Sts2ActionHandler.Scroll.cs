using System.Globalization;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using Spirectl.Sts2.Core.Actions;

namespace Spirectl.Sts2.Live;

// ABSOLUTE SCROLL (`set-scroll-offset`) — park a scrollable surface at a named container-local Y.
//
// The two surfaces STS2 scrolls this way are the travel MAP (its `TheMap` container, which carries every path,
// point and quill stroke as a child) and a CARD GRID (`card_grid.tscn`'s `ScrollContainer` — the deck / draw /
// discard / exhaust views). Both work the same way on screen: the surface holds a scroll TARGET, the container's
// own Y eases toward it every frame, and the surface pulls the target back inside its limits when it lands
// outside. This action writes that target, after clamping to the SAME limits, and reports the clamped value back
// — which is the whole point of it: a client that led the scroll locally has no other way to learn that the game
// refused the last 40px of a flick.
//
// Addressing is `elementId` — the container node's live instance id, exactly as select-map-node and
// hover-element take it. The owning surface is found by walking UP from that node, and the write is refused
// unless the addressed node really is that surface's own scroll container, so a stray id under a map screen
// cannot drive the map.
//
// The pure half (kill switch, offset parse, clamp) is Sts2ScrollOffsetMath, which is Godot-free and unit-tested.
public sealed partial class Sts2ActionHandler
{
    // The two surfaces' scroll-target members, and the container each one moves. These are the names this action
    // must write for it to do anything at all; nothing else about either surface is read.
    private const string MapScrollTargetMember = "_targetDragPos";
    private const string MapScrollContainerMember = "_mapContainer";
    private const string MapScrollLimitLoMember = "_scrollLimitBottom";
    private const string MapScrollLimitHiMember = "_scrollLimitTop";
    private const string GridScrollTargetMember = "_targetDrag";
    private const string GridScrollContainerMember = "_scrollContainer";
    private const string GridScrollLimitLoMember = "ScrollLimitBottom";
    private const string GridScrollLimitHiMember = "ScrollLimitTop";

    // How far up the tree the owning surface may sit above the addressed container. Both are direct children
    // today; the budget is slack for a scene re-nest, not an invitation to address something far away.
    private const int ScrollSurfaceSearchDepth = 6;

    private ActionExecutionResult ExecuteSetScrollOffset(SemanticActionRequest request)
    {
        if (!Sts2ScrollOffsetMath.Enabled)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.NotEnabled,
                message: "set-scroll-offset is disabled on this host (SPIRECTL_SCROLL_OFFSET_ACTION).",
                details: [new ActionFailureDetail("element_id", request.ElementId ?? string.Empty, "Unset SPIRECTL_SCROLL_OFFSET_ACTION to re-enable, or drive the surface with relative wheel/drag input.")]);
        }

        var elementId = (request.ElementId ?? ResolveRequestValue(request, "elementId"))?.Trim();
        if (string.IsNullOrWhiteSpace(elementId) || !ulong.TryParse(elementId, out var instanceId))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "set-scroll-offset requires the scroll container's numeric elementId.",
                details: [new ActionFailureDetail("element_id", elementId ?? string.Empty, "Pass the live node instance id of the map container or a card grid's scroll container.")]);
        }

        var rawOffset = ResolveRequestValue(request, Sts2ScrollOffsetMath.OffsetArgumentKey);
        if (!Sts2ScrollOffsetMath.TryParseOffset(rawOffset, out var wantedY))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: $"set-scroll-offset requires a finite '{Sts2ScrollOffsetMath.OffsetArgumentKey}' value.",
                details: [new ActionFailureDetail(Sts2ScrollOffsetMath.OffsetArgumentKey, rawOffset ?? string.Empty, "Pass the wanted container-local Y as a plain number.")]);
        }

        if (!GodotObject.IsInstanceIdValid(instanceId) || GodotObject.InstanceFromId(instanceId) is not Node container)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.StaleId,
                message: $"element '{elementId}' is no longer a live node.",
                details: [new ActionFailureDetail("element_id", elementId, "Re-address the container from the current scene stream.")]);
        }

        var surface = ResolveScrollSurface(container, out var kind);
        if (surface is null || kind == ScrollSurfaceKind.None)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: $"element '{elementId}' is not the scroll container of a scrollable surface.",
                details: [new ActionFailureDetail("element_id", elementId, "set-scroll-offset addresses the map container or a card grid's scroll container.")]);
        }

        if (surface is CanvasItem visual && !visual.IsVisibleInTree())
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.NotVisible,
                message: $"the surface owning element '{elementId}' is not visible in the current scene tree.",
                details: [new ActionFailureDetail("element_id", elementId, "Retry while the map screen / card grid is on screen.")]);
        }

        var (limitA, limitB) = ScrollLimitsFor(surface, kind);
        var clampedY = Sts2ScrollOffsetMath.Clamp(wantedY, limitA, limitB);

        if (!ApplyScrollTarget(surface, kind, clampedY))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "the live surface did not expose a writable scroll target.",
                details: [new ActionFailureDetail("element_id", elementId, $"Missing {(kind == ScrollSurfaceKind.Map ? MapScrollTargetMember : GridScrollTargetMember)} on the resolved surface.")]);
        }

        // Deliberately NOT logged at Info: a leading client sends one of these per gesture frame, and a per-frame
        // bridge log line is a measurable cost on the game thread.
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:set-scroll-offset:{request.RequestId}",
            kind: request.Kind,
            message: $"Set {(kind == ScrollSurfaceKind.Map ? "map" : "grid")} scroll offset to {clampedY.ToString("0.##", CultureInfo.InvariantCulture)}.",
            values: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Sts2ScrollOffsetMath.ResultOffsetKey] = clampedY.ToString("R", CultureInfo.InvariantCulture),
                [Sts2ScrollOffsetMath.ResultRequestedKey] = wantedY.ToString("R", CultureInfo.InvariantCulture),
                [Sts2ScrollOffsetMath.ResultSurfaceKey] = kind == ScrollSurfaceKind.Map ? "map" : "grid",
            });
    }

    // The surface that OWNS `container`, walking up a bounded number of parents — and only when `container` is
    // that surface's own scroll container, so the id really names the thing the write moves.
    private static Node? ResolveScrollSurface(Node container, out ScrollSurfaceKind kind)
    {
        kind = ScrollSurfaceKind.None;
        Node? current = container;
        for (var depth = 0; current is not null && depth <= ScrollSurfaceSearchDepth; depth++)
        {
            switch (current)
            {
                case NMapScreen map
                    when ReferenceEquals(Sts2LiveIntrospection.GetMemberValue(map, MapScrollContainerMember), container):
                    kind = ScrollSurfaceKind.Map;
                    return map;
                case NCardGrid grid
                    when ReferenceEquals(Sts2LiveIntrospection.GetMemberValue(grid, GridScrollContainerMember), container):
                    kind = ScrollSurfaceKind.Grid;
                    return grid;
            }

            current = current.GetParent();
        }

        return null;
    }

    // The surface's own scroll window, in the order the surface spells it (the two ends are NOT sorted — the map's
    // window runs low→high and a grid's runs high→low; Sts2ScrollOffsetMath.Clamp takes either).
    private static (double A, double B) ScrollLimitsFor(Node surface, ScrollSurfaceKind kind)
    {
        if (kind == ScrollSurfaceKind.Map)
        {
            return (
                ReadConstant(surface.GetType(), MapScrollLimitLoMember, Sts2ScrollOffsetMath.DefaultMapLimitLo),
                ReadConstant(surface.GetType(), MapScrollLimitHiMember, Sts2ScrollOffsetMath.DefaultMapLimitHi));
        }

        var lo = AsDouble(Sts2LiveIntrospection.GetMemberValue(surface, GridScrollLimitLoMember));
        var hi = AsDouble(Sts2LiveIntrospection.GetMemberValue(surface, GridScrollLimitHiMember));
        // A grid that could not report its window is not clamped by us at all — the surface's own pull-back still
        // applies, and inventing a window here would be worse than deferring to it.
        return (lo ?? double.NegativeInfinity, hi ?? double.PositiveInfinity);
    }

    private static bool ApplyScrollTarget(Node surface, ScrollSurfaceKind kind, double value)
    {
        if (kind == ScrollSurfaceKind.Map)
        {
            // The map's target is a Vector2 whose X the surface never scrolls — carry it through rather than
            // zeroing it, so this action can only ever move the axis it claims to.
            var currentX = Sts2LiveIntrospection.GetMemberValue(surface, MapScrollTargetMember) is Vector2 current
                ? current.X
                : 0f;
            return Sts2LiveIntrospection.TrySetMemberValue(
                surface,
                MapScrollTargetMember,
                new Vector2(currentX, (float)value));
        }

        return Sts2LiveIntrospection.TrySetMemberValue(surface, GridScrollTargetMember, (float)value);
    }

    // A private compile-time constant is not an instance member, so the ordinary introspection helper cannot see
    // it; read it off the type's literal instead and fall back to the caller's value. Keeping the read here means
    // a future re-tune of the window inside the game is picked up rather than silently disagreed with.
    private static double ReadConstant(Type type, string name, double fallback)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var field = current.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (field is null || !field.IsLiteral)
            {
                continue;
            }

            try
            {
                return AsDouble(field.GetRawConstantValue()) ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }

        return fallback;
    }

    private static double? AsDouble(object? value) => value switch
    {
        float f when !float.IsNaN(f) && !float.IsInfinity(f) => f,
        double d when !double.IsNaN(d) && !double.IsInfinity(d) => d,
        int i => i,
        _ => null,
    };
}
