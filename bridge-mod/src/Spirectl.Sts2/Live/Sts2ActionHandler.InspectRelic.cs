using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Relics;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;

public sealed partial class Sts2ActionHandler
{
    // Opens the relic-details overlay (NInspectRelicScreen) for a relic-bar
    // relic, mirroring NRelicInventory.OnRelicClicked: the browse list is the
    // inventory holders' models in display order and the screen is fetched via
    // NGame.GetInspectRelicScreen(). Accepts either the stable state id
    // (relic:p:1:0:BURNING_BLOOD) or a bare relic model id.
    private ActionExecutionResult ExecuteInspectRelic(SemanticActionRequest request)
    {
        const string actionName = "inspect-relic";
        var screen = _screenLocator.Locate();
        var localPlayerId = ResolveLocalPlayerId();
        var requestedPlayerId = request.Perspective?.PlayerId ?? ResolveRequestValue(request, "playerId");
        if (BuildOwnershipFailure(
                request,
                new ActionOwnershipContext(
                    requestedPlayerId,
                    localPlayerId,
                    localPlayerId,
                    ResolveRunHostPlayerId(localPlayerId),
                    [],
                    screen.ScreenType,
                    actionName),
                out var ownershipFailure))
        {
            return ownershipFailure;
        }

        var requestedRelicId = ResolveRequestValue(request, "relicId");
        if (string.IsNullOrWhiteSpace(requestedRelicId))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "inspect-relic requires a relicId argument.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "relicId",
                        Value: requestedRelicId ?? string.Empty,
                        Note: "Pass a stable relic id from state.run.players[].relics[].id (or a relic model id).",
                        ReasonCode: ActionFailureCode.InvalidAction,
                        Screen: screen.ScreenType,
                        PlayerId: localPlayerId),
                ],
                screen: screen.ScreenType,
                playerId: localPlayerId);
        }

        var checkedHookPaths = new[]
        {
            "NRun.Instance.GlobalUi.RelicInventory.RelicNodes",
            "NGame.Instance.GetInspectRelicScreen().Open",
        };
        var inventory = Sts2LiveIntrospection.GetMemberValue(
            Sts2LiveIntrospection.GetMemberValue(NRun.Instance, "GlobalUi"),
            "RelicInventory") as NRelicInventory;
        if (inventory is null || NGame.Instance is null)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.WrongScreen,
                message: $"{actionName} is only available while the run relic bar is present.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "screen",
                        Value: screen.ScreenType,
                        Note: "Retry while the game is in a run and the relic inventory is mounted.",
                        ReasonCode: ActionFailureCode.WrongScreen,
                        Screen: screen.ScreenType,
                        PlayerId: localPlayerId,
                        CheckedHookPaths: checkedHookPaths),
                ],
                screen: screen.ScreenType,
                playerId: localPlayerId,
                checkedHookPaths: checkedHookPaths);
        }

        // The state's stable relic id embeds the model id as its last segment.
        var separator = requestedRelicId.LastIndexOf(':');
        var requestedModelId = separator >= 0 ? requestedRelicId[(separator + 1)..] : requestedRelicId;

        var relics = new List<RelicModel>();
        RelicModel? target = null;
        foreach (var holder in inventory.RelicNodes)
        {
            var model = holder?.Relic?.Model;
            if (model is null)
            {
                continue;
            }

            relics.Add(model);
            if (target is null
                && string.Equals(model.Id.Entry, requestedModelId, StringComparison.OrdinalIgnoreCase))
            {
                target = model;
            }
        }

        if (target is null)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.StaleId,
                message: $"No relic-bar relic matched '{requestedRelicId}'.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "relicId",
                        Value: requestedRelicId,
                        Note: "Use a current stable relic id from state.run.players[].relics[].id.",
                        ReasonCode: ActionFailureCode.StaleId,
                        Screen: screen.ScreenType,
                        PlayerId: localPlayerId,
                        CheckedHookPaths: checkedHookPaths),
                ],
                screen: screen.ScreenType,
                playerId: localPlayerId,
                checkedHookPaths: checkedHookPaths);
        }

        // Navigating while the overlay is already open (the overlay arrows reuse
        // inspect-relic on the neighboring relic): jump via SetRelic like the
        // game's arrow handlers instead of replaying Open's enter tweens. The
        // index is resolved against the SCREEN's own browse list, which may
        // predate the current relic bar; fall back to a fresh Open when the
        // target is not in it.
        var openScreen = NGame.Instance.InspectRelicScreen;
        if (openScreen is { Visible: true }
            && Sts2LiveIntrospection.GetMemberValue(openScreen, "_relics") is System.Collections.IEnumerable screenRelics)
        {
            var screenIndex = -1;
            var index = 0;
            foreach (var entry in screenRelics)
            {
                if (ReferenceEquals(entry, target))
                {
                    screenIndex = index;
                    break;
                }

                index += 1;
            }

            if (screenIndex >= 0 && Sts2LiveIntrospection.TryInvokeMethod(openScreen, "SetRelic", screenIndex))
            {
                _logStream.Write(BridgeLogLevel.Info, "bridge.action", $"Executed {actionName} via SetRelic({screenIndex}) on the open overlay.");
                return ActionExecutionResult.Success(
                    actionInstanceId: $"action:{actionName}:{request.RequestId}",
                    kind: request.Kind,
                    message: $"Showed '{target.Id.Entry}' in the open relic-details overlay.");
            }
        }

        NGame.Instance.GetInspectRelicScreen().Open(relics, target);
        _logStream.Write(BridgeLogLevel.Info, "bridge.action", $"Executed {actionName} for relic model '{target.Id.Entry}'.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:{actionName}:{request.RequestId}",
            kind: request.Kind,
            message: $"Opened the relic-details overlay for '{target.Id.Entry}'.");
    }

    // Closes the relic-details overlay, mirroring its backstop press
    // (NInspectRelicScreen.OnBackstopPressed -> Close).
    private ActionExecutionResult ExecuteCloseInspectRelic(SemanticActionRequest request)
    {
        const string actionName = "close-inspect-relic";
        var screen = _screenLocator.Locate();
        var localPlayerId = ResolveLocalPlayerId();
        var requestedPlayerId = request.Perspective?.PlayerId ?? ResolveRequestValue(request, "playerId");
        if (BuildOwnershipFailure(
                request,
                new ActionOwnershipContext(
                    requestedPlayerId,
                    localPlayerId,
                    localPlayerId,
                    ResolveRunHostPlayerId(localPlayerId),
                    [],
                    screen.ScreenType,
                    actionName),
                out var ownershipFailure))
        {
            return ownershipFailure;
        }

        // The property (unlike GetInspectRelicScreen()) does not lazily create
        // the screen node as a side effect.
        var inspectScreen = NGame.Instance?.InspectRelicScreen;
        if (inspectScreen is null || !inspectScreen.Visible)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.NotVisible,
                message: "The relic-details overlay is not open.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "inspect_relic_screen",
                        Value: "NGame.Instance.InspectRelicScreen",
                        Note: "Retry while state.run.view.inspectRelic is present.",
                        ReasonCode: ActionFailureCode.NotVisible,
                        Screen: screen.ScreenType,
                        PlayerId: localPlayerId),
                ],
                screen: screen.ScreenType,
                playerId: localPlayerId);
        }

        inspectScreen.Close();
        _logStream.Write(BridgeLogLevel.Info, "bridge.action", $"Executed {actionName}.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:{actionName}:{request.RequestId}",
            kind: request.Kind,
            message: "Closed the relic-details overlay.");
    }
}
