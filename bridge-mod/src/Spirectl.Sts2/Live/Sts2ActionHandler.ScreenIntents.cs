using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Combat;
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
    // Sts2ActionHandler.ScreenIntents.Full.cs, moved verbatim, which the embedded profile does not compile.

    private ActionExecutionResult ExecuteClaimReward(SemanticActionRequest request)
    {
        var rewardId = ResolveRequestValue(request, "rewardId");
        var elementId = request.ElementId ?? ResolveRequestValue(request, "elementId");
        var elementAddressed = string.IsNullOrWhiteSpace(rewardId) && !string.IsNullOrWhiteSpace(elementId);
        if (elementAddressed)
        {
            if (!TryResolveRewardContext(out var rewardContext))
            {
                return ActionExecutionResult.Failure(
                    kind: request.Kind,
                    code: ActionFailureCode.WrongScreen,
                    message: "claim-reward element addressing is only available on the live rewards screen.",
                    details: [new ActionFailureDetail("screen", _screenLocator.Locate().ScreenType, "Retry when state.screen.id is rewards.")]);
            }

            rewardId = Sts2RewardElementChoice.Resolve(
                elementId,
                rewardContext.ChoicesById.Values
                    .Where(choice => !choice.IsFlowChoice)
                    .Select(choice => (choice.Control.GetInstanceId(), choice.Snapshot.Id)));
            if (string.IsNullOrWhiteSpace(rewardId))
            {
                return ActionExecutionResult.Failure(
                    kind: request.Kind,
                    code: ActionFailureCode.NotVisible,
                    message: "The requested reward element is not visible on the active rewards screen.",
                    details: [new ActionFailureDetail("element_id", elementId, "Use a current NRewardButton id from the scene stream.")]);
            }
        }

        if (string.IsNullOrWhiteSpace(rewardId))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "claim-reward requires a stable reward id from the current rewards screen.",
                details: [new ActionFailureDetail("reward_id", string.Empty, "Provide state.rewards.rewards[].id, availableActions[].arguments.values.rewardId, or a current reward element id.")]);
        }

        if (!Sts2RewardIds.TryParseRewardChoiceId(rewardId, out _, out _)
            && !Sts2RewardIds.TryParseVisibleRewardChoiceId(rewardId, out _, out _))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.StaleId,
                message: "claim-reward requires a stable first-party reward id.",
                details: [new ActionFailureDetail("reward_id", rewardId, "Expected an id shaped like reward:<player-id>:<reward-index> or reward:<player-id>:visible:<visible-index>.")]);
        }

        // A browser addresses the row it can see, not the reward-set synchronization state behind it. Immediate
        // rewards can therefore use the same direct, player-keyed commit path already required by browser-only
        // seats. Undoable card/removal rewards still need GetReward to open their native selection screen.
        if (elementAddressed && IsImmediateLiveReward(rewardId))
        {
            return ExecuteRewardSeatClaimCommit(request, rewardId);
        }

        // A NON-host browser seat has no native NRewardsScreen button to press, so the GetReward path
        // can't apply its immediate (gold/relic/potion/special-card) reward. Commit it headlessly from
        // the captured RewardsSet. The host-local "me" seat keeps the native GetReward path below.
        if (IsCapturedSeatReward(rewardId))
        {
            return ExecuteRewardSeatClaimCommit(request, rewardId);
        }

        return ExecuteRewardIntentChoice(request, rewardId, SemanticActionKind.ClaimReward, expectedFlow: false);
    }

    private ActionExecutionResult ExecuteRewardIntentChoice(
        SemanticActionRequest request,
        string choiceId,
        SemanticActionKind actionKind,
        bool expectedFlow)
    {
        if (!TryResolveRewardContext(out var rewardContext))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.WrongScreen,
                message: $"{ResolveActionName(actionKind)} is only available on the live rewards screen.",
                details: [new ActionFailureDetail("screen", _screenLocator.Locate().ScreenType, "Retry when state.screen.id is rewards.")]);
        }

        var requestedChoiceId = choiceId;
        choiceId = ResolveRewardChoiceIdAlias(choiceId, rewardContext);
        if (!rewardContext.ChoicesById.TryGetValue(choiceId, out var rewardChoice)
            && expectedFlow)
        {
            // The rewards Proceed/Skip button changes id between "Skip" (rewards still unclaimed) and
            // "Proceed" (all rewards claimed) modes. The browser dispatches skip-rewards without a specific
            // flow id (ExecuteSkipRewards defaults to SkipFlowChoiceId), so once everything is claimed the
            // default no longer matches and the run wedged. Match whichever flow choice the live screen
            // currently exposes.
            rewardChoice = rewardContext.ChoicesById.Values.FirstOrDefault(candidate => candidate.IsFlowChoice);
        }
        if (rewardChoice is null)
        {
            var code = expectedFlow && Sts2RewardIds.TryParseFlowChoiceId(choiceId, out _)
                || !expectedFlow && (Sts2RewardIds.TryParseRewardChoiceId(requestedChoiceId, out _, out _)
                    || Sts2RewardIds.TryParseVisibleRewardChoiceId(requestedChoiceId, out _, out _))
                    ? ActionFailureCode.NotVisible
                    : ActionFailureCode.StaleId;
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: code,
                message: "The requested rewards control is not visible on the active rewards screen.",
                details: [new ActionFailureDetail(expectedFlow ? "reward_flow_id" : "reward_id", requestedChoiceId, "Use a currently visible id from state.rewards or availableActions.")]);
        }

        var requestedPlayerId = request.Perspective?.PlayerId ?? ResolveRequestValue(request, "playerId");
        if (BuildOwnershipFailure(
                request,
                new ActionOwnershipContext(
                    RequestedPlayerId: requestedPlayerId,
                    ResolvedOwnerPlayerId: rewardChoice.PlayerId,
                    LocalPlayerId: rewardContext.LocalPlayerId,
                    HostPlayerId: ResolveRunHostPlayerId(rewardContext.LocalPlayerId),
                    HostLocalPlayerIds: [],
                    Screen: rewardContext.Screen.ScreenType,
                    Action: ResolveActionName(actionKind)),
                out var ownershipFailure))
        {
            return ownershipFailure;
        }

        if (!string.IsNullOrWhiteSpace(requestedPlayerId)
            && !string.IsNullOrWhiteSpace(rewardChoice.PlayerId)
            && !string.Equals(requestedPlayerId, rewardChoice.PlayerId, StringComparison.Ordinal))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.WrongPlayer,
                message: "The requested rewards control belongs to a different player.",
                details: [new ActionFailureDetail("player_id", requestedPlayerId, $"choiceOwnerPlayerId={rewardChoice.PlayerId}.")]);
        }

        if (rewardChoice.IsFlowChoice != expectedFlow)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.StaleId,
                message: "The requested rewards id does not match this action kind.",
                details: [new ActionFailureDetail(expectedFlow ? "reward_flow_id" : "reward_id", choiceId, $"Use {ResolveActionName(actionKind)} with a matching visible rewards id.")]);
        }

        // A reward ITEM (gold/card/relic/potion) whose only problem is the headless-unreliable
        // Visible/IsEnabled flag should still claim through the live GetReward() hook — the engine
        // arbitrates a genuinely unclaimable reward — the same wall as the treasure relic holder and
        // rest-site options. The FLOW choice (skip/proceed) keeps the strict gate: its enabled state is
        // reliable and meaningful (whether the rewards screen may be left yet).
        if (!rewardChoice.IsExecutable && expectedFlow)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.NotEnabled,
                message: "The requested rewards control is visible but not enabled.",
                details: [new ActionFailureDetail(expectedFlow ? "reward_flow_id" : "reward_id", choiceId, "Retry when the control appears in availableActions.")]);
        }

        var executed = expectedFlow
            ? Sts2LiveIntrospection.TryInvokeMethod(rewardContext.ScreenObject, "OnProceedButtonPressed", rewardChoice.Control)
            : Sts2LiveIntrospection.TryInvokeParameterlessMethod(rewardChoice.Control, "GetReward");
        if (!executed)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not execute the requested rewards control through the active live hooks.",
                details: [new ActionFailureDetail(expectedFlow ? "reward_flow_id" : "reward_id", choiceId, expectedFlow ? "Missing rewardsScreen.OnProceedButtonPressed hook." : "Missing rewardButton.GetReward hook.")]);
        }

        return ActionExecutionResult.Success(
            actionInstanceId: $"action:{ResolveActionName(actionKind)}:{request.RequestId}",
            kind: request.Kind,
            message: $"Executed {ResolveActionName(actionKind)} '{choiceId}'.");
    }

    private static string ResolveRewardChoiceIdAlias(string choiceId, RewardActionContext rewardContext)
    {
        if (!Sts2RewardIds.TryParseVisibleRewardChoiceId(choiceId, out var playerId, out var visibleIndex))
        {
            return choiceId;
        }

        return rewardContext.ChoicesById.Values
            .FirstOrDefault(choice =>
                !choice.IsFlowChoice
                && choice.DisplayIndex == visibleIndex
                && string.Equals(choice.PlayerId, playerId, StringComparison.Ordinal))
            ?.Snapshot.Id
            ?? choiceId;
    }

    private static string? ResolveRequestValue(SemanticActionRequest request, string key)
        => request.Values is not null && request.Values.TryGetValue(key, out var value)
            ? value?.Trim()
            : null;

    private static string ResolveActionName(SemanticActionKind kind)
        => kind switch
        {
            SemanticActionKind.ClaimReward => "claim-reward",
            SemanticActionKind.SkipRewards => "skip-rewards",
            SemanticActionKind.SelectCard => "select-card",
            SemanticActionKind.SkipCardSelection => "skip-card-selection",
            SemanticActionKind.SelectBundle => "select-bundle",
            SemanticActionKind.BuyCard => "buy-card",
            SemanticActionKind.BuyRelic => "buy-relic",
            SemanticActionKind.BuyPotion => "buy-potion",
            SemanticActionKind.RemoveCard => "remove-card",
            SemanticActionKind.LeaveShop => "leave-shop",
            SemanticActionKind.CloseShopInventory => "close-shop-inventory",
            SemanticActionKind.BackFromMap => "back-from-map",
            SemanticActionKind.SelectEventOption => "select-event-option",
            SemanticActionKind.OpenEventShop => "open-event-shop",
            SemanticActionKind.UseCrystalSphereControl => "use-crystal-sphere-control",
            SemanticActionKind.ProceedEvent => "proceed-event",
            SemanticActionKind.JoinLobbyPlayer => "join-lobby-player",
            SemanticActionKind.LeaveLobbyPlayer => "leave-lobby-player",
            _ => kind.ToString(),
        };

}
