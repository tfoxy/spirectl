using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using Godot;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace Spirectl.Sts2.Live;

public sealed partial class Sts2ActionHandler
{
    // The members an embedded-profile dispatch arm reaches. The rest of this area is in
    // Sts2ActionHandler.ShopMapLobby.Full.cs, moved verbatim, which the embedded profile does not compile.
    private ActionExecutionResult ExecuteMouseClick(SemanticActionRequest request)
    {
        // Coordinates win when supplied (empty-space / raw fallback); otherwise resolve the element id to the
        // live node's rect so the browser can click by stable identity (robust to client-side CSS scaling).
        Vector2 position;
        if (request.MouseX is not null && request.MouseY is not null)
        {
            if (request.MouseX < 0 || request.MouseY < 0)
            {
                return ActionExecutionResult.Failure(
                    kind: request.Kind,
                    code: ActionFailureCode.InvalidAction,
                    message: "mouse-click requires non-negative viewport coordinates.",
                    details:
                    [
                        new ActionFailureDetail(
                            Field: request.MouseX < 0 ? "x" : "y",
                            Value: request.MouseX < 0 ? request.MouseX.Value.ToString() : request.MouseY.Value.ToString(),
                            Note: "Use screenshot/viewport pixel coordinates at or above zero."),
                    ]);
            }

            position = new Vector2(request.MouseX.Value, request.MouseY.Value);
        }
        else if (!string.IsNullOrWhiteSpace(request.ElementId))
        {
            if (!TryResolveElementPoint(request, out position, out _, out var failure))
            {
                return failure!;
            }
        }
        else
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "mouse-click requires explicit viewport coordinates or an element id.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "x",
                        Value: string.Empty,
                        Note: "Provide viewport coordinates (x/y) or an elementId."),
                ]);
        }

        var rawButton = request.MouseButton ?? RawMouseButtonKind.Left;
        var mouseButton = ToGodotMouseButton(rawButton);
        // The click point is in canvas/GUI space (design coords or a Control's global rect); convert to window
        // pixels so it lands correctly when the game window != the 1920x1080 base size.
        var inject = Sts2InputCoordinates.CanvasToWindow(position);

        // COALESCED WHEEL TICKS (`count`, optional). A browser scrolling a long list emits far more wheel notches
        // per second than the input queue can inject one-per-message without falling behind (each injection blocks
        // the game thread for up to a frame), so the client may fold several consecutive same-direction notches into
        // ONE message and say how many it stands for. Only a full click (MousePressed null) can repeat — a press or
        // a release is an edge, not a quantity — and only a WHEEL button, because repeating a left/right click would
        // silently multiply a destructive action. Absent/1/unparseable ⇒ exactly one tick, byte-identical to the
        // pre-`count` behaviour. Clamped to 20 so a malformed client can't stall the game thread.
        var clickCount = ReadWheelCount(request, rawButton);
        Input.WarpMouse(inject);
        // MousePressed selects the gesture: null = a full click (down+up); true = press/hold (begins a drag —
        // the button stays down so the following hover/move stream injects as drag-motion); false = release.
        string gesture;
        if (request.MousePressed is null)
        {
            // A motion before the press lets Godot's hover-gated controls register the pointer over them first.
            InjectMotion(inject, (MouseButtonMask)0);
            for (var i = 0; i < clickCount; i++)
            {
                Input.ParseInputEvent(CreateMouseClickEvent(inject, mouseButton, pressed: true));
                Input.ParseInputEvent(CreateMouseClickEvent(inject, mouseButton, pressed: false));
            }
            _heldMouseButton = null;
            gesture = clickCount > 1 ? $"click x{clickCount}" : "click";
        }
        else if (request.MousePressed.Value)
        {
            InjectMotion(inject, (MouseButtonMask)0);
            Input.ParseInputEvent(CreateMouseClickEvent(inject, mouseButton, pressed: true));
            _heldMouseButton = mouseButton;
            gesture = "press";
        }
        else
        {
            // Move to the release point with the button still held (so the drag tracks to here), then release.
            InjectMotion(inject, ToMouseButtonMask(mouseButton));
            Input.ParseInputEvent(CreateMouseClickEvent(inject, mouseButton, pressed: false));
            _heldMouseButton = null;
            gesture = "release";
        }

        var buttonName = rawButton.ToString().ToLowerInvariant();
        var message = $"Injected raw {buttonName} {gesture} at canvas ({position.X:0},{position.Y:0}) → window ({inject.X:0},{inject.Y:0}).";
        _logStream.Write(BridgeLogLevel.Info, "bridge.action", message);
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:mouse-click:{request.RequestId}",
            kind: request.Kind,
            message: message,
            provisional: true);
    }

    // How many identical ticks this mouse-click message stands for. Reads the optional scalar `count` off the
    // request's Values bag (no new field on the record — the bag is exactly the channel for an action-specific
    // scalar), and refuses to repeat anything but a wheel notch. Anything unparseable, absent, or below 1 answers 1.
    internal const int MaxWheelClickCount = 20;

    internal static int ReadWheelCount(SemanticActionRequest request, RawMouseButtonKind button)
    {
        if (button != RawMouseButtonKind.WheelUp && button != RawMouseButtonKind.WheelDown)
        {
            return 1;
        }

        if (request.Values is null || !request.Values.TryGetValue("count", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return 1;
        }

        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var count))
        {
            return 1;
        }

        return count < 1 ? 1 : count > MaxWheelClickCount ? MaxWheelClickCount : count;
    }

    private ActionExecutionResult ExecuteSelectMapNode(SemanticActionRequest request)
    {
        var nodeId = request.MapNodeId?.Trim();
        // ELEMENT-ADDRESSED ALTERNATIVE (browser mirror). A mirror client renders the live SCENE and knows a map
        // point only by the node id the scene watcher streamed for it — which IS that node's GetInstanceId — so it
        // cannot derive the row/col of "map-node:{row}:{col}". select-map-node therefore also accepts an
        // `elementId` (the same addressing the element-addressed pointer input uses — see TryResolveElementPoint),
        // resolved to the live NMapPoint and then run through the EXACT same path as a node id: same travelable
        // gate, same ownership rules, same vote. `mapNodeId` is unchanged and still wins when both are present.
        var elementId = (request.ElementId ?? ResolveRequestValue(request, "elementId"))?.Trim();
        if (string.IsNullOrWhiteSpace(nodeId) && string.IsNullOrWhiteSpace(elementId))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "select-map-node requires an executable map node id from the current screen.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "node_id",
                        Value: string.Empty,
                        Note: "Provide a stable map node id from state.choices[].id or availableActions[].arguments.mapNodeId, or a live map point's elementId."),
                ]);
        }

        // Whichever addressing the caller used is what every failure detail below reports back.
        var usesElementId = string.IsNullOrWhiteSpace(nodeId);
        var requestedField = usesElementId ? "element_id" : "node_id";
        var requestedRef = (usesElementId ? elementId : nodeId) ?? string.Empty;

        if (!TryResolveMapContext(out var mapContext))
        {
            return MapActionUnavailable(
                request.Kind,
                "select-map-node is only available on the live map screen when travel is enabled.",
                requestedField,
                requestedRef,
                "Retry when state.screen.id is map and availableActions includes select-map-node.");
        }

        var requestedPlayerId = request.Perspective?.PlayerId;
        // A synthetic host-local seat (browser player) is a real participant in the
        // run-global map vote, but has no NMapScreen of its own; route it to the vote
        // injection instead of failing WrongPlayer like a peerless remote client would.
        var isHostLocalSeatRequest = !string.IsNullOrWhiteSpace(requestedPlayerId)
            && !string.Equals(requestedPlayerId, mapContext.LocalPlayerId, StringComparison.Ordinal)
            && IsHostLocalPlayerId(requestedPlayerId);
        if (BuildOwnershipFailure(
                request,
                new ActionOwnershipContext(
                    requestedPlayerId,
                    isHostLocalSeatRequest ? requestedPlayerId : mapContext.LocalPlayerId,
                    mapContext.LocalPlayerId,
                    ResolveRunHostPlayerId(mapContext.LocalPlayerId),
                    isHostLocalSeatRequest ? [requestedPlayerId!] : [],
                    mapContext.Screen.ScreenType,
                    "select-map-node"),
                out var ownershipFailure))
        {
            return ownershipFailure;
        }

        if (!isHostLocalSeatRequest
            && !string.IsNullOrWhiteSpace(requestedPlayerId)
            && !string.Equals(requestedPlayerId, mapContext.LocalPlayerId, StringComparison.Ordinal))
        {
            return MapActionUnavailable(
                request.Kind,
                "select-map-node is only available for the local player on the active map screen.",
                "player_id",
                requestedPlayerId,
                $"localPlayerId={mapContext.LocalPlayerId ?? "unknown"}.");
        }

        ResolvedMapNode? resolvedNode;
        if (usesElementId)
        {
            if (!TryResolveMapNodeByElementId(mapContext, requestedRef, out resolvedNode, out var elementFailure))
            {
                return elementFailure;
            }
        }
        else if (!mapContext.NodesById.TryGetValue(nodeId!, out resolvedNode))
        {
            return MapActionUnavailable(
                request.Kind,
                "select-map-node requires a visible map node id from the current screen.",
                "node_id",
                requestedRef,
                "Provide a stable map node id from state.choices[].id or availableActions[].arguments.mapNodeId.");
        }

        if (!Sts2ActionCatalog.CanSelectMapNode(
                resolvedNode.Snapshot,
                mapContext.MapScreen.IsTravelEnabled,
                mapContext.MapScreen.IsTraveling))
        {
            return MapActionUnavailable(
                request.Kind,
                "select-map-node requires a currently travelable map node.",
                requestedField,
                requestedRef,
                "Retry with a travelable node id from availableActions[].arguments.mapNodeId after map travel becomes available.");
        }

        if (isHostLocalSeatRequest)
        {
            return ExecuteHostLocalSeatMapVote(request, requestedPlayerId!, resolvedNode);
        }

        mapContext.MapScreen.OnMapPointSelectedLocally(resolvedNode.Node);
        _logStream.Write(BridgeLogLevel.Info, "bridge.action", $"Selected map node '{resolvedNode.Snapshot.Label}'.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:select-map-node:{request.RequestId}",
            kind: request.Kind,
            message: $"Selected map node '{resolvedNode.Snapshot.Label}'.");
    }

    // Resolve an ELEMENT id — a live node's Godot GetInstanceId, exactly what Sts2RuntimeSceneWatcher streams as a
    // scene node's `id` — to one of the CURRENT map screen's points. The tapped element is usually a DESCENDANT of
    // the map point (its icon / label / glow), so walk the ancestor chain and take the first node that IS one of
    // this screen's points, matched by INSTANCE IDENTITY against _mapPointDictionary's values (never by name or
    // position). Fails closed: an element from another screen, a stale id, or a non-node id resolves to nothing.
    private bool TryResolveMapNodeByElementId(
        MapActionContext mapContext,
        string elementId,
        [NotNullWhen(true)] out ResolvedMapNode? resolved,
        [NotNullWhen(false)] out ActionExecutionResult? failure)
    {
        resolved = null;
        failure = null;

        if (!ulong.TryParse(elementId, out var instanceId))
        {
            failure = ActionExecutionResult.Failure(
                kind: SemanticActionKind.SelectMapNode,
                code: ActionFailureCode.InvalidAction,
                message: "select-map-node requires a numeric element id (a live scene node's instance id).",
                details: [new ActionFailureDetail("element_id", elementId, "Use the streamed scene node id of a map point.")]);
            return false;
        }

        if (!GodotObject.IsInstanceIdValid(instanceId) || GodotObject.InstanceFromId(instanceId) is not Node element)
        {
            failure = ActionExecutionResult.Failure(
                kind: SemanticActionKind.SelectMapNode,
                code: ActionFailureCode.StaleId,
                message: $"select-map-node element '{elementId}' is no longer a live node.",
                details: [new ActionFailureDetail("element_id", elementId, "Re-read the current scene and retry with a live map point id.")]);
            return false;
        }

        var pointsByInstanceId = new Dictionary<ulong, ResolvedMapNode>();
        foreach (var node in mapContext.NodesById.Values)
        {
            pointsByInstanceId[node.Node.GetInstanceId()] = node;
        }

        for (Node? current = element; current is not null; current = current.GetParent())
        {
            if (pointsByInstanceId.TryGetValue(current.GetInstanceId(), out var hit))
            {
                resolved = hit;
                return true;
            }
        }

        failure = MapActionUnavailable(
            SemanticActionKind.SelectMapNode,
            "select-map-node requires an element that belongs to a map point on the current screen.",
            "element_id",
            elementId,
            "Tap a map point (or use availableActions[].arguments.mapNodeId).");
        return false;
    }

    // Casts a map-travel vote for a peerless synthetic seat, exactly as
    // NMapScreen.OnMapPointSelectedLocally does for the real local player: enqueue a
    // VoteForMapCoordAction with the seat's Player. On the host this enqueues locally
    // (no network peer needed); once every player has voted the host RNG-picks one and
    // the party travels.
    private ActionExecutionResult ExecuteHostLocalSeatMapVote(
        SemanticActionRequest request,
        string seatPlayerId,
        ResolvedMapNode resolvedNode)
    {
        var manager = RunManager.Instance;
        var runState = manager?.DebugOnlyGetState();
        var seatPlayer = ulong.TryParse(seatPlayerId.AsSpan(2), out var seatNetId)
            ? runState?.GetPlayer(seatNetId)
            : null;
        var synchronizer = manager?.MapSelectionSynchronizer;
        if (manager is null || runState is null || seatPlayer is null || synchronizer is null)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "The live run did not expose the map synchronizer or the seat's run player.",
                details: [new ActionFailureDetail("player_id", seatPlayerId, "Retry when the run and its map are active.")]);
        }

        try
        {
            var source = new MapLocation(runState.CurrentMapCoord, runState.CurrentActIndex);
            var vote = new MapVote
            {
                coord = new MapCoord(resolvedNode.Col, resolvedNode.Row),
                mapGenerationCount = synchronizer.MapGenerationCount,
            };
            manager.ActionQueueSynchronizer.RequestEnqueue(new VoteForMapCoordAction(seatPlayer, source, vote));
        }
        catch (Exception ex)
        {
            var reason = (ex as System.Reflection.TargetInvocationException)?.InnerException?.Message ?? ex.Message;
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: $"Casting the map vote for the host-local seat failed: {reason}",
                details: [new ActionFailureDetail("node_id", resolvedNode.Snapshot.Id, "The map synchronizer rejected the vote.")]);
        }

        _logStream.Write(
            BridgeLogLevel.Info,
            "bridge.action",
            $"Voted map node '{resolvedNode.Snapshot.Id}' for host-local seat {seatPlayerId}.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:select-map-node:{request.RequestId}",
            kind: request.Kind,
            message: $"Voted map node '{resolvedNode.Snapshot.Label}' for {seatPlayerId}.");
    }

}
