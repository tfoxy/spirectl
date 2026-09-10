using Godot;
using MegaCrit.Sts2.Core.Nodes;
using Spirectl.Sts2;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;

public sealed partial class Sts2ActionHandler
{
    private ActionExecutionResult ExecuteToggleMap(SemanticActionRequest request)
        => ExecuteToggleTopBarAction(request, "toggle-map", "Map", "Map", "map");

    private ActionExecutionResult ExecuteToggleDeck(SemanticActionRequest request)
        => ExecuteToggleTopBarAction(request, "toggle-deck", "Deck", "Deck", "deck");

    private ActionExecutionResult ExecuteToggleSettings(SemanticActionRequest request)
        => ExecuteToggleTopBarAction(request, "toggle-settings", "Pause", "Settings", "settings");

    private ActionExecutionResult ExecuteToggleTopBarAction(
        SemanticActionRequest request,
        string actionName,
        string topBarMemberName,
        string label,
        string targetName)
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

        var topBar = Sts2LiveIntrospection.GetMemberValue(
            Sts2LiveIntrospection.GetMemberValue(NRun.Instance, "GlobalUi"),
            "TopBar");
        if (topBar is not Node topBarNode)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.WrongScreen,
                message: $"{actionName} is only available while a run top bar is present.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "screen",
                        Value: screen.ScreenType,
                        Note: "Retry while the game is in a run and the top bar is mounted.",
                        ReasonCode: ActionFailureCode.WrongScreen,
                        Screen: screen.ScreenType,
                        PlayerId: localPlayerId),
                ],
                screen: screen.ScreenType,
                playerId: localPlayerId);
        }

        var button = Sts2LiveIntrospection.GetMemberValue(topBar, topBarMemberName) as Control
            ?? ResolveTopBarButtonByName(topBarNode, topBarMemberName);
        if (button is null)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: $"Could not locate the run top-bar {label.ToLowerInvariant()} button.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "top_bar_button",
                        Value: topBarMemberName,
                        Note: $"Expected NRun.Instance.GlobalUi.TopBar.{topBarMemberName} or a matching named descendant.",
                        ReasonCode: ActionFailureCode.MissingHook,
                        Screen: screen.ScreenType,
                        PlayerId: localPlayerId,
                        CheckedHookPaths: [$"NRun.Instance.GlobalUi.TopBar.{topBarMemberName}", $"TopBar/**/{topBarMemberName}"]),
                ],
                screen: screen.ScreenType,
                playerId: localPlayerId,
                checkedHookPaths: [$"NRun.Instance.GlobalUi.TopBar.{topBarMemberName}", $"TopBar/**/{topBarMemberName}"]);
        }

        if (!button.Visible)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.NotVisible,
                message: $"The run top-bar {label.ToLowerInvariant()} button is not visible.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "top_bar_button",
                        Value: topBarMemberName,
                        Note: "Retry when the top-bar control is visible.",
                        ReasonCode: ActionFailureCode.NotVisible,
                        Screen: screen.ScreenType,
                        PlayerId: localPlayerId),
                ],
                screen: screen.ScreenType,
                playerId: localPlayerId);
        }

        if (IsControlDisabled(button))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.NotEnabled,
                message: $"The run top-bar {label.ToLowerInvariant()} button is disabled.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "top_bar_button",
                        Value: topBarMemberName,
                        Note: "Retry when the top-bar control is enabled.",
                        ReasonCode: ActionFailureCode.NotEnabled,
                        Screen: screen.ScreenType,
                        PlayerId: localPlayerId),
                ],
                screen: screen.ScreenType,
                playerId: localPlayerId);
        }

        if (!Sts2LiveIntrospection.TryInvokeParameterlessMethod(button, "OnRelease"))
        {
            _logStream.Write(
                BridgeLogLevel.Error,
                "bridge.action",
                $"No callable top-bar OnRelease hook was found for action '{actionName}' on button type '{button.GetType().FullName}'.");
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: $"Could not toggle {targetName} through the run top-bar hook.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "top_bar_button",
                        Value: topBarMemberName,
                        Note: "The located top-bar button did not expose a callable parameterless OnRelease hook.",
                        ReasonCode: ActionFailureCode.MissingHook,
                        Screen: screen.ScreenType,
                        PlayerId: localPlayerId,
                        CheckedHookPaths: [$"NRun.Instance.GlobalUi.TopBar.{topBarMemberName}.OnRelease"]),
                ],
                screen: screen.ScreenType,
                playerId: localPlayerId,
                checkedHookPaths: [$"NRun.Instance.GlobalUi.TopBar.{topBarMemberName}.OnRelease"]);
        }

        _logStream.Write(
            BridgeLogLevel.Info,
            "bridge.action",
            $"Executed {actionName} through the run top bar.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:{actionName}:{request.RequestId}",
            kind: request.Kind,
            message: $"Toggled {targetName}.");
    }

    private static Control? ResolveTopBarButtonByName(Node topBar, string topBarMemberName)
    {
        foreach (var child in Sts2TreeSearch.FindDescendants(
            topBar,
            static node => node.GetChildren().OfType<Node>(),
            node => string.Equals(node.Name.ToString(), topBarMemberName, StringComparison.Ordinal)))
        {
            if (child is Control control)
            {
                return control;
            }
        }

        return null;
    }

    private static bool IsControlDisabled(object control)
    {
        return Sts2LiveIntrospection.GetMemberValue(control, "Disabled") switch
        {
            bool disabled => disabled,
            _ => Sts2LiveIntrospection.GetMemberValue(control, "IsDisabled") switch
            {
                bool isDisabled => isDisabled,
                _ => Sts2LiveIntrospection.GetMemberValue(control, "IsEnabled") switch
                {
                    bool isEnabled => !isEnabled,
                    _ => false,
                },
            },
        };
    }
}
