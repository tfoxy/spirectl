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

    private ActionExecutionResult ExecuteShopChoice(SemanticActionRequest request, string choiceId)
    {
        if (!TryResolveShopContext(out var shopContext))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose is only available on the live shop screen for visible merchant slots.",
                "choice_id",
                choiceId,
                "Retry when state.screen.id is shop and availableActions includes choose.");
        }

        if (!shopContext.ChoicesById.TryGetValue(choiceId, out var shopChoice))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose requires a visible shop choice id from the current screen.",
                "choice_id",
                choiceId,
                "Provide a stable shop choice id from state.choices[].id or availableActions[].arguments.choiceId.");
        }

        var requestedPlayerId = request.Perspective?.PlayerId;
        if (!string.IsNullOrWhiteSpace(requestedPlayerId)
            && !string.IsNullOrWhiteSpace(shopChoice.PlayerId)
            && !string.Equals(requestedPlayerId, shopChoice.PlayerId, StringComparison.Ordinal))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose is only available for the requested shop owner on the active shop screen.",
                "player_id",
                requestedPlayerId,
                $"choiceOwnerPlayerId={shopChoice.PlayerId}.");
        }

        if (!shopChoice.IsExecutable)
        {
            var note = !shopChoice.IsStocked
                ? "The requested shop slot is no longer stocked."
                : !shopChoice.EnoughGold
                    ? "The local player does not currently have enough gold for the requested shop slot."
                    : "Retry with a currently executable shop choice from availableActions[].arguments.choiceId.";
            return ChoiceActionUnavailable(
                request.Kind,
                "choose requires a currently executable shop choice.",
                "choice_id",
                choiceId,
                note);
        }

        if (!TryInvokeShopChoice(shopContext.ScreenObject, shopContext.Inventory, shopChoice, out var accepted))
        {
            _logStream.Write(
                BridgeLogLevel.Error,
                "bridge.action",
                $"No callable shop hook was found for choice '{choiceId}'.");
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not execute the requested shop choice through the active live hooks.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "choice_id",
                        Value: choiceId,
                        Note: shopChoice.IsFlowChoice
                            ? "The active shop screen did not expose a recognized shop-flow callback."
                            : "The active shop screen did not expose a recognized merchant purchase callback."),
                ]);
        }

        if (!accepted)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.RuntimeFailure,
                message: "The runtime rejected the requested shop choice after preflight validation succeeded.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "choice_id",
                        Value: choiceId,
                        Note: "The shop state likely changed before the merchant purchase flow completed."),
                ]);
        }

        _logStream.Write(
            BridgeLogLevel.Info,
            "bridge.action",
            shopChoice.IsFlowChoice
                ? $"Executed shop flow choice '{shopChoice.Snapshot.Label}'."
                : $"Accepted shop choice '{shopChoice.Snapshot.Label}'.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:choose:{request.RequestId}",
            kind: request.Kind,
            message: $"Executed choice '{choiceId}'.");
    }

    private ActionExecutionResult ExecuteBuyCard(SemanticActionRequest request)
        => ExecuteShopIntentChoice(
            request,
            ResolveRequestValue(request, "shopItemId") ?? request.CardId,
            SemanticActionKind.BuyCard,
            expectedSlotKind: "card",
            expectedFlowChoiceId: null,
            fieldName: "shop_item_id");

    private ActionExecutionResult ExecuteBuyRelic(SemanticActionRequest request)
        => ExecuteShopIntentChoice(
            request,
            ResolveRequestValue(request, "shopItemId") ?? ResolveRequestValue(request, "relicId"),
            SemanticActionKind.BuyRelic,
            expectedSlotKind: "relic",
            expectedFlowChoiceId: null,
            fieldName: "shop_item_id");

    private ActionExecutionResult ExecuteBuyPotion(SemanticActionRequest request)
        => ExecuteShopIntentChoice(
            request,
            ResolveRequestValue(request, "shopItemId") ?? request.PotionId,
            SemanticActionKind.BuyPotion,
            expectedSlotKind: "potion",
            expectedFlowChoiceId: null,
            fieldName: "shop_item_id");

    private ActionExecutionResult ExecuteRemoveCard(SemanticActionRequest request)
        => ExecuteShopIntentChoice(
            request,
            ResolveRequestValue(request, "shopItemId"),
            SemanticActionKind.RemoveCard,
            expectedSlotKind: "card-removal",
            expectedFlowChoiceId: null,
            fieldName: "shop_item_id");

    private ActionExecutionResult ExecuteLeaveShop(SemanticActionRequest request)
        => ExecuteShopIntentChoice(
            request,
            Sts2ShopIds.LeaveChoiceId(),
            SemanticActionKind.LeaveShop,
            expectedSlotKind: "leave",
            expectedFlowChoiceId: Sts2ShopIds.LeaveChoiceId(),
            fieldName: "shop_flow_id");

    private ActionExecutionResult ExecuteCloseShopInventory(SemanticActionRequest request)
        => ExecuteShopIntentChoice(
            request,
            Sts2ShopIds.CloseInventoryChoiceId(),
            SemanticActionKind.CloseShopInventory,
            expectedSlotKind: "close-inventory",
            expectedFlowChoiceId: Sts2ShopIds.CloseInventoryChoiceId(),
            fieldName: "shop_flow_id");

    // Open the merchant inventory — the real, human-equivalent of clicking the in-scene merchant. The
    // browser hides that MerchantButton and no other control calls OpenInventory, so without this the
    // shop never opens and its buy/remove/leave controls (which require the live shop screen) never
    // surface. Unlike buy/leave/close (which go through ExecuteShopIntentChoice and need an ALREADY-open
    // shop screen), open-shop runs BEFORE the shop screen exists, so it talks to NMerchantRoom directly —
    // exactly the path OnMerchantOpened takes when a player walks up to the merchant. (Non-debug twin of
    // ExecuteDebugOpenShop.)
    private ActionExecutionResult ExecuteOpenShop(SemanticActionRequest request)
    {
        if (RunManager.Instance?.IsInProgress != true)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "open-shop requires a run in progress.");
        }

        var room = NMerchantRoom.Instance;
        if (room is null)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.WrongScreen,
                message: "open-shop is only available in a merchant room.",
                details: [new ActionFailureDetail("screen", _screenLocator.Locate().ScreenType, "Retry when the current room is a merchant room.")]);
        }

        room.OpenInventory();
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:open-shop:{request.RequestId}",
            kind: request.Kind,
            message: "Opened the merchant inventory.");
    }

    // Leave the merchant ROOM — distinct from closing the buy inventory. The in-room Proceed button
    // releases to NMerchantRoom.HideScreen -> NMapScreen.Open(), which surfaces the travel map; leave-shop
    // only closes the inventory panel, so without this the merchant room never opens the map and travel
    // stays disabled (the browser keeps re-opening the shop). Open the travel map directly — that is the
    // outcome of HideScreen, and it avoids the one-time merchant FTUE modal HideScreen would otherwise
    // show (which has no browser dismissal path). Human-equivalent of clicking the room's Proceed button.
    private ActionExecutionResult ExecuteProceedMerchantRoom(SemanticActionRequest request)
    {
        if (RunManager.Instance?.IsInProgress != true)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "proceed-merchant-room requires a run in progress.");
        }

        var room = NMerchantRoom.Instance;
        if (room is null)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.WrongScreen,
                message: "proceed-merchant-room is only available in a merchant room.",
                details: [new ActionFailureDetail("screen", _screenLocator.Locate().ScreenType, "Retry when the current room is a merchant room.")]);
        }

        var map = NMapScreen.Instance;
        if (map is null)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "proceed-merchant-room could not resolve the travel map to open.");
        }

        // Mirror RunManager.ProceedFromTerminalRewardsScreen (the post-combat path) so the merchant's
        // overlay travel map is actually travelable. A bare Open() early-returns when the map is already
        // open (e.g. a prior toggle-map peek), so its RecalculateTravelability never runs and no node is
        // State=Travelable; Open() also never (re)asserts travel, leaving IsTravelEnabled stale. Force a
        // recalc and re-enable travel idempotently: with this the next-row nodes become travelable and
        // run.map.view.isAcceptingVotes flips true, so the browser map controls become clickable.
        if (!map.IsOpen)
        {
            map.Open();
        }
        else
        {
            Sts2LiveIntrospection.TryInvokeParameterlessMethod(map, "RecalculateTravelability");
        }

        map.SetTravelEnabled(enabled: true);
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:proceed-merchant-room:{request.RequestId}",
            kind: request.Kind,
            message: "Left the merchant room (opened the travel map).");
    }

    private ActionExecutionResult ExecuteShopIntentChoice(
        SemanticActionRequest request,
        string? choiceId,
        SemanticActionKind actionKind,
        string expectedSlotKind,
        string? expectedFlowChoiceId,
        string fieldName)
    {
        var actionName = ResolveActionName(actionKind);
        if (string.IsNullOrWhiteSpace(choiceId))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: $"{actionName} requires a stable visible shop id.",
                details: [new ActionFailureDetail(fieldName, string.Empty, "Provide a shop id from state.shop.purchasableItems[].id or availableActions[].arguments.values.shopItemId.")]);
        }

        if (!TryResolveShopContext(out var shopContext))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.WrongScreen,
                message: $"{actionName} is only available on the live shop screen.",
                details: [new ActionFailureDetail("screen", _screenLocator.Locate().ScreenType, "Retry when state.screen.id is shop.")]);
        }

        if (!shopContext.ChoicesById.TryGetValue(choiceId, out var shopChoice))
        {
            // A well-formed id of the right kind that simply isn't in the dictionary is "not visible"
            // (e.g. the shop is closed), not "stale". Recognize BOTH id worlds: the 5-part screen-slot
            // ChoiceId AND the canonical inventory-model id (shop:{collection}:{index}) the browser sends.
            var canonicalKindMatches = Sts2ShopIds.TryParseInventoryEntryId(choiceId, out var canonicalCollection, out _)
                && string.Equals(Sts2ShopIds.KindForCollection(canonicalCollection), expectedSlotKind, StringComparison.Ordinal);
            var looksLikeSameActionType = expectedFlowChoiceId is not null
                ? string.Equals(choiceId, expectedFlowChoiceId, StringComparison.Ordinal)
                : expectedSlotKind == "card-removal"
                    ? Sts2ShopIds.TryParseCardRemovalChoiceId(choiceId, out _, out _) || canonicalKindMatches
                    : (Sts2ShopIds.TryParseShopItemChoiceId(choiceId, out _, out var parsedKind, out _, out _)
                        && string.Equals(parsedKind, expectedSlotKind, StringComparison.Ordinal))
                        || canonicalKindMatches;
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: looksLikeSameActionType ? ActionFailureCode.NotVisible : ActionFailureCode.StaleId,
                message: "The requested shop control is not visible on the active shop screen.",
                details: [new ActionFailureDetail(fieldName, choiceId, "Use a currently visible id from state.shop or availableActions.")]);
        }

        var requestedPlayerId = request.Perspective?.PlayerId ?? ResolveRequestValue(request, "playerId");
        if (BuildOwnershipFailure(
                request,
                new ActionOwnershipContext(requestedPlayerId, shopChoice.PlayerId, shopContext.LocalPlayerId, ResolveRunHostPlayerId(shopContext.LocalPlayerId), [], shopContext.Screen.ScreenType, actionName),
                out var ownershipFailure))
        {
            return ownershipFailure;
        }

        if (!string.IsNullOrWhiteSpace(requestedPlayerId)
            && !string.IsNullOrWhiteSpace(shopChoice.PlayerId)
            && !string.Equals(requestedPlayerId, shopChoice.PlayerId, StringComparison.Ordinal))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.WrongPlayer,
                message: "The requested shop control belongs to a different player.",
                details: [new ActionFailureDetail("player_id", requestedPlayerId, $"choiceOwnerPlayerId={shopChoice.PlayerId}.")]);
        }

        if (!string.Equals(shopChoice.SlotKind, expectedSlotKind, StringComparison.Ordinal))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.StaleId,
                message: "The requested shop id does not match this action kind.",
                details: [new ActionFailureDetail(fieldName, choiceId, $"Use {actionName} with a matching visible shop id.")]);
        }

        if (!shopChoice.IsExecutable)
        {
            var note = !shopChoice.IsStocked
                ? "The requested shop slot is visible but out of stock."
                : !shopChoice.EnoughGold
                    ? "The local player does not currently have enough gold for the requested shop slot."
                    : "Retry when the shop control appears in availableActions.";
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.NotEnabled,
                message: "The requested shop control is visible but not enabled.",
                details: [new ActionFailureDetail(fieldName, choiceId, note)]);
        }

        if (!TryInvokeShopChoice(shopContext.ScreenObject, shopContext.Inventory, shopChoice, out var accepted))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not execute the requested shop control through the active live hooks.",
                details: [new ActionFailureDetail(fieldName, choiceId, shopChoice.IsFlowChoice ? "Missing shopScreen.Close/backButton.OnRelease hook." : "Missing merchantEntry.OnTryPurchaseWrapper hook.")]);
        }

        if (!accepted)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.RuntimeFailure,
                message: "The runtime rejected the requested shop control after preflight validation succeeded.",
                details: [new ActionFailureDetail(fieldName, choiceId, "The shop state likely changed before the merchant purchase flow completed.")]);
        }

        // A card buy added a card to the buyer's deck via CardPileCmd.Add but skipped the merchant slot's
        // card-fly VFX (which only runs when the shop SCREEN is up) — the same skip as the reward commit —
        // so the game's deck-count badge would stay stale. Re-raise the deck event. (relic/potion top-bar
        // widgets are bridge-driven and self-correct on rebroadcast.)
        if (string.Equals(expectedSlotKind, "card", StringComparison.Ordinal)
            && Sts2LiveIntrospection.GetMemberValue(shopContext.Inventory, "Player") is Player buyer)
        {
            RefreshDeckBadge(buyer, added: true);
        }

        return ActionExecutionResult.Success(
            actionInstanceId: $"action:{actionName}:{request.RequestId}",
            kind: request.Kind,
            message: $"Executed {actionName} '{choiceId}'.");
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

    // Draws (or erases) one annotation stroke on the LOCAL player's NMapDrawings. The points arrive in
    // TheMap content space; Sts2MapDrawingTransform.ContentToLocal converts each to the NMapDrawings-local
    // BeginLineLocal input. BeginLineLocal/Update/Stop auto-broadcast a MapDrawingMessage to peers, so this
    // is multiplayer-safe with no new netcode. Only the local player can write their own drawing surface.
    private ActionExecutionResult ExecuteDrawMapStroke(SemanticActionRequest request)
    {
        var points = request.StrokePoints;
        if (points is null || points.Count < 2)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "draw-map-stroke requires at least two stroke points in TheMap content space.",
                details: [new ActionFailureDetail("points", string.Empty, "Provide a polyline of >=2 content-space points.")]);
        }

        if (!TryResolveMapContext(out var mapContext))
        {
            return MapActionUnavailable(
                request.Kind,
                "draw-map-stroke is only available on the live map screen.",
                "points",
                string.Empty,
                "Retry when state.screen.id is map.");
        }

        var requestedPlayerId = request.Perspective?.PlayerId;
        if (!string.IsNullOrWhiteSpace(requestedPlayerId)
            && !string.Equals(requestedPlayerId, mapContext.LocalPlayerId, StringComparison.Ordinal))
        {
            return MapActionUnavailable(
                request.Kind,
                "draw-map-stroke writes the local player's map drawings only.",
                "player_id",
                requestedPlayerId,
                $"localPlayerId={mapContext.LocalPlayerId ?? "unknown"}.");
        }

        var drawings = mapContext.MapScreen.Drawings;
        if (drawings is null)
        {
            return MapActionUnavailable(
                request.Kind,
                "the live map screen did not expose its drawing surface.",
                "points",
                string.Empty,
                "Retry when the map screen is fully open.");
        }

        var isEraser = request.IsEraser == true;
        drawings.SetDrawingModeLocal(isEraser ? DrawingMode.Erasing : DrawingMode.Drawing);
        var (x0, y0) = Spirectl.Sts2.Core.Map.Sts2MapDrawingTransform.ContentToLocal(points[0].X, points[0].Y);
        drawings.BeginLineLocal(new Vector2((float)x0, (float)y0), null);
        for (var i = 1; i < points.Count; i++)
        {
            var (xi, yi) = Spirectl.Sts2.Core.Map.Sts2MapDrawingTransform.ContentToLocal(points[i].X, points[i].Y);
            drawings.UpdateCurrentLinePositionLocal(new Vector2((float)xi, (float)yi));
        }

        drawings.StopLineLocal();
        _logStream.Write(BridgeLogLevel.Info, "bridge.action", $"Drew a {(isEraser ? "eraser" : "pen")} map stroke ({points.Count} points).");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:draw-map-stroke:{request.RequestId}",
            kind: request.Kind,
            message: $"Drew a {(isEraser ? "eraser" : "pen")} map stroke with {points.Count} points.");
    }

    // Clears the LOCAL player's map drawings (ClearDrawnLinesLocal broadcasts to peers too).
    private ActionExecutionResult ExecuteClearMapDrawings(SemanticActionRequest request)
    {
        if (!TryResolveMapContext(out var mapContext))
        {
            return MapActionUnavailable(
                request.Kind,
                "clear-map-drawings is only available on the live map screen.",
                "player_id",
                request.Perspective?.PlayerId ?? string.Empty,
                "Retry when state.screen.id is map.");
        }

        var requestedPlayerId = request.Perspective?.PlayerId;
        if (!string.IsNullOrWhiteSpace(requestedPlayerId)
            && !string.Equals(requestedPlayerId, mapContext.LocalPlayerId, StringComparison.Ordinal))
        {
            return MapActionUnavailable(
                request.Kind,
                "clear-map-drawings clears the local player's map drawings only.",
                "player_id",
                requestedPlayerId,
                $"localPlayerId={mapContext.LocalPlayerId ?? "unknown"}.");
        }

        var drawings = mapContext.MapScreen.Drawings;
        if (drawings is null)
        {
            return MapActionUnavailable(
                request.Kind,
                "the live map screen did not expose its drawing surface.",
                "player_id",
                requestedPlayerId ?? string.Empty,
                "Retry when the map screen is fully open.");
        }

        drawings.ClearDrawnLinesLocal();
        _logStream.Write(BridgeLogLevel.Info, "bridge.action", "Cleared the local player's map drawings.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:clear-map-drawings:{request.RequestId}",
            kind: request.Kind,
            message: "Cleared the local player's map drawings.");
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

    private ActionExecutionResult ExecuteBackFromMap(SemanticActionRequest request)
    {
        if (!TryResolveMapContext(out var mapContext))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.WrongScreen,
                message: "back-from-map is only available on the live map screen.",
                details: [new ActionFailureDetail("screen", _screenLocator.Locate().ScreenType, "Retry when state.screen.id is map.")]);
        }

        var requestedPlayerId = request.Perspective?.PlayerId ?? ResolveRequestValue(request, "playerId");
        var mapChoice = mapContext.ChoicesById.Values.FirstOrDefault(choice => string.Equals(choice.PreferredAction, "back-from-map", StringComparison.Ordinal));
        if (mapChoice is null)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.NotVisible,
                message: "back-from-map requires the map back control to be currently visible.",
                details: [new ActionFailureDetail("choice_id", Sts2MapIds.BackChoiceId(), "Use back-from-map only when the map back action appears in availableActions.")]);
        }

        if (BuildOwnershipFailure(
                request,
                new ActionOwnershipContext(requestedPlayerId, mapChoice.PlayerId, mapContext.LocalPlayerId, ResolveRunHostPlayerId(mapContext.LocalPlayerId), [], mapContext.Screen.ScreenType, "back-from-map"),
                out var ownershipFailure))
        {
            return ownershipFailure;
        }

        if (!string.IsNullOrWhiteSpace(requestedPlayerId)
            && !string.IsNullOrWhiteSpace(mapChoice.PlayerId)
            && !string.Equals(requestedPlayerId, mapChoice.PlayerId, StringComparison.Ordinal))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.WrongPlayer,
                message: "back-from-map is only available for the requested map choice owner.",
                details: [new ActionFailureDetail("player_id", requestedPlayerId, $"choiceOwnerPlayerId={mapChoice.PlayerId}.")]);
        }

        if (!mapChoice.IsExecutable)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.NotEnabled,
                message: "The map back control is visible but not enabled.",
                details: [new ActionFailureDetail("choice_id", mapChoice.Snapshot.Id, "Retry when the control appears in availableActions.")]);
        }

        if (!TryExecuteMapChoice(mapChoice))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not execute map back through the active live hooks.",
                details: [new ActionFailureDetail("choice_id", mapChoice.Snapshot.Id, "The active map screen did not expose a recognized map back callback.")]);
        }

        return ActionExecutionResult.Success(
            actionInstanceId: $"action:back-from-map:{request.RequestId}",
            kind: request.Kind,
            message: "Went back from the map.");
    }

    private ActionExecutionResult ExecuteMapChoice(SemanticActionRequest request, string choiceId)
    {
        if (!TryResolveMapContext(out var mapContext))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose is only available on the live map screen for visible map flow choices.",
                "choice_id",
                choiceId,
                "Retry when state.screen.id is map and availableActions includes choose.");
        }

        if (!mapContext!.ChoicesById.TryGetValue(choiceId, out var mapChoice))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose requires a visible map choice id from the current screen.",
                "choice_id",
                choiceId,
                "Provide a stable map flow choice id from state.choices[].id or availableActions[].arguments.choiceId.");
        }

        var requestedPlayerId = request.Perspective?.PlayerId;
        if (!string.IsNullOrWhiteSpace(requestedPlayerId)
            && !string.IsNullOrWhiteSpace(mapChoice.PlayerId)
            && !string.Equals(requestedPlayerId, mapChoice.PlayerId, StringComparison.Ordinal))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose is only available for the requested map choice owner on the active map screen.",
                "player_id",
                requestedPlayerId,
                $"choiceOwnerPlayerId={mapChoice.PlayerId}.");
        }

        if (!mapChoice.IsExecutable)
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose requires a currently executable map choice.",
                "choice_id",
                choiceId,
                "Retry with a currently executable map choice from availableActions[].arguments.choiceId.");
        }

        if (!TryExecuteMapChoice(mapChoice))
        {
            _logStream.Write(
                BridgeLogLevel.Error,
                "bridge.action",
                $"No callable map hook was found for choice '{choiceId}'.");
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not execute the requested map choice through the active live hooks.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "choice_id",
                        Value: choiceId,
                        Note: "The active map screen did not expose a recognized map-flow callback."),
                ]);
        }

        _logStream.Write(BridgeLogLevel.Info, "bridge.action", $"Executed map choice '{mapChoice.Snapshot.Label}'.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:choose:{request.RequestId}",
            kind: request.Kind,
            message: $"Executed choice '{choiceId}'.");
    }

    private ActionExecutionResult ExecuteEndTurn(SemanticActionRequest request)
    {
        var requestedPlayerId = request.Perspective?.PlayerId;
        if (!TryResolveCombatContext(requestedPlayerId, out var combatContext))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "end-turn is only available for the active combat player during the play phase.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "player_id",
                        Value: request.Perspective?.PlayerId ?? string.Empty,
                        Note: "Retry when state.screen.id is combat and availableActions includes end-turn."),
                ]);
        }

        if (BuildOwnershipFailure(
                request,
                new ActionOwnershipContext(
                    requestedPlayerId,
                    combatContext.ActionOwnerPlayerId,
                    combatContext.LocalPlayerId,
                    combatContext.HostPlayerId,
                    combatContext.HostLocalPlayerIds,
                    combatContext.Screen.ScreenType,
                    "end-turn"),
                out var ownershipFailure))
        {
            return ownershipFailure;
        }

        var isPlayerTurn = CombatManager.Instance is not null
            && CombatManager.Instance.IsInProgress
            && Sts2CombatFacts.IsPlayPhase(combatContext.ActionOwnerPlayer);

        if (!Sts2ActionCatalog.CanEndTurn(
            combatContext.Screen.ScreenType,
            combatContext.ActionOwnerPlayerId,
            isPlayerTurn,
            requestedPlayerId,
            combatContext.IsActionOwnerHostLocalSeat))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "end-turn is only available for the active combat player during the play phase.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "player_id",
                        Value: requestedPlayerId ?? string.Empty,
                        Note: $"screen={combatContext.Screen.ScreenType}, activePlayerId={combatContext.ActionOwnerPlayerId ?? "unknown"}, isPlayerTurn={isPlayerTurn}."),
                ]);
        }

        if (!TryInvokeEndTurn(combatContext.ActionOwnerPlayer))
        {
            _logStream.Write(
                BridgeLogLevel.Error,
                "bridge.action",
                $"No callable end-turn hook was found for player '{combatContext.ActionOwnerPlayerId ?? "unknown"}'.");
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not execute end-turn through the active combat hooks.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "player_id",
                        Value: combatContext.ActionOwnerPlayerId ?? string.Empty,
                        Note: "The active combat controller did not expose any recognized end-turn callback."),
                ]);
        }

        _logStream.Write(
            BridgeLogLevel.Info,
            "bridge.action",
            $"Executed end-turn for '{combatContext.ActionOwnerPlayerId ?? "local"}'.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:end-turn:{request.RequestId}",
            kind: request.Kind,
            message: $"Ended turn for '{combatContext.ActionOwnerPlayerId ?? "local"}'.");
    }

    private ActionExecutionResult ExecuteCancelEndTurn(SemanticActionRequest request)
    {
        var requestedPlayerId = request.Perspective?.PlayerId;
        if (!TryResolveCombatContext(requestedPlayerId, out var combatContext))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "cancel-end-turn is only available for a combat player that has ended their turn.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "player_id",
                        Value: request.Perspective?.PlayerId ?? string.Empty,
                        Note: "Retry when state.screen.id is combat and the player has ended their turn (combat.hasEndedTurn)."),
                ]);
        }

        if (BuildOwnershipFailure(
                request,
                new ActionOwnershipContext(
                    requestedPlayerId,
                    combatContext.ActionOwnerPlayerId,
                    combatContext.LocalPlayerId,
                    combatContext.HostPlayerId,
                    combatContext.HostLocalPlayerIds,
                    combatContext.Screen.ScreenType,
                    "cancel-end-turn"),
                out var ownershipFailure))
        {
            return ownershipFailure;
        }

        var playerHasEndedTurn = combatContext.ActionOwnerPlayer is not null
            && CombatManager.Instance is not null
            && CombatManager.Instance.IsPlayerReadyToEndTurn(combatContext.ActionOwnerPlayer);

        if (!Sts2ActionCatalog.CanCancelEndTurn(
            combatContext.Screen.ScreenType,
            combatContext.ActionOwnerPlayerId,
            playerHasEndedTurn,
            requestedPlayerId,
            combatContext.IsActionOwnerHostLocalSeat))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "cancel-end-turn is only available for a combat player that has ended their turn.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "player_id",
                        Value: requestedPlayerId ?? string.Empty,
                        Note: $"screen={combatContext.Screen.ScreenType}, activePlayerId={combatContext.ActionOwnerPlayerId ?? "unknown"}, hasEndedTurn={playerHasEndedTurn}."),
                ]);
        }

        if (!TryInvokeCancelEndTurn(combatContext.ActionOwnerPlayer))
        {
            _logStream.Write(
                BridgeLogLevel.Error,
                "bridge.action",
                $"No callable cancel-end-turn hook was found for player '{combatContext.ActionOwnerPlayerId ?? "unknown"}'.");
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not execute cancel-end-turn through the active combat hooks.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "player_id",
                        Value: combatContext.ActionOwnerPlayerId ?? string.Empty,
                        Note: "The active combat controller did not expose any recognized undo-end-turn callback."),
                ]);
        }

        _logStream.Write(
            BridgeLogLevel.Info,
            "bridge.action",
            $"Executed cancel-end-turn for '{combatContext.ActionOwnerPlayerId ?? "local"}'.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:cancel-end-turn:{request.RequestId}",
            kind: request.Kind,
            message: $"Cancelled pending end-turn for '{combatContext.ActionOwnerPlayerId ?? "local"}'.");
    }

    private ActionExecutionResult ExecuteReady(SemanticActionRequest request)
    {
        if (!TryResolveLobbyContext(out var lobbyContext))
        {
            return LobbyActionUnavailable(
                request.Kind,
                "ready is only available when the local multiplayer lobby player can ready up.",
                "action",
                "ready",
                $"Retry when state.screen.id is {Sts2SupportedScreenIds.StartRunLobbyScreenId} or {Sts2SupportedScreenIds.LoadRunLobbyScreenId} and availableActions includes ready.");
        }

        var requestedPlayerId = request.Perspective?.PlayerId ?? ResolveRequestValue(request, "playerId");
        var ownership = ResolveLobbySeatOwnership(lobbyContext, requestedPlayerId);
        if (BuildOwnershipFailure(
                request,
                new ActionOwnershipContext(requestedPlayerId, ownership.ResolvedOwnerPlayerId, lobbyContext.Lobby.LocalPlayerId, lobbyContext.Lobby.HostPlayerId, ownership.HostLocalPlayerIds, lobbyContext.Screen.ScreenType, "ready"),
                out var ownershipFailure))
        {
            return ownershipFailure;
        }

        // A browser-owned synthetic host-local seat (e.g. p:2) readies ITSELF: mutate that seat in
        // StartRunLobby.Players, not the host's local player (which TrySetReady/SetReady would target).
        if (ownership.TargetsSyntheticSeat && lobbyContext.StartRunLobby is not null)
        {
            return SetSyntheticSeatReady(lobbyContext, ownership.SeatNetId, ready: true, request);
        }

        var localPlayer = lobbyContext.Lobby.PlayersById.GetValueOrDefault(lobbyContext.Lobby.LocalPlayerId ?? string.Empty);
        if (!Sts2ActionCatalog.CanReady(lobbyContext.Screen.ScreenType, localPlayer))
        {
            return LobbyActionUnavailable(
                request.Kind,
                "ready is only available when the local multiplayer lobby player can ready up.",
                "player_id",
                lobbyContext.Lobby.LocalPlayerId ?? string.Empty,
                "The local player must be present, unready, and have a selected character.");
        }

        var executed = lobbyContext.StartRunLobby is not null
            ? TrySetReady(lobbyContext.StartRunLobby, true)
            : lobbyContext.LoadRunLobby is not null && TrySetReady(lobbyContext.LoadRunLobby, true);
        if (!executed)
        {
            return LobbyRuntimeFailure(
                request.Kind,
                "Could not execute ready through the active multiplayer lobby hooks.",
                lobbyContext.Lobby.LocalPlayerId ?? string.Empty,
                "The active lobby did not expose a callable ready hook.");
        }

        _logStream.Write(BridgeLogLevel.Info, "bridge.action", "Marked the local lobby player ready.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:ready:{request.RequestId}",
            kind: request.Kind,
            message: "Marked the local lobby player ready.");
    }

    private ActionExecutionResult ExecuteUnready(SemanticActionRequest request)
    {
        if (!TryResolveLobbyContext(out var lobbyContext))
        {
            return LobbyActionUnavailable(
                request.Kind,
                "unready is only available when the local multiplayer lobby player is already ready.",
                "action",
                "unready",
                $"Retry when state.screen.id is {Sts2SupportedScreenIds.StartRunLobbyScreenId} or {Sts2SupportedScreenIds.LoadRunLobbyScreenId} and availableActions includes unready.");
        }

        var requestedPlayerId = request.Perspective?.PlayerId ?? ResolveRequestValue(request, "playerId");
        var ownership = ResolveLobbySeatOwnership(lobbyContext, requestedPlayerId);
        if (BuildOwnershipFailure(
                request,
                new ActionOwnershipContext(requestedPlayerId, ownership.ResolvedOwnerPlayerId, lobbyContext.Lobby.LocalPlayerId, lobbyContext.Lobby.HostPlayerId, ownership.HostLocalPlayerIds, lobbyContext.Screen.ScreenType, "unready"),
                out var ownershipFailure))
        {
            return ownershipFailure;
        }

        if (ownership.TargetsSyntheticSeat && lobbyContext.StartRunLobby is not null)
        {
            return SetSyntheticSeatReady(lobbyContext, ownership.SeatNetId, ready: false, request);
        }

        var localPlayer = lobbyContext.Lobby.PlayersById.GetValueOrDefault(lobbyContext.Lobby.LocalPlayerId ?? string.Empty);
        if (!Sts2ActionCatalog.CanUnready(lobbyContext.Screen.ScreenType, localPlayer))
        {
            return LobbyActionUnavailable(
                request.Kind,
                "unready is only available when the local multiplayer lobby player is already ready.",
                "player_id",
                lobbyContext.Lobby.LocalPlayerId ?? string.Empty,
                "The local player must already be ready.");
        }

        var executed = lobbyContext.StartRunLobby is not null
            ? TrySetReady(lobbyContext.StartRunLobby, false)
            : lobbyContext.LoadRunLobby is not null && TrySetReady(lobbyContext.LoadRunLobby, false);
        if (!executed)
        {
            return LobbyRuntimeFailure(
                request.Kind,
                "Could not execute unready through the active multiplayer lobby hooks.",
                lobbyContext.Lobby.LocalPlayerId ?? string.Empty,
                "The active lobby did not expose a callable unready hook.");
        }

        _logStream.Write(BridgeLogLevel.Info, "bridge.action", "Marked the local lobby player unready.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:unready:{request.RequestId}",
            kind: request.Kind,
            message: "Marked the local lobby player unready.");
    }

    private ActionExecutionResult ExecuteSelectCharacter(SemanticActionRequest request)
    {
        var characterId = request.CharacterId?.Trim();
        if (string.IsNullOrWhiteSpace(characterId))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "select-character requires an executable lobby character id from the current screen.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "character_id",
                        Value: string.Empty,
                        Note: "Provide a stable character id from state.lobby.availableCharacters[].id."),
                ]);
        }

        if (!TryResolveLobbyContext(out var lobbyContext) || lobbyContext.ScreenObject is null)
        {
            return LobbyActionUnavailable(
                request.Kind,
                "select-character is only available on the multiplayer lobby when the active screen exposes executable character buttons.",
                "character_id",
                characterId,
                $"Retry when state.screen.id is {Sts2SupportedScreenIds.StartRunLobbyScreenId} or {Sts2SupportedScreenIds.LoadRunLobbyScreenId} and availableActions includes select-character.");
        }

        var requestedPlayerId = request.Perspective?.PlayerId ?? ResolveRequestValue(request, "playerId");
        var ownership = ResolveLobbySeatOwnership(lobbyContext, requestedPlayerId);
        if (BuildOwnershipFailure(
                request,
                new ActionOwnershipContext(requestedPlayerId, ownership.ResolvedOwnerPlayerId, lobbyContext.Lobby.LocalPlayerId, lobbyContext.Lobby.HostPlayerId, ownership.HostLocalPlayerIds, lobbyContext.Screen.ScreenType, "select-character"),
                out var ownershipFailure))
        {
            return ownershipFailure;
        }

        var target = ResolveSelectableCharacterTarget(lobbyContext.ScreenObject, characterId);
        if (target is null)
        {
            return LobbyActionUnavailable(
                request.Kind,
                "select-character requires an executable lobby character id from the current screen.",
                "character_id",
                characterId,
                "The target character must be visible, unlocked, and different from the current local selection.");
        }

        // A browser-owned synthetic host-local seat selects its OWN character: set it directly on that
        // seat (the screen's SelectCharacter only drives the host's local selection).
        if (ownership.TargetsSyntheticSeat && lobbyContext.StartRunLobby is not null)
        {
            if (target.Character is null)
            {
                return LobbyActionUnavailable(
                    request.Kind,
                    "select-character for a remote lobby seat requires a concrete character (random is not supported yet).",
                    "character_id",
                    characterId,
                    "Pick a specific unlocked character for this seat.");
            }

            return SetSyntheticSeatCharacter(lobbyContext, ownership.SeatNetId, target.Character, request);
        }

        var localPlayer = lobbyContext.Lobby.PlayersById.GetValueOrDefault(lobbyContext.Lobby.LocalPlayerId ?? string.Empty);
        var targetCharacter = lobbyContext.Lobby.AvailableCharactersById.GetValueOrDefault(characterId);
        if (!Sts2ActionCatalog.CanSelectCharacter(lobbyContext.Screen.ScreenType, localPlayer, targetCharacter))
        {
            return LobbyActionUnavailable(
                request.Kind,
                "select-character requires an executable lobby character id from the current screen.",
                "character_id",
                characterId,
                "The target character must be visible, unlocked, and different from the current local selection.");
        }

        var executed = target.Character is not null
            && lobbyContext.StartRunLobby is not null
            && Sts2LiveIntrospection.TryInvokeMethod(lobbyContext.ScreenObject, "SelectCharacter", target.Button, target.Character);
        executed = executed
            || Sts2LobbyCharacterButtonInvoker.TryExecute(target.Button);
        if (!executed)
        {
            return LobbyRuntimeFailure(
                request.Kind,
                "Could not execute select-character through the active multiplayer lobby hooks.",
                characterId,
                "The active lobby screen did not expose a callable character button press or SelectCharacter(button, model) path.");
        }

        _logStream.Write(BridgeLogLevel.Info, "bridge.action", $"Selected lobby character '{characterId}'.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:select-character:{request.RequestId}",
            kind: request.Kind,
            message: $"Selected lobby character '{characterId}'.");
    }

    private ActionExecutionResult ExecuteJoinLobbyPlayer(SemanticActionRequest request)
    {
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? ResolveRequestValue(request, "displayName")
            : request.DisplayName.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "join-lobby-player requires a non-empty displayName.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "display_name",
                        Value: string.Empty,
                        Note: "Provide the exact browser display name to show in the native lobby list."),
                ]);
        }

        if (!TryResolveLobbyContext(out var lobbyContext) || lobbyContext?.StartRunLobby is null)
        {
            return LobbyActionUnavailable(
                request.Kind,
                "join-lobby-player is only available in the start-run multiplayer lobby.",
                "action",
                "join-lobby-player",
                $"Retry when state.screen.id is {Sts2SupportedScreenIds.StartRunLobbyScreenId} and lobby.lobbyId is start-run.");
        }

        var activeNetIds = lobbyContext.StartRunLobby.Players
            .Select(player => player.id)
            .ToHashSet();
        if (Sts2HostLocalSeatRegistry.FindSyntheticSeatByName(displayName, activeNetIds) is { } existingNetId)
        {
            var existingPlayer = lobbyContext.StartRunLobby.Players.FirstOrDefault(player => player.id == existingNetId);
            if (!existingPlayer.Equals(default(GameLobbyPlayer)))
            {
                RefreshSyntheticStartRunLobbyPlayerUi(lobbyContext.ScreenObject, existingPlayer);
            }

            return ActionExecutionResult.Success(
                actionInstanceId: $"action:join-lobby-player:{request.RequestId}",
                kind: request.Kind,
                message: $"Synthetic lobby player '{displayName}' already exists as p:{existingNetId}.");
        }

        var localPlayer = lobbyContext.StartRunLobby.LocalPlayer is GameLobbyPlayer resolvedLocal
            ? resolvedLocal
            : lobbyContext.StartRunLobby.Players.FirstOrDefault(player => player.id == lobbyContext.StartRunLobby.NetService.NetId);
        if (localPlayer.Equals(default(GameLobbyPlayer)))
        {
            return LobbyRuntimeFailure(
                request.Kind,
                "Could not create a synthetic lobby player because the local lobby player was unavailable.",
                displayName,
                "The active start-run lobby did not expose a local player to copy unlock state from.");
        }

        var character = ResolveDefaultLobbyCharacter(lobbyContext.ScreenObject, localPlayer);
        if (character is null)
        {
            return LobbyRuntimeFailure(
                request.Kind,
                "Could not create a synthetic lobby player because no playable lobby character was available.",
                displayName,
                "The active start-run lobby did not expose a selected local character, unlocked character button, or ModelDb character fallback.");
        }

        var netId = NextSyntheticNetId(lobbyContext.StartRunLobby.Players);
        var slotId = NextLobbySlotId(lobbyContext.StartRunLobby.Players);
        var syntheticPlayer = new GameLobbyPlayer
        {
            id = netId,
            slotId = slotId,
            character = character,
            unlockState = localPlayer.unlockState,
            maxMultiplayerAscensionUnlocked = localPlayer.maxMultiplayerAscensionUnlocked,
            isReady = false,
        };
        lobbyContext.StartRunLobby.Players.Add(syntheticPlayer);
        Sts2HostLocalSeatRegistry.RegisterSyntheticSeat(netId, displayName);
        RefreshSyntheticStartRunLobbyPlayerUi(lobbyContext.ScreenObject, syntheticPlayer);

        _logStream.Write(BridgeLogLevel.Info, "bridge.action", $"Created synthetic lobby player '{displayName}' as p:{netId}.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:join-lobby-player:{request.RequestId}",
            kind: request.Kind,
            message: $"Created synthetic lobby player '{displayName}' as p:{netId}.");
    }

    private ActionExecutionResult ExecuteLeaveLobbyPlayer(SemanticActionRequest request)
    {
        var requestedPlayerId = request.Perspective?.PlayerId ?? ResolveRequestValue(request, "playerId");
        if (!TryParseLobbyPlayerId(requestedPlayerId, out var netId))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "leave-lobby-player requires a synthetic lobby player id.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "player_id",
                        Value: requestedPlayerId ?? string.Empty,
                        Note: "Provide the player id returned by state.lobby.players[].id, such as p:2."),
                ]);
        }

        if (!TryResolveLobbyContext(out var lobbyContext) || lobbyContext?.StartRunLobby is null)
        {
            return LobbyActionUnavailable(
                request.Kind,
                "leave-lobby-player is only available in the start-run multiplayer lobby.",
                "player_id",
                requestedPlayerId ?? string.Empty,
                $"Retry when state.screen.id is {Sts2SupportedScreenIds.StartRunLobbyScreenId} and lobby.lobbyId is start-run.");
        }

        if (!Sts2HostLocalSeatRegistry.IsSyntheticHostLocalSeat(netId))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "leave-lobby-player can only remove synthetic host-local lobby seats.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "player_id",
                        Value: requestedPlayerId ?? string.Empty,
                        Note: "Native local or remote STS2 players are not removed by this action."),
                ]);
        }

        var index = lobbyContext.StartRunLobby.Players.FindIndex(player => player.id == netId);
        if (index < 0)
        {
            Sts2HostLocalSeatRegistry.UnregisterSyntheticSeat(netId);
            return ActionExecutionResult.Success(
                actionInstanceId: $"action:leave-lobby-player:{request.RequestId}",
                kind: request.Kind,
                message: $"Synthetic lobby player p:{netId} was already absent.");
        }

        var syntheticPlayer = lobbyContext.StartRunLobby.Players[index];
        lobbyContext.StartRunLobby.Players.RemoveAt(index);
        Sts2HostLocalSeatRegistry.UnregisterSyntheticSeat(netId);
        Sts2LiveIntrospection.TryInvokeMethod(lobbyContext.ScreenObject, "RemotePlayerDisconnected", syntheticPlayer);
        _logStream.Write(BridgeLogLevel.Info, "bridge.action", $"Removed synthetic lobby player p:{netId}.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:leave-lobby-player:{request.RequestId}",
            kind: request.Kind,
            message: $"Removed synthetic lobby player p:{netId}.");
    }

    private readonly record struct LobbySeatOwnership(
        string ResolvedOwnerPlayerId,
        IReadOnlyList<string> HostLocalPlayerIds,
        bool TargetsSyntheticSeat,
        ulong SeatNetId);

    // Resolve who owns a lobby ready/select action, mirroring the combat path
    // (TryResolveCombatContext): the requested player owns it when it is the local player OR a host-local
    // seat; otherwise the host's local player does. Browser players are synthetic host-local seats, so a
    // request for p:2 resolves to p:2 (passing the WrongPlayer ownership check) and is flagged for direct
    // per-seat mutation instead of the local-player hooks.
    private static LobbySeatOwnership ResolveLobbySeatOwnership(LobbyActionContext lobbyContext, string? requestedPlayerId)
    {
        var hostLocalPlayerIds = lobbyContext.Lobby.Players
            .Where(player => player.IsHostLocalSeat)
            .Select(player => player.Id)
            .ToArray();
        var localPlayerId = lobbyContext.Lobby.LocalPlayerId;
        var requestedIsLocal = !string.IsNullOrWhiteSpace(requestedPlayerId)
            && string.Equals(requestedPlayerId, localPlayerId, StringComparison.Ordinal);
        var requestedIsHostLocal = hostLocalPlayerIds.Contains(requestedPlayerId ?? string.Empty, StringComparer.Ordinal);
        var resolvedOwner = !string.IsNullOrWhiteSpace(requestedPlayerId)
            && lobbyContext.Lobby.PlayersById.ContainsKey(requestedPlayerId!)
            && (requestedIsLocal || requestedIsHostLocal)
                ? requestedPlayerId!
                : localPlayerId ?? string.Empty;
        TryParseLobbyPlayerId(resolvedOwner, out var seatNetId);
        var targetsSynthetic = !requestedIsLocal
            && seatNetId > 0
            && Sts2HostLocalSeatRegistry.IsSyntheticHostLocalSeat(seatNetId);
        return new LobbySeatOwnership(resolvedOwner, hostLocalPlayerIds, targetsSynthetic, seatNetId);
    }

    private ActionExecutionResult SetSyntheticSeatReady(LobbyActionContext lobbyContext, ulong netId, bool ready, SemanticActionRequest request)
    {
        var lobby = lobbyContext.StartRunLobby!;
        var index = lobby.Players.FindIndex(player => player.id == netId);
        if (index < 0)
        {
            return LobbyRuntimeFailure(
                request.Kind,
                $"Could not {(ready ? "ready" : "unready")} synthetic lobby seat p:{netId}; the seat was not found.",
                $"p:{netId}",
                "The seat may have been removed from the active lobby.");
        }

        var seat = lobby.Players[index];
        if (ready && seat.character is null)
        {
            return LobbyActionUnavailable(
                request.Kind,
                "ready requires the lobby seat to have a selected character.",
                "player_id",
                $"p:{netId}",
                "Select a character for this seat before readying.");
        }

        seat.isReady = ready;
        lobby.Players[index] = seat;
        RefreshSyntheticStartRunLobbyPlayerUi(lobbyContext.ScreenObject, seat);
        if (ready)
        {
            // A synthetic seat has no network peer, so mutating its ready flag never runs the lobby's
            // begin-check the way a real client's LobbyPlayerSetReadyMessage would. Invoke it directly
            // so the run actually begins once every seat (incl. the host seat) is ready.
            Sts2LiveIntrospection.TryInvokeMethod(lobby, "BeginRunIfAllPlayersReady");
        }
        _logStream.Write(BridgeLogLevel.Info, "bridge.action", $"Marked synthetic lobby seat p:{netId} {(ready ? "ready" : "unready")}.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:{(ready ? "ready" : "unready")}:{request.RequestId}",
            kind: request.Kind,
            message: $"Marked synthetic lobby seat p:{netId} {(ready ? "ready" : "unready")}.");
    }

    private ActionExecutionResult SetSyntheticSeatCharacter(LobbyActionContext lobbyContext, ulong netId, CharacterModel character, SemanticActionRequest request)
    {
        var lobby = lobbyContext.StartRunLobby!;
        var index = lobby.Players.FindIndex(player => player.id == netId);
        if (index < 0)
        {
            return LobbyRuntimeFailure(
                request.Kind,
                $"Could not select a character for synthetic lobby seat p:{netId}; the seat was not found.",
                $"p:{netId}",
                "The seat may have been removed from the active lobby.");
        }

        var seat = lobby.Players[index];
        // Changing character clears ready, mirroring the host's select-then-ready flow.
        seat.character = character;
        seat.isReady = false;
        lobby.Players[index] = seat;
        RefreshSyntheticStartRunLobbyPlayerUi(lobbyContext.ScreenObject, seat);
        _logStream.Write(BridgeLogLevel.Info, "bridge.action", $"Selected character '{character.Id.Entry}' for synthetic lobby seat p:{netId}.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:select-character:{request.RequestId}",
            kind: request.Kind,
            message: $"Selected character '{character.Id.Entry}' for synthetic lobby seat p:{netId}.");
    }

}
