using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;

public sealed partial class Sts2ActionHandler
{
    private ActionExecutionResult ExecuteViewDrawPile(SemanticActionRequest request)
        => ExecuteViewCombatPile(request, "view-draw-pile", "DrawPile", "draw pile");

    private ActionExecutionResult ExecuteViewDiscardPile(SemanticActionRequest request)
        => ExecuteViewCombatPile(request, "view-discard-pile", "DiscardPile", "discard pile");

    private ActionExecutionResult ExecuteViewExhaustPile(SemanticActionRequest request)
        => ExecuteViewCombatPile(request, "view-exhaust-pile", "ExhaustPile", "exhaust pile");

    // Opens the in-game card pile viewer (NCardPileScreen) by releasing the combat
    // HUD pile button (NCombatCardPile), mirroring how the top-bar deck button is
    // toggled. The pile cards themselves are already exposed through
    // run.players[].combat.{draw,discard,exhaust}Pile; this only opens the screen.
    private ActionExecutionResult ExecuteViewCombatPile(
        SemanticActionRequest request,
        string actionName,
        string pileNodeName,
        string label)
    {
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

        var combatUi = Sts2LiveIntrospection.GetMemberValue(NCombatRoom.Instance, "Ui") as Node
            ?? NCombatRoom.Instance;
        if (combatUi is null)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.WrongScreen,
                message: $"{actionName} is only available during combat.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "screen",
                        Value: screen.ScreenType,
                        Note: "Retry while a combat room with the pile HUD is mounted.",
                        ReasonCode: ActionFailureCode.WrongScreen,
                        Screen: screen.ScreenType,
                        PlayerId: localPlayerId,
                        CheckedHookPaths: ["NCombatRoom.Instance.Ui"]),
                ],
                screen: screen.ScreenType,
                playerId: localPlayerId,
                checkedHookPaths: ["NCombatRoom.Instance.Ui"]);
        }

        var checkedHookPaths = new[]
        {
            $"NCombatRoom.Instance.Ui/**/{pileNodeName}",
            $"NCombatRoom.Instance.Ui/**/{pileNodeName}.OnRelease",
        };
        var control = ResolveDescendantControlByName(combatUi, pileNodeName);
        if (control is null)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: $"Could not locate the combat {label} button.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "combat_pile_control",
                        Value: pileNodeName,
                        Note: $"Expected an NCombatCardPile named {pileNodeName} under the combat UI.",
                        ReasonCode: ActionFailureCode.MissingHook,
                        Screen: screen.ScreenType,
                        PlayerId: localPlayerId,
                        CheckedHookPaths: checkedHookPaths),
                ],
                screen: screen.ScreenType,
                playerId: localPlayerId,
                checkedHookPaths: checkedHookPaths);
        }

        if (!control.Visible)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.NotVisible,
                message: $"The combat {label} button is not visible.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "combat_pile_control",
                        Value: pileNodeName,
                        Note: "Retry when the pile button is visible (the exhaust pile hides while empty).",
                        ReasonCode: ActionFailureCode.NotVisible,
                        Screen: screen.ScreenType,
                        PlayerId: localPlayerId),
                ],
                screen: screen.ScreenType,
                playerId: localPlayerId);
        }

        if (IsControlDisabled(control))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.NotEnabled,
                message: $"The combat {label} button is disabled.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "combat_pile_control",
                        Value: pileNodeName,
                        Note: "Retry when the pile button is enabled.",
                        ReasonCode: ActionFailureCode.NotEnabled,
                        Screen: screen.ScreenType,
                        PlayerId: localPlayerId),
                ],
                screen: screen.ScreenType,
                playerId: localPlayerId);
        }

        if (!Sts2LiveIntrospection.TryInvokeParameterlessMethod(control, "OnRelease"))
        {
            _logStream.Write(
                BridgeLogLevel.Error,
                "bridge.action",
                $"No callable combat pile OnRelease hook was found for action '{actionName}' on control type '{control.GetType().FullName}'.");
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: $"Could not open the {label} viewer through the combat pile hook.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "combat_pile_control",
                        Value: pileNodeName,
                        Note: "The located combat pile control did not expose a callable parameterless OnRelease hook.",
                        ReasonCode: ActionFailureCode.MissingHook,
                        Screen: screen.ScreenType,
                        PlayerId: localPlayerId,
                        CheckedHookPaths: checkedHookPaths),
                ],
                screen: screen.ScreenType,
                playerId: localPlayerId,
                checkedHookPaths: checkedHookPaths);
        }

        _logStream.Write(BridgeLogLevel.Info, "bridge.action", $"Executed {actionName} through the combat pile HUD.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:{actionName}:{request.RequestId}",
            kind: request.Kind,
            message: $"Opened the {label} viewer.");
    }
}
