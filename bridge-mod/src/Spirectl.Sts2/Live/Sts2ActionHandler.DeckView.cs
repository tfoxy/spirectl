using Godot;
using MegaCrit.Sts2.Core.Nodes;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;

public sealed partial class Sts2ActionHandler
{
    private ActionExecutionResult ExecuteSortDeckView(SemanticActionRequest request)
    {
        var by = ResolveRequestValue(request, "by");
        if (string.IsNullOrWhiteSpace(by))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "sort-deck-view requires a sort key.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "by",
                        Value: string.Empty,
                        Note: "Use one of: obtained, type, cost, alphabet.",
                        ReasonCode: ActionFailureCode.InvalidAction),
                ]);
        }

        return ExecuteDeckViewButtonAction(
            request,
            actionName: "sort-deck-view",
            targetName: $"deck view {by} sorter",
            memberName: by.Trim().ToLowerInvariant() switch
            {
                "obtained" => "_obtainedSorter",
                "type" => "_typeSorter",
                "cost" => "_costSorter",
                "alphabet" => "_alphabetSorter",
                _ => null,
            },
            fallbackNodeName: by.Trim().ToLowerInvariant() switch
            {
                "obtained" => "ObtainedSorter",
                "type" => "CardTypeSorter",
                "cost" => "CostSorter",
                "alphabet" => "AlphabeticalSorter",
                _ => null,
            },
            successMessage: $"Sorted deck view by {by.Trim().ToLowerInvariant()}.");
    }

    private ActionExecutionResult ExecuteToggleDeckViewUpgrades(SemanticActionRequest request)
        => ExecuteDeckViewButtonAction(
            request,
            actionName: "toggle-deck-view-upgrades",
            targetName: "deck view upgrades checkbox",
            memberName: "_showUpgrades",
            fallbackNodeName: "Upgrades",
            successMessage: "Toggled deck view upgrade previews.");

    private ActionExecutionResult ExecuteDeckViewButtonAction(
        SemanticActionRequest request,
        string actionName,
        string targetName,
        string? memberName,
        string? fallbackNodeName,
        string successMessage)
    {
        if (memberName is null || fallbackNodeName is null)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: $"{actionName} received an unsupported control key.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "by",
                        Value: ResolveRequestValue(request, "by") ?? string.Empty,
                        Note: "Use one of: obtained, type, cost, alphabet.",
                        ReasonCode: ActionFailureCode.InvalidAction),
                ]);
        }

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

        var capstoneContainer = Sts2LiveIntrospection.GetMemberValue(
            Sts2LiveIntrospection.GetMemberValue(NRun.Instance, "GlobalUi"),
            "CapstoneContainer");
        var capstone = Sts2LiveIntrospection.GetMemberValue(capstoneContainer, "CurrentCapstoneScreen");
        if (capstone is not Node deckViewScreen
            || !Sts2LiveIntrospection.IsType(capstone, "MegaCrit.Sts2.Core.Nodes.Screens.NDeckViewScreen"))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.WrongScreen,
                message: $"{actionName} is only available on the deck view screen.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "screen",
                        Value: screen.ScreenType,
                        Note: "Open the run deck view before retrying.",
                        ReasonCode: ActionFailureCode.WrongScreen,
                        Screen: screen.ScreenType,
                        PlayerId: localPlayerId,
                        CheckedHookPaths: ["NRun.Instance.GlobalUi.CapstoneContainer.CurrentCapstoneScreen"]),
                ],
                screen: screen.ScreenType,
                playerId: localPlayerId,
                checkedHookPaths: ["NRun.Instance.GlobalUi.CapstoneContainer.CurrentCapstoneScreen"]);
        }

        var checkedHookPaths = new[]
        {
            $"NDeckViewScreen.{memberName}",
            $"DeckViewScreen/**/{fallbackNodeName}",
            $"NDeckViewScreen.{memberName}.OnRelease",
        };
        var control = Sts2LiveIntrospection.GetMemberValue(deckViewScreen, memberName) as Control
            ?? ResolveDescendantControlByName(deckViewScreen, fallbackNodeName);
        if (control is null)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: $"Could not locate the {targetName}.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "deck_view_control",
                        Value: fallbackNodeName,
                        Note: $"Expected NDeckViewScreen.{memberName} or a matching named descendant.",
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
                message: $"The {targetName} is not visible.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "deck_view_control",
                        Value: fallbackNodeName,
                        Note: "Retry when the deck view control is visible.",
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
                message: $"The {targetName} is disabled.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "deck_view_control",
                        Value: fallbackNodeName,
                        Note: "Retry when the deck view control is enabled.",
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
                $"No callable deck view OnRelease hook was found for action '{actionName}' on control type '{control.GetType().FullName}'.");
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: $"Could not execute {actionName} through the deck view hook.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "deck_view_control",
                        Value: fallbackNodeName,
                        Note: "The located deck view control did not expose a callable parameterless OnRelease hook.",
                        ReasonCode: ActionFailureCode.MissingHook,
                        Screen: screen.ScreenType,
                        PlayerId: localPlayerId,
                        CheckedHookPaths: checkedHookPaths),
                ],
                screen: screen.ScreenType,
                playerId: localPlayerId,
                checkedHookPaths: checkedHookPaths);
        }

        _logStream.Write(BridgeLogLevel.Info, "bridge.action", $"Executed {actionName} through the deck view.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:{actionName}:{request.RequestId}",
            kind: request.Kind,
            message: successMessage);
    }

    private static Control? ResolveDescendantControlByName(Node root, string nodeName)
    {
        foreach (var child in Sts2TreeSearch.FindDescendants(
            root,
            static node => node.GetChildren().OfType<Node>(),
            node => string.Equals(node.Name.ToString(), nodeName, StringComparison.Ordinal)))
        {
            if (child is Control control)
            {
                return control;
            }
        }

        return null;
    }
}
