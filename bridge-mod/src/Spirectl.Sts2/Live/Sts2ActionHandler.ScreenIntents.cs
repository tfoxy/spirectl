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
    private ActionExecutionResult ExecuteChoose(SemanticActionRequest request)
    {
        var choiceId = request.ChoiceId?.Trim();
        if (string.IsNullOrWhiteSpace(choiceId))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "choose requires a visible choice id from the current screen.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "choice_id",
                        Value: string.Empty,
                        Note: "Provide a stable choice id from state.choices[].id."),
                ]);
        }

        var screen = _screenLocator.Locate();
        if (screen.ScreenType == "main-menu")
        {
            return ExecuteMainMenuChoice(
                request,
                screen,
                Sts2LiveIntrospection.ResolveCurrentScreenObject(),
                _logStream);
        }

        if (Sts2SupportedScreenIds.IsShopScreenType(screen.ScreenType))
        {
            return ExecuteShopChoice(request, choiceId);
        }

        if (Sts2SupportedScreenIds.IsCardSelectionFamily(screen.ScreenType))
        {
            return ExecuteCardSelectionChoice(request, choiceId);
        }

        if (Sts2SupportedScreenIds.IsEventRoomScreenType(screen.ScreenType))
        {
            return ExecuteEventRoomChoice(request, choiceId);
        }

        if (Sts2SupportedScreenIds.IsMapScreenType(screen.ScreenType))
        {
            return ExecuteMapChoice(request, choiceId);
        }

        if (Sts2SupportedScreenIds.IsCrystalSphereScreenType(screen.ScreenType))
        {
            return ExecuteCrystalSphereChoice(request, choiceId);
        }

        if (Sts2SupportedScreenIds.IsTreasureRoomFamily(screen.ScreenType))
        {
            return ExecuteTreasureRoomChoice(request, choiceId);
        }

        if (Sts2SupportedScreenIds.IsRestSiteScreenType(screen.ScreenType))
        {
            return ExecuteRestSiteChoice(request, choiceId);
        }

        if (!Sts2SupportedScreenIds.IsRewardsScreenType(screen.ScreenType))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose requires a visible choice id from the current screen.",
                "choice_id",
                choiceId,
                $"Choice '{choiceId}' is not executable on screen '{screen.ScreenType}' in this spec.");
        }

        if (!TryResolveRewardContext(out var rewardContext))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose is only available on the live rewards screen for visible reward choices.",
                "choice_id",
                choiceId,
                "Retry when state.screen.id is rewards and availableActions includes choose.");
        }

        if (!rewardContext.ChoicesById.TryGetValue(choiceId, out var rewardChoice))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose requires a visible reward choice id from the current screen.",
                "choice_id",
                choiceId,
                "Provide a stable reward choice id from state.choices[].id or availableActions[].arguments.choiceId.");
        }

        var requestedPlayerId = request.Perspective?.PlayerId;
        if (!string.IsNullOrWhiteSpace(requestedPlayerId)
            && !string.IsNullOrWhiteSpace(rewardChoice.PlayerId)
            && !string.Equals(requestedPlayerId, rewardChoice.PlayerId, StringComparison.Ordinal))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose is only available for the requested reward owner on the active rewards screen.",
                "player_id",
                requestedPlayerId,
                $"choiceOwnerPlayerId={rewardChoice.PlayerId}.");
        }

        if (!rewardChoice.IsExecutable)
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose requires a currently executable rewards choice.",
                "choice_id",
                choiceId,
                "Retry with a currently executable reward choice from availableActions[].arguments.choiceId.");
        }

        if (Sts2RewardIds.IsFlowChoiceId(choiceId))
        {
            if (!Sts2LiveIntrospection.TryInvokeMethod(rewardContext.ScreenObject, "OnProceedButtonPressed", rewardChoice.Control))
            {
                _logStream.Write(
                    BridgeLogLevel.Error,
                    "bridge.action",
                    $"No callable reward flow hook was found for choice '{choiceId}'.");
                return ActionExecutionResult.Failure(
                    kind: request.Kind,
                    code: ActionFailureCode.MissingHook,
                    message: "Could not execute the requested reward flow choice through the active live hooks.",
                    details:
                    [
                        new ActionFailureDetail(
                            Field: "choice_id",
                            Value: choiceId,
                            Note: "The active rewards screen did not expose a recognized proceed/skip callback."),
                    ]);
            }

            _logStream.Write(
                BridgeLogLevel.Info,
                "bridge.action",
                $"Executed reward flow '{rewardChoice.Snapshot.Label}'.");
            return ActionExecutionResult.Success(
                actionInstanceId: $"action:choose:{request.RequestId}",
                kind: request.Kind,
                message: $"Executed choice '{choiceId}'.");
        }

        if (!Sts2LiveIntrospection.TryInvokeParameterlessMethod(rewardChoice.Control, "GetReward"))
        {
            _logStream.Write(
                BridgeLogLevel.Error,
                "bridge.action",
                $"No callable reward hook was found for choice '{choiceId}'.");
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not execute the requested reward choice through the active live hooks.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "choice_id",
                        Value: choiceId,
                        Note: "The active rewards screen did not expose a recognized reward callback."),
                ]);
        }

        _logStream.Write(
            BridgeLogLevel.Info,
            "bridge.action",
            $"Selected reward '{rewardChoice.Snapshot.Label}'.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:choose:{request.RequestId}",
            kind: request.Kind,
            message: $"Executed choice '{choiceId}'.");
    }

    private ActionExecutionResult ExecuteCardSelectionChoice(SemanticActionRequest request, string choiceId)
    {
        if (!TryResolveCardSelectionContext(out var cardSelectionContext))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose is only available on live card-selection overlays for visible card or skip choices.",
                "choice_id",
                choiceId,
                "Retry when state.screen.id is card-selection and availableActions includes choose.");
        }

        if (!cardSelectionContext.ChoicesById.TryGetValue(choiceId, out var choice))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose requires a visible card-selection choice id from the current screen.",
                "choice_id",
                choiceId,
                "Provide a stable card-selection choice id from state.choices[].id or availableActions[].arguments.choiceId.");
        }

        var requestedPlayerId = request.Perspective?.PlayerId;
        if (!string.IsNullOrWhiteSpace(requestedPlayerId)
            && !string.IsNullOrWhiteSpace(cardSelectionContext.LocalPlayerId)
            && !string.Equals(requestedPlayerId, cardSelectionContext.LocalPlayerId, StringComparison.Ordinal))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose is only available for the local player on the active card-selection screen.",
                "player_id",
                requestedPlayerId,
                $"localPlayerId={cardSelectionContext.LocalPlayerId}.");
        }

        if (!choice.IsExecutable)
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose requires a currently executable card-selection choice.",
                "choice_id",
                choiceId,
                "Retry with a currently executable card-selection choice from availableActions[].arguments.choiceId.");
        }

        var executed = choice.ExecutionKind switch
        {
            CardSelectionChoiceExecutionKind.Card when !string.IsNullOrWhiteSpace(choice.ExecuteMethodName)
                => Sts2LiveIntrospection.TryInvokeMethod(
                    cardSelectionContext.ScreenObject,
                    choice.ExecuteMethodName!,
                    choice.Target),
            CardSelectionChoiceExecutionKind.SkipButton when !string.IsNullOrWhiteSpace(choice.ExecuteMethodName)
                => Sts2LiveIntrospection.TryInvokeMethod(
                    cardSelectionContext.ScreenObject,
                    choice.ExecuteMethodName!,
                    choice.Target),
            CardSelectionChoiceExecutionKind.Alternative
                => TryInvokeCardSelectionAlternative(cardSelectionContext.ScreenObject, choice),
            CardSelectionChoiceExecutionKind.Bundle
                => TryExecuteBundleSelectionChoice(cardSelectionContext.ScreenObject, choice),
            _ => false,
        };

        if (!executed)
        {
            _logStream.Write(
                BridgeLogLevel.Error,
                "bridge.action",
                $"No callable card-selection hook was found for choice '{choiceId}'.");
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not execute the requested card-selection choice through the active live hooks.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "choice_id",
                        Value: choiceId,
                        Note: "The active card-selection overlay did not expose a recognized selection or skip callback."),
                ]);
        }

        _logStream.Write(
            BridgeLogLevel.Info,
            "bridge.action",
            $"Accepted card-selection choice '{choice.Snapshot.Label}'.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:choose:{request.RequestId}",
            kind: request.Kind,
            message: $"Executed choice '{choiceId}'.");
    }

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

    private ActionExecutionResult ExecuteSkipRewards(SemanticActionRequest request)
    {
        var rewardId = ResolveRequestValue(request, "rewardId");
        if (string.IsNullOrWhiteSpace(rewardId))
        {
            rewardId = Sts2RewardIds.SkipFlowChoiceId;
        }

        return ExecuteRewardIntentChoice(request, rewardId, SemanticActionKind.SkipRewards, expectedFlow: true);
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

    private ActionExecutionResult ExecuteSelectCard(SemanticActionRequest request)
    {
        var cardId = (ResolveRequestValue(request, "cardId") ?? request.CardId)?.Trim();

        // Per-player undoable card-CHOICE commit: when a reward id rides along, the client opened
        // the card choice in its OWN overlay (no live host selection screen), so commit the chosen
        // offered card directly to the owning player's deck.
        var rewardId = ResolveRequestValue(request, "rewardId");
        if (!string.IsNullOrWhiteSpace(rewardId))
        {
            return ExecuteRewardSeatChoiceCommit(request, rewardId, cardId ?? string.Empty);
        }

        if (string.IsNullOrWhiteSpace(cardId))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "select-card requires a stable visible card-selection card id.",
                details: [new ActionFailureDetail("card_id", string.Empty, "Provide state.cardSelection.cards[].id or availableActions[].arguments.cardId.")]);
        }

        if (!Sts2CardSelectionIds.TryParseCardChoiceId(cardId, out _, out _))
        {
            return ExecuteSelectCombatHandCard(request, cardId);
        }

        return ExecuteCardSelectionIntentChoice(request, cardId, CardSelectionChoiceExecutionKind.Card, "select-card");
    }

    private ActionExecutionResult ExecuteSelectCombatHandCard(SemanticActionRequest request, string cardId)
    {
        var requestedPlayerId = ResolveRequestValue(request, "playerId") ?? request.Perspective?.PlayerId;
        if (!TryResolveCombatContext(requestedPlayerId, out var combatContext))
        {
            return CombatActionUnavailable(
                request.Kind,
                "select-card requires a visible combat hand card or card-selection card.",
                "card_id",
                cardId,
                "Retry when state.run.currentRoom.combat is visible, or provide an id shaped like card-selection:card:<card-id>:<index> on card-selection screens.");
        }

        if (BuildOwnershipFailure(
                request,
                new ActionOwnershipContext(
                    RequestedPlayerId: requestedPlayerId,
                    ResolvedOwnerPlayerId: combatContext.ActionOwnerPlayerId,
                    LocalPlayerId: combatContext.LocalPlayerId,
                    HostPlayerId: combatContext.HostPlayerId,
                    HostLocalPlayerIds: combatContext.HostLocalPlayerIds,
                    Screen: combatContext.Screen.ScreenType,
                    Action: "select-card"),
                out var ownershipFailure))
        {
            return ownershipFailure;
        }

        var resolvedCard = ResolveLocalHandCard(combatContext.ActionOwnerPlayer, combatContext.ActionOwnerPlayerId, cardId);
        if (resolvedCard is null)
        {
            return CombatActionUnavailable(
                request.Kind,
                "select-card requires a stable card id from the current combat hand.",
                "card_id",
                cardId,
                "Provide a stable id from state.run.players[].combat.hand.cards[].id.");
        }

        var selected = _selectedCardViewState.Toggle(combatContext.ActionOwnerPlayerId, resolvedCard.Id);
        var message = selected is null
            ? $"Deselected {resolvedCard.Name}."
            : $"Selected {resolvedCard.Name}.";
        _logStream.Write(BridgeLogLevel.Info, "bridge.action", message);
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:select-card:{request.RequestId}",
            kind: request.Kind,
            message: message);
    }

    private ActionExecutionResult ExecuteSkipCardSelection(SemanticActionRequest request)
        => ExecuteCardSelectionIntentChoice(request, Sts2CardSelectionIds.SkipChoiceId(), CardSelectionChoiceExecutionKind.SkipButton, "skip-card-selection");

    private ActionExecutionResult ExecuteSelectBundle(SemanticActionRequest request)
    {
        var bundleId = ResolveRequestValue(request, "bundleId");
        if (string.IsNullOrWhiteSpace(bundleId))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "select-bundle requires a stable visible bundle id.",
                details: [new ActionFailureDetail("bundle_id", string.Empty, "Provide state.bundleSelection.bundles[].id or availableActions[].arguments.values.bundleId.")]);
        }

        if (!Sts2CardSelectionIds.TryParseBundleChoiceId(bundleId, out _, out _))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.StaleId,
                message: "select-bundle requires a stable first-party bundle id.",
                details: [new ActionFailureDetail("bundle_id", bundleId, "Expected an id shaped like card-selection:bundle:<bundle-id>:<index>.")]);
        }

        return ExecuteCardSelectionIntentChoice(request, bundleId, CardSelectionChoiceExecutionKind.Bundle, "select-bundle");
    }

    private ActionExecutionResult ExecuteCardSelectionIntentChoice(
        SemanticActionRequest request,
        string choiceId,
        CardSelectionChoiceExecutionKind expectedKind,
        string actionName)
    {
        if (!TryResolveCardSelectionContext(out var cardSelectionContext))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.WrongScreen,
                message: $"{actionName} is only available on supported card-selection overlays.",
                details: [new ActionFailureDetail("screen", _screenLocator.Locate().ScreenType, "Retry when the corresponding card-selection screen is active.")]);
        }

        if (!cardSelectionContext.ChoicesById.TryGetValue(choiceId, out var choice))
        {
            var shaped = expectedKind switch
            {
                CardSelectionChoiceExecutionKind.Card => Sts2CardSelectionIds.TryParseCardChoiceId(choiceId, out _, out _),
                CardSelectionChoiceExecutionKind.Bundle => Sts2CardSelectionIds.TryParseBundleChoiceId(choiceId, out _, out _),
                CardSelectionChoiceExecutionKind.SkipButton => Sts2CardSelectionIds.IsSkipChoiceId(choiceId),
                _ => false,
            };
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: shaped ? ActionFailureCode.NotVisible : ActionFailureCode.StaleId,
                message: "The requested card-selection control is not visible on the active screen.",
                details: [new ActionFailureDetail("choice_id", choiceId, "Use a currently visible id from state or availableActions.")]);
        }

        var requestedPlayerId = request.Perspective?.PlayerId ?? ResolveRequestValue(request, "playerId");
        if (BuildOwnershipFailure(
                request,
                new ActionOwnershipContext(
                    RequestedPlayerId: requestedPlayerId,
                    ResolvedOwnerPlayerId: cardSelectionContext.LocalPlayerId,
                    LocalPlayerId: cardSelectionContext.LocalPlayerId,
                    HostPlayerId: ResolveRunHostPlayerId(cardSelectionContext.LocalPlayerId),
                    HostLocalPlayerIds: [],
                    Screen: cardSelectionContext.Screen.ScreenType,
                    Action: actionName),
                out var ownershipFailure))
        {
            return ownershipFailure;
        }

        if (!string.IsNullOrWhiteSpace(requestedPlayerId)
            && !string.IsNullOrWhiteSpace(cardSelectionContext.LocalPlayerId)
            && !string.Equals(requestedPlayerId, cardSelectionContext.LocalPlayerId, StringComparison.Ordinal))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.WrongPlayer,
                message: "The requested card-selection control belongs to a different player.",
                details: [new ActionFailureDetail("player_id", requestedPlayerId, $"localPlayerId={cardSelectionContext.LocalPlayerId}.")]);
        }

        var kindMatches = choice.ExecutionKind == expectedKind
            || expectedKind == CardSelectionChoiceExecutionKind.SkipButton && choice.ExecutionKind == CardSelectionChoiceExecutionKind.Alternative;
        if (!kindMatches)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.StaleId,
                message: "The requested card-selection id does not match this action kind.",
                details: [new ActionFailureDetail("choice_id", choiceId, $"Use {actionName} with a matching visible card-selection id.")]);
        }

        // A CARD pick whose only problem is the (headless-unreliable) holder Disabled/IsEnabled flag should
        // still fall through to the live hook below — the engine arbitrates a genuinely invalid pick — the
        // same headless wall as the treasure relic holder and rest-site options. Skip and Bundle keep the
        // strict gate (their enabled state is reliable and meaningful).
        if (!choice.IsExecutable && choice.ExecutionKind != CardSelectionChoiceExecutionKind.Card)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.NotEnabled,
                message: "The requested card-selection control is visible but not enabled.",
                details: [new ActionFailureDetail("choice_id", choiceId, "Retry when the control appears in availableActions.")]);
        }

        var executed = choice.ExecutionKind switch
        {
            CardSelectionChoiceExecutionKind.Card when !string.IsNullOrWhiteSpace(choice.ExecuteMethodName)
                => Sts2LiveIntrospection.TryInvokeMethod(cardSelectionContext.ScreenObject, choice.ExecuteMethodName!, choice.Target),
            CardSelectionChoiceExecutionKind.SkipButton when !string.IsNullOrWhiteSpace(choice.ExecuteMethodName)
                => Sts2LiveIntrospection.TryInvokeMethod(cardSelectionContext.ScreenObject, choice.ExecuteMethodName!, choice.Target),
            CardSelectionChoiceExecutionKind.Alternative
                => TryInvokeCardSelectionAlternative(cardSelectionContext.ScreenObject, choice),
            CardSelectionChoiceExecutionKind.Bundle
                => TryExecuteBundleSelectionChoice(cardSelectionContext.ScreenObject, choice),
            _ => false,
        };
        if (!executed)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not execute the requested card-selection control through the active live hooks.",
                details: [new ActionFailureDetail("choice_id", choiceId, "The active card-selection overlay did not expose a recognized selection, skip, or bundle callback.")]);
        }

        // A deck/simple card-selection screen (NDeckCardSelect* / NSimpleCardSelectScreen — the full-deck
        // remove/upgrade/transform/enchant events) only STAGES a card pick: OnCardClicked highlights it and,
        // once the staged count reaches MaxSelect, the game auto-opens a preview whose Confirm button still
        // has to be released to commit (CheckIfSelectionComplete → completionSource). The browser/headless
        // surface exposes no separate confirm gesture, so the bot would keep toggling cards forever. Once the
        // staged selection is full (the human's final pick), commit it here — this is exactly clicking Confirm.
        // ChooseACard (SelectHolder) commits on the first pick and is NOT a partial-grid screen, so it is
        // unaffected; the combat card reward takes the ClaimReward path, not this one.
        if (choice.ExecutionKind == CardSelectionChoiceExecutionKind.Card
            && Sts2CardSelectionScreenInspector.IsPartialCardGridScreen(cardSelectionContext.ScreenObject))
        {
            var maxSelect = Sts2CardSelectionScreenInspector.ResolveCardSelectionMaxSelect(cardSelectionContext.ScreenObject);
            var selectedCount = Sts2CardSelectionScreenInspector.ResolveSelectedCardCount(cardSelectionContext.ScreenObject);
            if (maxSelect is > 0
                && selectedCount >= maxSelect.Value
                && Sts2CardSelectionScreenInspector.TryConfirmSelection(cardSelectionContext.ScreenObject))
            {
                return ActionExecutionResult.Success(
                    actionInstanceId: $"action:{actionName}:{request.RequestId}",
                    kind: request.Kind,
                    message: $"Executed {actionName} '{choiceId}' (auto-committed staged selection).");
            }
        }

        return ActionExecutionResult.Success(
            actionInstanceId: $"action:{actionName}:{request.RequestId}",
            kind: request.Kind,
            message: $"Executed {actionName} '{choiceId}'.");
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

    private ActionExecutionResult ExecuteConfirmSelection(SemanticActionRequest request)
    {
        // Per-player undoable card-REMOVAL commit: when a reward id rides along, the client opened
        // the removal in its OWN overlay (no live host selection screen), so commit it directly
        // against the owning player's reward + deck instead of staging on a live selection screen.
        var rewardId = ResolveRequestValue(request, "rewardId");
        if (!string.IsNullOrWhiteSpace(rewardId))
        {
            return ExecuteRewardSeatRemovalCommit(request, rewardId, request.CardIds ?? Array.Empty<string>());
        }

        if (!TryResolveCardSelectionContext(out var cardSelectionContext))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "confirm-selection is only available on supported card-selection overlays with a staged selection.",
                "screen",
                _screenLocator.Locate().ScreenType,
                "Retry when state.screen.id is simple-card-selection or deck-card-selection and availableActions includes confirm-selection.");
        }

        // Fold staging + confirm: a client that keeps the deck-card selection purely local
        // (no per-click host round-trip) commits its staged ids here in one call. The host
        // starts with nothing staged, so stage the requested cards first; empty cardIds
        // preserves the original behavior (confirm whatever is already staged). Mirrors
        // confirm-hand-selection.
        var requestedCardIds = request.CardIds ?? Array.Empty<string>();
        if (requestedCardIds.Count > 0
            && !Sts2CardSelectionScreenInspector.TryStageDeckSelection(cardSelectionContext.ScreenObject, requestedCardIds, out var unresolvedCardId))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "confirm-selection could not stage one of the requested card ids on the active deck-card-selection overlay.",
                "card_id",
                unresolvedCardId ?? string.Empty,
                "Pass current ids from state.run.players[].overlays[].deckCardSelection.cards[].id; ids go stale when the overlay changes.");
        }

        if (!Sts2CardSelectionScreenInspector.CanConfirmSelection(cardSelectionContext.ScreenObject))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "confirm-selection requires a currently staged selection on the active overlay.",
                "selection",
                string.Empty,
                "Stage one or more visible card choices first, then retry when availableActions includes confirm-selection.");
        }

        if (!Sts2CardSelectionScreenInspector.TryConfirmSelection(cardSelectionContext.ScreenObject))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not confirm the staged selection through the active live hooks.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "selection",
                        Value: string.Empty,
                        Note: "The active selection overlay did not expose a recognized confirm callback."),
                ]);
        }

        _logStream.Write(
            BridgeLogLevel.Info,
            "bridge.action",
            "Confirmed the staged selection.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:confirm-selection:{request.RequestId}",
            kind: request.Kind,
            message: "Confirmed the staged selection.");
    }

    private ActionExecutionResult ExecuteCancelSelection(SemanticActionRequest request)
    {
        if (Sts2TargetManagerAccess.InstanceOrNull is { IsInSelection: true } targetManager)
        {
            targetManager.CancelTargeting();
            _logStream.Write(
                BridgeLogLevel.Info,
                "bridge.action",
                "Cancelled target selection.");
            return ActionExecutionResult.Success(
                actionInstanceId: $"action:cancel-selection:{request.RequestId}",
                kind: request.Kind,
                message: "Cancelled target selection.");
        }

        if (!TryResolveCardSelectionContext(out var cardSelectionContext))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "cancel-selection is only available on supported card-selection overlays with a staged selection.",
                "screen",
                _screenLocator.Locate().ScreenType,
                "Retry when state.screen.id is simple-card-selection or deck-card-selection and availableActions includes cancel-selection.");
        }

        if (!Sts2CardSelectionScreenInspector.CanCancelSelection(cardSelectionContext.ScreenObject))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "cancel-selection requires a currently staged selection on the active overlay.",
                "selection",
                string.Empty,
                "Stage one or more visible card choices first, then retry when availableActions includes cancel-selection.");
        }

        if (!Sts2CardSelectionScreenInspector.TryCancelSelection(cardSelectionContext.ScreenObject))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not cancel the staged selection through the active live hooks.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "selection",
                        Value: string.Empty,
                        Note: "The active selection overlay did not expose a recognized cancel callback."),
                ]);
        }

        _logStream.Write(
            BridgeLogLevel.Info,
            "bridge.action",
            "Cancelled the staged selection.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:cancel-selection:{request.RequestId}",
            kind: request.Kind,
            message: "Cancelled the staged selection.");
    }

    private ActionExecutionResult ExecuteEventRoomChoice(SemanticActionRequest request, string choiceId)
    {
        if (!TryResolveEventRoomContext(out var eventRoomContext))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose is only available in the live event room for visible event options.",
                "choice_id",
                choiceId,
                "Retry when state.screen.id is event-room and availableActions includes choose.");
        }

        if (!eventRoomContext.ChoicesById.TryGetValue(choiceId, out var choice))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose requires a visible event-room choice id from the current room.",
                "choice_id",
                choiceId,
                "Provide a stable event-room choice id from state.choices[].id or availableActions[].arguments.choiceId.");
        }

        var requestedPlayerId = request.Perspective?.PlayerId;
        if (!string.IsNullOrWhiteSpace(requestedPlayerId)
            && !string.IsNullOrWhiteSpace(eventRoomContext.LocalPlayerId)
            && !string.Equals(requestedPlayerId, eventRoomContext.LocalPlayerId, StringComparison.Ordinal))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose is only available for the local player on the active event room.",
                "player_id",
                requestedPlayerId,
                $"localPlayerId={eventRoomContext.LocalPlayerId}.");
        }

        if (!choice.IsExecutable)
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose requires a currently executable event-room choice.",
                "choice_id",
                choiceId,
                "Retry with a currently executable event-room choice from availableActions[].arguments.choiceId.");
        }

        var executed = TryExecuteEventRoomChoice(eventRoomContext, choice);

        if (!executed)
        {
            _logStream.Write(
                BridgeLogLevel.Error,
                "bridge.action",
                $"No callable event-room hook was found for choice '{choiceId}'.");
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not execute the requested event-room choice through the active live hooks.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "choice_id",
                        Value: choiceId,
                        Note: choice.ExecutionKind == EventRoomChoiceExecutionKind.FakeMerchantOpenShop
                            ? "The active Fake Merchant button did not expose a recognized release callback."
                            : "The active event room did not expose a recognized option callback."),
                ]);
        }

        _logStream.Write(
            BridgeLogLevel.Info,
            "bridge.action",
            choice.ExecutionKind == EventRoomChoiceExecutionKind.FakeMerchantOpenShop
                ? "Opened the Fake Merchant shop."
                : $"Selected event-room option '{choice.Snapshot.Label}'.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:choose:{request.RequestId}",
            kind: request.Kind,
            message: $"Executed choice '{choiceId}'.");
    }

    private ActionExecutionResult ExecuteSelectEventOption(SemanticActionRequest request)
    {
        var eventOptionId = ResolveRequestValue(request, "eventOptionId");
        if (string.IsNullOrWhiteSpace(eventOptionId))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "select-event-option requires a visible event option id from the current event room.",
                details: [new ActionFailureDetail("event_option_id", string.Empty, "Provide availableActions[].arguments.values.eventOptionId.")]);
        }

        return ExecuteEventRoomIntent(request, eventOptionId, "select-event-option");
    }

    private ActionExecutionResult ExecuteOpenEventShop(SemanticActionRequest request)
    {
        var eventOptionId = ResolveRequestValue(request, "eventOptionId") ?? Sts2EventRoomIds.FakeMerchantOpenShopChoiceId();
        return ExecuteEventRoomIntent(request, eventOptionId, "open-event-shop");
    }

    private ActionExecutionResult ExecuteProceedEvent(SemanticActionRequest request)
    {
        if (Sts2SupportedScreenIds.IsCrystalSphereScreenType(_screenLocator.Locate().ScreenType))
        {
            return ExecuteCrystalSphereIntent(request, Sts2CrystalSphereIds.ProceedChoiceId(), "proceed-event");
        }

        return ExecuteEventRoomIntent(request, Sts2EventRoomIds.ProceedChoiceId(), "proceed-event", allowAnyProceedOption: true);
    }

    private ActionExecutionResult ExecuteEventRoomIntent(
        SemanticActionRequest request,
        string requestedId,
        string expectedAction,
        bool allowAnyProceedOption = false)
    {
        if (!TryResolveEventRoomContext(out var eventRoomContext))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.WrongScreen,
                message: $"{expectedAction} is only available in the live event room.",
                details: [new ActionFailureDetail("screen", _screenLocator.Locate().ScreenType, "Retry when state.screen.id is event-room.")]);
        }

        var requestedPlayerId = request.Perspective?.PlayerId ?? ResolveRequestValue(request, "playerId");
        // A synthetic host-local seat (browser player) owns its OWN per-player event
        // state; route it to the seat path instead of failing WrongPlayer like a real
        // remote peer (who acts through their own client).
        var isHostLocalSeatRequest = !string.IsNullOrWhiteSpace(requestedPlayerId)
            && !string.Equals(requestedPlayerId, eventRoomContext.LocalPlayerId, StringComparison.Ordinal)
            && IsHostLocalPlayerId(requestedPlayerId);
        if (BuildOwnershipFailure(
                request,
                new ActionOwnershipContext(
                    RequestedPlayerId: requestedPlayerId,
                    ResolvedOwnerPlayerId: isHostLocalSeatRequest ? requestedPlayerId : eventRoomContext.LocalPlayerId,
                    LocalPlayerId: eventRoomContext.LocalPlayerId,
                    HostPlayerId: ResolveRunHostPlayerId(eventRoomContext.LocalPlayerId),
                    HostLocalPlayerIds: isHostLocalSeatRequest ? [requestedPlayerId!] : [],
                    Screen: eventRoomContext.Screen.ScreenType,
                    Action: expectedAction),
                out var ownershipFailure))
        {
            return ownershipFailure;
        }

        if (isHostLocalSeatRequest)
        {
            return ExecuteHostLocalSeatEventRoomIntent(request, requestedPlayerId!, requestedId, expectedAction, allowAnyProceedOption);
        }

        if (!string.IsNullOrWhiteSpace(requestedPlayerId)
            && !string.IsNullOrWhiteSpace(eventRoomContext.LocalPlayerId)
            && !string.Equals(requestedPlayerId, eventRoomContext.LocalPlayerId, StringComparison.Ordinal))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.WrongPlayer,
                message: $"{expectedAction} is only available for the local player on the active event room.",
                details: [new ActionFailureDetail("player_id", requestedPlayerId, $"localPlayerId={eventRoomContext.LocalPlayerId}.")]);
        }

        var choice = allowAnyProceedOption
            ? eventRoomContext.ChoicesById.Values.FirstOrDefault(candidate => string.Equals(candidate.PreferredAction, expectedAction, StringComparison.Ordinal))
            : eventRoomContext.ChoicesById.GetValueOrDefault(requestedId);
        if (choice is null)
        {
            if (string.Equals(expectedAction, "proceed-event", StringComparison.Ordinal))
            {
                // A finished event (e.g. Neow's DONE page) exposes no proceed CHOICE, but proceeding it
                // simply opens the map. Call the canonical proceed directly so the run can advance.
                TaskHelper.RunSafely(NEventRoom.Proceed());
                return ActionExecutionResult.Success(
                    actionInstanceId: $"action:proceed-event:{request.RequestId}",
                    kind: request.Kind,
                    message: "Proceeded past the finished event and opened the map.");
            }

            // The native layout exposed no matching choice — in headless this is the norm for
            // ancient events (the guard neuters the native ancient layout). Resolve the local
            // player's select-event-option against the per-player event MODEL instead.
            if (string.Equals(expectedAction, "select-event-option", StringComparison.Ordinal)
                && Sts2EventRoomIds.TryParseChoiceId(requestedId, out _, out _))
            {
                var modelResult = TryExecuteLocalEventOptionViaModel(request, requestedId, expectedAction);
                if (modelResult is not null)
                {
                    return modelResult;
                }
            }

            var looksLikeModeledId = expectedAction switch
            {
                "open-event-shop" => Sts2EventRoomIds.IsFakeMerchantOpenShopChoiceId(requestedId),
                "select-event-option" => Sts2EventRoomIds.TryParseChoiceId(requestedId, out _, out _),
                "proceed-event" => Sts2EventRoomIds.IsProceedChoiceId(requestedId),
                _ => false,
            };
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: looksLikeModeledId ? ActionFailureCode.NotVisible : ActionFailureCode.StaleId,
                message: "The requested event-room control is not visible on the active event room.",
                details: [new ActionFailureDetail("event_option_id", requestedId, "Use a currently visible id from state.eventRoom.options or availableActions.")]);
        }

        if (!string.Equals(choice.PreferredAction, expectedAction, StringComparison.Ordinal))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.StaleId,
                message: "The requested event-room id does not match this action kind.",
                details: [new ActionFailureDetail("event_option_id", requestedId, $"Use {expectedAction} with a matching visible event-room id.")]);
        }

        if (!choice.IsExecutable)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.NotEnabled,
                message: "The requested event-room control is visible but not enabled.",
                details: [new ActionFailureDetail("event_option_id", choice.Snapshot.Id, "Retry when the control appears in availableActions.")]);
        }

        if (!TryExecuteEventRoomChoice(eventRoomContext, choice))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not execute the requested event-room intent through the active live hooks.",
                details: [new ActionFailureDetail("event_option_id", choice.Snapshot.Id, "The active event room did not expose the required hook for this control.", CheckedHookPaths: choice.Snapshot.CheckedHookPaths)]);
        }

        return ActionExecutionResult.Success(
            actionInstanceId: $"action:{expectedAction}:{request.RequestId}",
            kind: request.Kind,
            message: $"Executed {expectedAction}.");
    }

    // A synthetic host-local seat (browser player) has no NEventRoom UI of its own —
    // the host's room object renders the HOST's per-player event, so the normal hook
    // path would act on the wrong player's state. Non-shared events are per-player
    // (each player owns an EventModel with its own options), and a real remote peer's
    // pick reaches the host as an OptionIndexChosenMessage that
    // EventSynchronizer.HandleEventOptionChosenMessage(message, senderId) resolves
    // against THAT player's EventModel.CurrentOptions. Mirror exactly that for the
    // peerless seat: resolve the option index on the seat's own event model and feed
    // the same message through the same handler.
    private ActionExecutionResult ExecuteHostLocalSeatEventRoomIntent(
        SemanticActionRequest request,
        string seatPlayerId,
        string requestedId,
        string expectedAction,
        bool allowAnyProceedOption)
    {
        if (allowAnyProceedOption || string.Equals(expectedAction, "proceed-event", StringComparison.Ordinal))
        {
            // Solo browser play (singleplayer / the browser is the only player): advance the run by
            // opening the map exactly as the host's local proceed does. NEventRoom.Proceed() closes the
            // finished event and shows the map; it spans frames, so run it as a background task.
            TaskHelper.RunSafely(NEventRoom.Proceed());
            return ActionExecutionResult.Success(
                actionInstanceId: $"action:{expectedAction}:{request.RequestId}",
                kind: request.Kind,
                message: "Proceeded past the event and opened the map.");
        }

        var manager = RunManager.Instance;
        var synchronizer = manager?.EventSynchronizer;
        var runState = manager?.DebugOnlyGetState();
        var player = ulong.TryParse(seatPlayerId.AsSpan(2), out var seatNetId)
            ? runState?.GetPlayer(seatNetId)
            : null;
        if (manager is null || synchronizer is null || player is null)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "The live run did not expose the event synchronizer or the seat's run player.",
                details: [new ActionFailureDetail("player_id", seatPlayerId, "Retry when the run and its event room are active.")]);
        }

        EventModel playerEvent;
        bool isShared;
        try
        {
            isShared = synchronizer.IsShared;
            playerEvent = synchronizer.GetEventForPlayer(player);
        }
        catch (Exception ex)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.WrongScreen,
                message: $"No per-player event is in progress for this seat: {ex.Message}",
                details: [new ActionFailureDetail("player_id", seatPlayerId, "Retry while the event room is active.")]);
        }

        if (isShared)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.NotEnabled,
                message: "Shared events resolve through the host's voting flow; host-local seat choices are not supported yet.",
                details: [new ActionFailureDetail("player_id", seatPlayerId, "Vote on the host screen for shared events.")]);
        }

        if (playerEvent.IsFinished)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.NotEnabled,
                message: "The seat's event is already finished.",
                details: [new ActionFailureDetail("event_option_id", requestedId, "The event no longer accepts option choices for this seat.")]);
        }

        // Enumerate the seat's options with the same id scheme the state provider uses
        // for run.currentRoom.event.playerStates[].options[].id, so the browser's id
        // matches exactly. Fall back to the index embedded in the id (its last segment)
        // if localization drift keeps the recomputed slug from matching.
        var options = playerEvent.CurrentOptions;
        var matchedIndex = -1;
        for (var index = 0; index < options.Count; index++)
        {
            var optionId = Sts2EventRoomIds.ResolveOptionId(
                options[index].TextKey,
                options[index].IsProceed,
                ResolveHostLocalSeatOptionLabel(options[index]),
                index,
                out _);
            if (string.Equals(Sts2EventRoomIds.ChoiceId(optionId, index), requestedId, StringComparison.Ordinal))
            {
                matchedIndex = index;
                break;
            }
        }

        if (matchedIndex < 0
            && Sts2EventRoomIds.TryParseChoiceId(requestedId, out _, out var parsedIndex)
            && parsedIndex >= 0
            && parsedIndex < options.Count)
        {
            matchedIndex = parsedIndex;
        }

        if (matchedIndex < 0)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: Sts2EventRoomIds.TryParseChoiceId(requestedId, out _, out _) ? ActionFailureCode.NotVisible : ActionFailureCode.StaleId,
                message: "The requested event option is not part of this seat's current event options.",
                details: [new ActionFailureDetail("event_option_id", requestedId, "Use a currently visible id from run.currentRoom.event.playerStates[].options for this seat.")]);
        }

        var option = options[matchedIndex];
        if (option.IsLocked || option.WasChosen)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.NotEnabled,
                message: option.IsLocked
                    ? "The requested event option is locked for this seat."
                    : "The requested event option was already chosen for this seat.",
                details: [new ActionFailureDetail("event_option_id", requestedId, "Retry with an enabled, unchosen option.")]);
        }

        var message = default(MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync.OptionIndexChosenMessage);
        message.type = MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync.OptionIndexType.Event;
        message.optionIndex = (uint)matchedIndex;
        message.location = manager.RunLocationTargetedBuffer.CurrentLocation;
        try
        {
            if (!Sts2LiveIntrospection.TryInvokeMethod(synchronizer, "HandleEventOptionChosenMessage", message, seatNetId))
            {
                return ActionExecutionResult.Failure(
                    kind: request.Kind,
                    code: ActionFailureCode.MissingHook,
                    message: "EventSynchronizer.HandleEventOptionChosenMessage was not invokable for the host-local seat.",
                    details: [new ActionFailureDetail("event_option_id", requestedId, "The live event synchronizer no longer matches the expected shape.")]);
            }
        }
        catch (Exception ex)
        {
            var reason = (ex as System.Reflection.TargetInvocationException)?.InnerException?.Message ?? ex.Message;
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: $"Choosing the event option for the host-local seat failed: {reason}",
                details: [new ActionFailureDetail("event_option_id", requestedId, "The event synchronizer rejected the choice.")]);
        }

        _logStream.Write(
            BridgeLogLevel.Info,
            "bridge.action",
            $"Chose event option '{requestedId}' (index {matchedIndex}) for host-local seat {seatPlayerId}.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:{expectedAction}:{request.RequestId}",
            kind: request.Kind,
            message: $"Executed {expectedAction} for {seatPlayerId}.");
    }

    // The native event layout can be ABSENT in headless: the ancient-event guard
    // (SPIRECTL_BRIDGE_ASSET_LOAD_GUARD) replaces the crash-prone native ancient layout
    // with a trivial Control that exposes no OptionButtons, so the layout inspector
    // resolves zero choices and ExecuteEventRoomIntent fails with "not visible". The
    // browser still renders options from the per-player event MODEL
    // (state.run.currentRoom.event.playerStates[].options). Resolve the LOCAL player's
    // pick against that same model via the game's own local path
    // (EventSynchronizer.ChooseLocalOption == ChooseOptionForEvent(LocalPlayer,index) +
    // the sync broadcast), so select-event-option works whether or not the native layout
    // exists. Returns null when the model path can't be engaged (no live run / shared
    // event / id not in the model) so the caller emits its standard failure.
    private ActionExecutionResult? TryExecuteLocalEventOptionViaModel(
        SemanticActionRequest request,
        string requestedId,
        string expectedAction)
    {
        var manager = RunManager.Instance;
        var synchronizer = manager?.EventSynchronizer;
        if (manager is null || synchronizer is null)
        {
            return null;
        }

        EventModel localEvent;
        try
        {
            if (synchronizer.IsShared)
            {
                // Shared events resolve through the host's voting flow, not a local model pick.
                return null;
            }

            localEvent = synchronizer.GetLocalEvent();
        }
        catch
        {
            return null;
        }

        if (localEvent is null || localEvent.IsFinished)
        {
            return null;
        }

        // Match the requested id with the SAME scheme the state provider uses for
        // run.currentRoom.event.playerStates[].options[].id; fall back to the index
        // embedded in the id when localization drift keeps the slug from matching.
        var options = localEvent.CurrentOptions;
        var matchedIndex = -1;
        for (var index = 0; index < options.Count; index++)
        {
            var optionId = Sts2EventRoomIds.ResolveOptionId(
                options[index].TextKey,
                options[index].IsProceed,
                ResolveHostLocalSeatOptionLabel(options[index]),
                index,
                out _);
            if (string.Equals(Sts2EventRoomIds.ChoiceId(optionId, index), requestedId, StringComparison.Ordinal))
            {
                matchedIndex = index;
                break;
            }
        }

        if (matchedIndex < 0
            && Sts2EventRoomIds.TryParseChoiceId(requestedId, out _, out var parsedIndex)
            && parsedIndex >= 0
            && parsedIndex < options.Count)
        {
            matchedIndex = parsedIndex;
        }

        if (matchedIndex < 0)
        {
            return null;
        }

        var option = options[matchedIndex];
        if (option.IsLocked || option.WasChosen)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.NotEnabled,
                message: option.IsLocked
                    ? "The requested event option is locked."
                    : "The requested event option was already chosen.",
                details: [new ActionFailureDetail("event_option_id", requestedId, "Retry with an enabled, unchosen option.")]);
        }

        try
        {
            synchronizer.ChooseLocalOption(matchedIndex);
        }
        catch (Exception ex)
        {
            var reason = (ex as System.Reflection.TargetInvocationException)?.InnerException?.Message ?? ex.Message;
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: $"Choosing the local event option failed: {reason}",
                details: [new ActionFailureDetail("event_option_id", requestedId, "The event synchronizer rejected the local choice.")]);
        }

        _logStream.Write(
            BridgeLogLevel.Info,
            "bridge.action",
            $"Chose local event option '{requestedId}' (index {matchedIndex}) via the event model (native layout absent).");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:{expectedAction}:{request.RequestId}",
            kind: request.Kind,
            message: $"Executed {expectedAction} via the event model.");
    }

    // Mirrors Sts2StateProvider.ResolveEventOptionLabel: the option-id slug prefers
    // the stable TextKey, then falls back to localized option text. LocString
    // formatting can throw for missing tables; treat that as "no label".
    private static string? ResolveHostLocalSeatOptionLabel(MegaCrit.Sts2.Core.Events.EventOption option)
    {
        foreach (var text in new object?[] { option.Title, option.Description, option.HistoryName })
        {
            try
            {
                var formatted = Sts2LiveIntrospection.InvokeMethod(text, "GetFormattedText")?.ToString()
                    ?? Sts2LiveIntrospection.InvokeMethod(text, "GetRawText")?.ToString();
                if (!string.IsNullOrWhiteSpace(formatted))
                {
                    return formatted;
                }
            }
            catch
            {
                // Localization failure — fall through to the next candidate.
            }
        }

        return null;
    }

    private ActionExecutionResult ExecuteCrystalSphereChoice(SemanticActionRequest request, string choiceId)
    {
        if (!TryResolveCrystalSphereContext(out var crystalSphereContext))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose is only available on the live Crystal Sphere screen for visible tool, cell, or proceed choices.",
                "choice_id",
                choiceId,
                "Retry when state.screen.id is crystal-sphere and availableActions includes choose.");
        }

        if (!crystalSphereContext.ChoicesById.TryGetValue(choiceId, out var choice))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose requires a visible Crystal Sphere choice id from the current screen.",
                "choice_id",
                choiceId,
                "Provide a stable Crystal Sphere choice id from state.choices[].id or availableActions[].arguments.choiceId.");
        }

        var requestedPlayerId = request.Perspective?.PlayerId;
        if (!string.IsNullOrWhiteSpace(requestedPlayerId)
            && !string.IsNullOrWhiteSpace(crystalSphereContext.LocalPlayerId)
            && !string.Equals(requestedPlayerId, crystalSphereContext.LocalPlayerId, StringComparison.Ordinal))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose is only available for the local player on the active Crystal Sphere screen.",
                "player_id",
                requestedPlayerId,
                $"localPlayerId={crystalSphereContext.LocalPlayerId}.");
        }

        if (!choice.IsExecutable)
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose requires a currently executable Crystal Sphere choice.",
                "choice_id",
                choiceId,
                "Retry with a currently executable Crystal Sphere choice from availableActions[].arguments.choiceId.");
        }

        if (!TryExecuteCrystalSphereChoice(crystalSphereContext.ScreenObject, choice))
        {
            _logStream.Write(
                BridgeLogLevel.Error,
                "bridge.action",
                $"No callable Crystal Sphere hook was found for choice '{choiceId}'.");
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not execute the requested Crystal Sphere choice through the active live hooks.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "choice_id",
                        Value: choiceId,
                        Note: choice.Kind switch
                        {
                            CrystalSphereChoiceKind.BigTool => "The active Crystal Sphere screen did not expose a recognized Big Divination callback.",
                            CrystalSphereChoiceKind.SmallTool => "The active Crystal Sphere screen did not expose a recognized Small Divination callback.",
                            CrystalSphereChoiceKind.Cell => "The active Crystal Sphere screen did not expose a recognized cell callback.",
                            _ => "The active Crystal Sphere screen did not expose a recognized proceed callback.",
                        }),
                ]);
        }

        _logStream.Write(
            BridgeLogLevel.Info,
            "bridge.action",
            choice.Kind switch
            {
                CrystalSphereChoiceKind.BigTool => "Selected Crystal Sphere Big Divination.",
                CrystalSphereChoiceKind.SmallTool => "Selected Crystal Sphere Small Divination.",
                CrystalSphereChoiceKind.Cell => $"Selected Crystal Sphere cell {choice.X},{choice.Y}.",
                _ => "Proceeded from Crystal Sphere.",
            });
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:choose:{request.RequestId}",
            kind: request.Kind,
            message: $"Executed choice '{choiceId}'.");
    }

    private ActionExecutionResult ExecuteTreasureRoomChoice(SemanticActionRequest request, string choiceId)
    {
        if (!TryResolveTreasureRoomContext(out var treasureRoomContext))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose is only available in the live treasure room for visible chest, relic, or proceed choices.",
                "choice_id",
                choiceId,
                "Retry when state.screen.id is treasure-room and availableActions includes choose.");
        }

        if (!treasureRoomContext.ChoicesById.TryGetValue(choiceId, out var choice))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose requires a visible treasure-room choice id from the current screen.",
                "choice_id",
                choiceId,
                "Provide a stable treasure-room choice id from state.choices[].id or availableActions[].arguments.choiceId.");
        }

        var requestedPlayerId = request.Perspective?.PlayerId;
        if (!string.IsNullOrWhiteSpace(requestedPlayerId)
            && !string.IsNullOrWhiteSpace(treasureRoomContext.LocalPlayerId)
            && !string.Equals(requestedPlayerId, treasureRoomContext.LocalPlayerId, StringComparison.Ordinal))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose is only available for the local player on the active treasure-room screen.",
                "player_id",
                requestedPlayerId,
                $"localPlayerId={treasureRoomContext.LocalPlayerId}.");
        }

        if (!choice.IsExecutable)
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose requires a currently executable treasure-room choice.",
                "choice_id",
                choiceId,
                "Retry with a currently executable treasure-room choice from availableActions[].arguments.choiceId.");
        }

        var executed = choice.Kind switch
        {
            TreasureRoomChoiceKind.OpenChest => treasureRoomContext.RoomObject is not null
                && (Sts2LiveIntrospection.TryInvokeMethod(treasureRoomContext.RoomObject, "OnChestButtonReleased", choice.Control)
                    || Sts2LiveIntrospection.TryInvokeParameterlessMethod(choice.Control, "OnRelease")),
            TreasureRoomChoiceKind.Relic => treasureRoomContext.RelicCollection is not null
                && (Sts2LiveIntrospection.TryInvokeMethod(treasureRoomContext.RelicCollection, "PickRelic", choice.Control)
                    || Sts2LiveIntrospection.TryInvokeParameterlessMethod(choice.Control, "OnRelease")),
            TreasureRoomChoiceKind.Proceed => treasureRoomContext.RoomObject is not null
                && (Sts2LiveIntrospection.TryInvokeMethod(treasureRoomContext.RoomObject, "OnProceedButtonReleased", choice.Control)
                    || Sts2LiveIntrospection.TryInvokeParameterlessMethod(choice.Control, "OnRelease")),
            _ => false,
        };
        if (!executed)
        {
            _logStream.Write(
                BridgeLogLevel.Error,
                "bridge.action",
                $"No callable treasure-room hook was found for choice '{choiceId}'.");
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not execute the requested treasure-room choice through the active live hooks.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "choice_id",
                        Value: choiceId,
                        Note: choice.Kind switch
                        {
                            TreasureRoomChoiceKind.OpenChest => "The active treasure room did not expose a recognized chest-open callback.",
                            TreasureRoomChoiceKind.Proceed => "The active treasure room did not expose a recognized proceed callback.",
                            _ => "The active treasure room did not expose a recognized relic-selection callback.",
                        }),
                ]);
        }

        _logStream.Write(
            BridgeLogLevel.Info,
            "bridge.action",
            choice.Kind switch
            {
                TreasureRoomChoiceKind.OpenChest => "Opened the active treasure-room chest.",
                TreasureRoomChoiceKind.Proceed => "Proceeded from the active treasure room.",
                _ => $"Selected treasure-room relic '{choice.Snapshot.Label}'.",
            });
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:choose:{request.RequestId}",
            kind: request.Kind,
            message: $"Executed choice '{choiceId}'.");
    }

    private ActionExecutionResult ExecuteUseCrystalSphereControl(SemanticActionRequest request)
    {
        var controlId = ResolveRequestValue(request, "controlId");
        if (string.IsNullOrWhiteSpace(controlId))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "use-crystal-sphere-control requires a visible Crystal Sphere control id.",
                details: [new ActionFailureDetail("control_id", string.Empty, "Provide availableActions[].arguments.values.controlId.")]);
        }

        return ExecuteCrystalSphereIntent(request, controlId, "use-crystal-sphere-control", ResolveRequestValue(request, "selectedTool"));
    }

    private ActionExecutionResult ExecuteCrystalSphereIntent(
        SemanticActionRequest request,
        string requestedId,
        string expectedAction,
        string? selectedToolOverride = null)
    {
        if (!TryResolveCrystalSphereContext(out var crystalSphereContext))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.WrongScreen,
                message: $"{expectedAction} is only available on the live Crystal Sphere screen.",
                details: [new ActionFailureDetail("screen", _screenLocator.Locate().ScreenType, "Retry when state.screen.id is crystal-sphere.")]);
        }

        var requestedPlayerId = request.Perspective?.PlayerId ?? ResolveRequestValue(request, "playerId");
        if (BuildOwnershipFailure(
                request,
                new ActionOwnershipContext(
                    RequestedPlayerId: requestedPlayerId,
                    ResolvedOwnerPlayerId: crystalSphereContext.LocalPlayerId,
                    LocalPlayerId: crystalSphereContext.LocalPlayerId,
                    HostPlayerId: ResolveRunHostPlayerId(crystalSphereContext.LocalPlayerId),
                    HostLocalPlayerIds: [],
                    Screen: crystalSphereContext.Screen.ScreenType,
                    Action: expectedAction),
                out var ownershipFailure))
        {
            return ownershipFailure;
        }

        if (!string.IsNullOrWhiteSpace(requestedPlayerId)
            && !string.IsNullOrWhiteSpace(crystalSphereContext.LocalPlayerId)
            && !string.Equals(requestedPlayerId, crystalSphereContext.LocalPlayerId, StringComparison.Ordinal))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.WrongPlayer,
                message: $"{expectedAction} is only available for the local player on the active Crystal Sphere screen.",
                details: [new ActionFailureDetail("player_id", requestedPlayerId, $"localPlayerId={crystalSphereContext.LocalPlayerId}.")]);
        }

        if (!crystalSphereContext.ChoicesById.TryGetValue(requestedId, out var choice))
        {
            var looksLikeModeledId = expectedAction == "proceed-event"
                ? Sts2CrystalSphereIds.IsProceedChoiceId(requestedId)
                : Sts2CrystalSphereIds.IsToolChoiceId(requestedId)
                    || Sts2CrystalSphereIds.TryParseCellChoiceId(requestedId, out _, out _);
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: looksLikeModeledId ? ActionFailureCode.NotVisible : ActionFailureCode.StaleId,
                message: "The requested Crystal Sphere control is not visible on the active screen.",
                details: [new ActionFailureDetail("control_id", requestedId, "Use a currently visible Crystal Sphere id from state.choices or availableActions.")]);
        }

        if (!string.Equals(choice.Snapshot.PreferredAction, expectedAction, StringComparison.Ordinal))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.StaleId,
                message: "The requested Crystal Sphere id does not match this action kind.",
                details: [new ActionFailureDetail("control_id", requestedId, $"Use {expectedAction} with a matching visible Crystal Sphere id.")]);
        }

        if (!choice.IsExecutable)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.NotEnabled,
                message: "The requested Crystal Sphere control is visible but not enabled.",
                details: [new ActionFailureDetail("control_id", requestedId, "Retry when the control appears in availableActions.")]);
        }

        if (!TryNormalizeCrystalSphereToolOverride(
                request,
                selectedToolOverride,
                out var normalizedToolOverride,
                out var toolOverrideFailure))
        {
            return toolOverrideFailure;
        }

        if (choice.Kind == CrystalSphereChoiceKind.Cell
            && normalizedToolOverride is not null
            && !TryApplyCrystalSphereToolOverride(crystalSphereContext.ScreenObject, crystalSphereContext.ChoicesById, normalizedToolOverride, out var checkedHookPaths))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not apply the requested Crystal Sphere tool override before clicking the cell.",
                details: [new ActionFailureDetail("selected_tool", normalizedToolOverride, "The active Crystal Sphere screen did not expose the requested tool hook.", CheckedHookPaths: checkedHookPaths)]);
        }

        if (!TryExecuteCrystalSphereChoice(crystalSphereContext.ScreenObject, choice))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not execute the requested Crystal Sphere intent through the active live hooks.",
                details: [new ActionFailureDetail("control_id", requestedId, "The active Crystal Sphere screen did not expose the required hook for this control.", CheckedHookPaths: choice.Snapshot.CheckedHookPaths)]);
        }

        return ActionExecutionResult.Success(
            actionInstanceId: $"action:{expectedAction}:{request.RequestId}",
            kind: request.Kind,
            message: $"Executed {expectedAction}.");
    }

    private static bool TryNormalizeCrystalSphereToolOverride(
        SemanticActionRequest request,
        string? selectedToolOverride,
        out string? normalizedToolOverride,
        [NotNullWhen(false)] out ActionExecutionResult? failure)
    {
        normalizedToolOverride = null;
        failure = null;

        if (string.IsNullOrWhiteSpace(selectedToolOverride))
        {
            return true;
        }

        normalizedToolOverride = selectedToolOverride.Trim().ToLowerInvariant();
        if (normalizedToolOverride is "big" or "small")
        {
            return true;
        }

        failure = ActionExecutionResult.Failure(
            kind: request.Kind,
            code: ActionFailureCode.InvalidAction,
            message: "use-crystal-sphere-control selectedTool must be 'big' or 'small' when provided.",
            details: [new ActionFailureDetail("selected_tool", selectedToolOverride, "Use selectedTool=big, selectedTool=small, or omit it to use the host-selected tool.")]);
        return false;
    }

    private static bool TryApplyCrystalSphereToolOverride(
        object screenObject,
        IReadOnlyDictionary<string, ResolvedCrystalSphereChoice> choicesById,
        string selectedTool,
        out IReadOnlyList<string> checkedHookPaths)
    {
        var toolChoiceId = selectedTool == "small"
            ? Sts2CrystalSphereIds.SmallToolChoiceId()
            : Sts2CrystalSphereIds.BigToolChoiceId();
        checkedHookPaths = selectedTool == "small"
            ? ["NCrystalSphereScreen.SetSmallDivination(smallButton)"]
            : ["NCrystalSphereScreen.SetBigDivination(bigButton)"];

        if (!choicesById.TryGetValue(toolChoiceId, out var toolChoice)
            || !toolChoice.IsExecutable)
        {
            return false;
        }

        checkedHookPaths = toolChoice.Snapshot.CheckedHookPaths;
        return TryExecuteCrystalSphereChoice(screenObject, toolChoice);
    }

    private ActionExecutionResult ExecuteOpenChest(SemanticActionRequest request)
        => ExecuteTreasureRoomIntent(request, TreasureRoomChoiceKind.OpenChest, Sts2TreasureRoomIds.OpenChestChoiceId(), "open-chest");

    private ActionExecutionResult ExecuteTakeRelic(SemanticActionRequest request)
    {
        var relicId = ResolveRequestValue(request, "relicId");
        if (string.IsNullOrWhiteSpace(relicId))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "take-relic requires a visible relic id from the current treasure room or relic-selection overlay.",
                details: [new ActionFailureDetail("relic_id", string.Empty, "Provide state.treasureRoom.relics[].id, state.relicSelection.relics[].id, or availableActions[].arguments.values.relicId.")]);
        }

        var result = ExecuteTreasureRoomIntent(request, TreasureRoomChoiceKind.Relic, relicId, "take-relic");
        if (result.Accepted)
        {
            return result;
        }

        // Headless/browser fallback: the live relic holder is frequently not reported visible/populated to
        // the choice inspector (the in-engine UI click that normally finishes the chest never happens),
        // so no per-relic choice matches and the standard path above can't find the control. A
        // singleplayer chest has exactly one relic in the SingleplayerRelicHolder, so pick it directly via
        // the same PickRelic(holder) hook the debug treasure resolver uses. This unblocks the chest
        // (canProceed flips) so proceed-treasure-room can actually leave the room. Multiplayer relic CHOICE
        // (multiple holders) still flows through the per-relic choices above.
        if (TryResolveTreasureRoomContext(out var treasureRoomContext)
            && treasureRoomContext.RelicCollection is not null)
        {
            var holder = Sts2LiveIntrospection.GetMemberValue(treasureRoomContext.RelicCollection, "SingleplayerRelicHolder");
            if (holder is not null
                && Sts2LiveIntrospection.TryInvokeMethod(treasureRoomContext.RelicCollection, "PickRelic", holder))
            {
                return ActionExecutionResult.Success(
                    actionInstanceId: $"action:take-relic:{request.RequestId}",
                    kind: request.Kind,
                    message: "Executed take-relic (singleplayer relic holder).");
            }
        }

        return result;
    }

    private ActionExecutionResult ExecuteProceedTreasureRoom(SemanticActionRequest request)
        => ExecuteTreasureRoomIntent(request, TreasureRoomChoiceKind.Proceed, Sts2TreasureRoomIds.ProceedChoiceId(), "proceed-treasure-room");

    private ActionExecutionResult ExecuteTreasureRoomIntent(
        SemanticActionRequest request,
        TreasureRoomChoiceKind expectedKind,
        string requestedId,
        string actionName)
    {
        if (!TryResolveTreasureRoomContext(out var treasureRoomContext))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.WrongScreen,
                message: $"{actionName} is only available in the live treasure room or relic-selection overlay.",
                details: [new ActionFailureDetail("screen", _screenLocator.Locate().ScreenType, "Retry when state.screen.id is treasure-room or relic-selection.")]);
        }

        var requestedPlayerId = request.Perspective?.PlayerId ?? ResolveRequestValue(request, "playerId");
        if (BuildOwnershipFailure(
                request,
                new ActionOwnershipContext(requestedPlayerId, treasureRoomContext.LocalPlayerId, treasureRoomContext.LocalPlayerId, ResolveRunHostPlayerId(treasureRoomContext.LocalPlayerId), [], treasureRoomContext.Screen.ScreenType, actionName),
                out var ownershipFailure))
        {
            return ownershipFailure;
        }

        if (!string.IsNullOrWhiteSpace(requestedPlayerId)
            && !string.IsNullOrWhiteSpace(treasureRoomContext.LocalPlayerId)
            && !string.Equals(requestedPlayerId, treasureRoomContext.LocalPlayerId, StringComparison.Ordinal))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.WrongPlayer,
                message: $"{actionName} is only available for the local player on the active treasure-room screen.",
                details: [new ActionFailureDetail("player_id", requestedPlayerId, $"localPlayerId={treasureRoomContext.LocalPlayerId}.")]);
        }

        var choice = expectedKind == TreasureRoomChoiceKind.Relic
            ? treasureRoomContext.ChoicesById.Values.FirstOrDefault(candidate =>
                candidate.Kind == TreasureRoomChoiceKind.Relic
                && (string.Equals(candidate.RelicId, requestedId, StringComparison.Ordinal)
                    || string.Equals(candidate.Snapshot.Id, requestedId, StringComparison.Ordinal)))
            : treasureRoomContext.ChoicesById.GetValueOrDefault(requestedId);
        if (choice is null)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.NotVisible,
                message: $"{actionName} requires the matching treasure-room control to be currently visible.",
                details: [new ActionFailureDetail(expectedKind == TreasureRoomChoiceKind.Relic ? "relic_id" : "choice_id", requestedId, "Re-read state and retry with a visible treasure-room control.")]);
        }

        if (choice.Kind != expectedKind)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.StaleId,
                message: "The requested treasure-room id does not match this action kind.",
                details: [new ActionFailureDetail("choice_id", choice.Snapshot.Id, $"Use {actionName} with a matching visible treasure-room id.")]);
        }

        if (!choice.IsExecutable)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.NotEnabled,
                message: "The requested treasure-room control is visible but not enabled.",
                details: [new ActionFailureDetail("choice_id", choice.Snapshot.Id, "Retry when the control appears in availableActions.")]);
        }

        var executed = ExecuteTreasureRoomChoiceHook(treasureRoomContext, choice);
        if (!executed)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not execute the requested treasure-room intent through the active live hooks.",
                details: [new ActionFailureDetail("choice_id", choice.Snapshot.Id, "The active treasure room did not expose the required hook for this control.")]);
        }

        return ActionExecutionResult.Success(
            actionInstanceId: $"action:{actionName}:{request.RequestId}",
            kind: request.Kind,
            message: $"Executed {actionName}.");
    }

    private static bool ExecuteTreasureRoomChoiceHook(
        TreasureRoomActionContext treasureRoomContext,
        ResolvedTreasureRoomChoice choice)
        => choice.Kind switch
        {
            TreasureRoomChoiceKind.OpenChest => treasureRoomContext.RoomObject is not null
                && (Sts2LiveIntrospection.TryInvokeMethod(treasureRoomContext.RoomObject, "OnChestButtonReleased", choice.Control)
                    || Sts2LiveIntrospection.TryInvokeParameterlessMethod(choice.Control, "OnRelease")),
            TreasureRoomChoiceKind.Relic => treasureRoomContext.RelicCollection is not null
                && (Sts2LiveIntrospection.TryInvokeMethod(treasureRoomContext.RelicCollection, "PickRelic", choice.Control)
                    || Sts2LiveIntrospection.TryInvokeParameterlessMethod(choice.Control, "OnRelease")),
            TreasureRoomChoiceKind.Proceed => treasureRoomContext.RoomObject is not null
                && (Sts2LiveIntrospection.TryInvokeMethod(treasureRoomContext.RoomObject, "OnProceedButtonReleased", choice.Control)
                    || Sts2LiveIntrospection.TryInvokeParameterlessMethod(choice.Control, "OnRelease")),
            _ => false,
        };

    private ActionExecutionResult ExecuteRestSiteChoice(SemanticActionRequest request, string choiceId)
    {
        if (!TryResolveRestSiteContext(out var restSiteContext))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose is only available in the live rest-site room for visible options or the enabled proceed button.",
                "choice_id",
                choiceId,
                "Retry when state.screen.id is rest-site and availableActions includes choose.");
        }

        if (!restSiteContext.ChoicesById.TryGetValue(choiceId, out var choice))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose requires a visible rest-site choice id from the current room.",
                "choice_id",
                choiceId,
                "Provide a stable rest-site choice id from state.choices[].id or availableActions[].arguments.choiceId.");
        }

        var requestedPlayerId = request.Perspective?.PlayerId;
        if (!string.IsNullOrWhiteSpace(requestedPlayerId)
            && !string.IsNullOrWhiteSpace(choice.PlayerId)
            && !string.Equals(requestedPlayerId, choice.PlayerId, StringComparison.Ordinal))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose is only available for the requested rest-site owner in the active room.",
                "player_id",
                requestedPlayerId,
                $"choiceOwnerPlayerId={choice.PlayerId}.");
        }

        if (!choice.IsExecutable)
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose requires a currently executable rest-site choice.",
                "choice_id",
                choiceId,
                "Retry with a currently executable rest-site choice from availableActions[].arguments.choiceId.");
        }

        var executed = choice.IsFlowChoice
            ? Sts2LiveIntrospection.TryInvokeMethod(restSiteContext.RoomObject, "OnProceedButtonReleased", choice.Control)
            : Sts2LiveIntrospection.TryInvokeParameterlessMethod(choice.Control, "OnRelease");
        if (!executed)
        {
            _logStream.Write(
                BridgeLogLevel.Error,
                "bridge.action",
                $"No callable rest-site hook was found for choice '{choiceId}'.");
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not execute the requested rest-site choice through the active live hooks.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "choice_id",
                        Value: choiceId,
                        Note: choice.IsFlowChoice
                            ? "The active rest-site room did not expose a recognized proceed callback."
                            : "The active rest-site room did not expose a recognized option callback."),
                ]);
        }

        _logStream.Write(
            BridgeLogLevel.Info,
            "bridge.action",
            choice.IsFlowChoice
                ? $"Executed rest-site flow '{choice.Snapshot.Label}'."
                : $"Selected rest-site option '{choice.Snapshot.Label}'.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:choose:{request.RequestId}",
            kind: request.Kind,
            message: $"Executed choice '{choiceId}'.");
    }

    private ActionExecutionResult ExecuteRest(SemanticActionRequest request)
        => ExecuteRestSiteIntent(request, "rest", "rest");

    private ActionExecutionResult ExecuteSmith(SemanticActionRequest request)
        => ExecuteRestSiteIntent(request, "smith", "smith");

    private ActionExecutionResult ExecuteUseRestSiteOption(SemanticActionRequest request)
    {
        var optionId = ResolveRequestValue(request, "restOptionId");
        if (string.IsNullOrWhiteSpace(optionId))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "use-rest-site-option requires a visible rest option id from the current rest site.",
                details: [new ActionFailureDetail("rest_option_id", string.Empty, "Provide availableActions[].arguments.values.restOptionId.")]);
        }

        return ExecuteRestSiteIntent(request, "use-rest-site-option", optionId);
    }

    private ActionExecutionResult ExecuteProceedRestSite(SemanticActionRequest request)
    {
        var result = ExecuteRestSiteIntent(request, "proceed-rest-site", Sts2RestSiteIds.ProceedChoiceId());
        if (result.Accepted)
        {
            return result;
        }

        // Headless/browser fallback: the proceed choice frequently isn't resolved cleanly (the proceed
        // button's visible/enabled flags and choice id are unreliable headless), so release the proceed
        // button directly when the standard path can't. This finishes the rest-site room so the host opens
        // the travel-enabled map, mirroring the rest-site option fallback.
        if (TryResolveRestSiteContext(out var restSiteContext)
            && TryReleaseRestSiteControlFallback(restSiteContext.RoomObject, "proceed-rest-site", Sts2RestSiteIds.ProceedChoiceId()))
        {
            return ActionExecutionResult.Success(
                actionInstanceId: $"action:proceed-rest-site:{request.RequestId}",
                kind: request.Kind,
                message: "Executed proceed-rest-site (rest-site fallback).");
        }

        return result;
    }

    private ActionExecutionResult ExecuteRestSiteIntent(
        SemanticActionRequest request,
        string expectedAction,
        string requestedId)
    {
        if (!TryResolveRestSiteContext(out var restSiteContext))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.WrongScreen,
                message: $"{expectedAction} is only available in the live rest-site room.",
                details: [new ActionFailureDetail("screen", _screenLocator.Locate().ScreenType, "Retry when state.screen.id is rest-site.")]);
        }

        var choice = expectedAction == "proceed-rest-site"
            ? restSiteContext.ChoicesById.GetValueOrDefault(requestedId)
            : restSiteContext.ChoicesById.Values.FirstOrDefault(candidate =>
                !candidate.IsFlowChoice
                && string.Equals(candidate.PreferredAction, expectedAction, StringComparison.Ordinal)
                && (string.Equals(candidate.RestOptionId, requestedId, StringComparison.Ordinal)
                    || string.Equals(candidate.Snapshot.Id, requestedId, StringComparison.Ordinal)
                    || expectedAction is "rest" or "smith"));
        if (choice is null)
        {
            // Headless/browser fallback: the live rest-site buttons frequently don't expose a populated
            // Option to the choice inspector, so no choice matches (same headless gap as the treasure
            // relic holder). Drive the underlying controls directly — release the requested option button
            // (matched by its live OptionId, else the first option button) or the proceed button — the
            // same OnRelease/OnProceedButtonReleased hooks a resolved choice would have used.
            if (TryReleaseRestSiteControlFallback(restSiteContext.RoomObject, expectedAction, requestedId))
            {
                return ActionExecutionResult.Success(
                    actionInstanceId: $"action:{expectedAction}:{request.RequestId}",
                    kind: request.Kind,
                    message: $"Executed {expectedAction} (rest-site fallback).");
            }

            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.NotVisible,
                message: $"{expectedAction} requires the matching rest-site control to be currently visible.",
                details: [new ActionFailureDetail(expectedAction == "proceed-rest-site" ? "choice_id" : "rest_option_id", requestedId, "Re-read state and retry with a visible rest-site control.")]);
        }

        var requestedPlayerId = request.Perspective?.PlayerId ?? ResolveRequestValue(request, "playerId");
        if (BuildOwnershipFailure(
                request,
                new ActionOwnershipContext(requestedPlayerId, choice.PlayerId, restSiteContext.LocalPlayerId, ResolveRunHostPlayerId(restSiteContext.LocalPlayerId), [], restSiteContext.Screen.ScreenType, expectedAction),
                out var ownershipFailure))
        {
            return ownershipFailure;
        }

        if (!string.IsNullOrWhiteSpace(requestedPlayerId)
            && !string.IsNullOrWhiteSpace(choice.PlayerId)
            && !string.Equals(requestedPlayerId, choice.PlayerId, StringComparison.Ordinal))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.WrongPlayer,
                message: $"{expectedAction} is only available for the requested rest-site owner in the active room.",
                details: [new ActionFailureDetail("player_id", requestedPlayerId, $"choiceOwnerPlayerId={choice.PlayerId}.")]);
        }

        if (!string.Equals(choice.PreferredAction, expectedAction, StringComparison.Ordinal))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.StaleId,
                message: "The requested rest-site id does not match this action kind.",
                details: [new ActionFailureDetail("choice_id", choice.Snapshot.Id, $"Use {expectedAction} with a matching visible rest-site id.")]);
        }

        if (!choice.IsExecutable)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.NotEnabled,
                message: "The requested rest-site control is visible but not enabled.",
                details: [new ActionFailureDetail("choice_id", choice.Snapshot.Id, "Retry when the control appears in availableActions.")]);
        }

        var executed = choice.IsFlowChoice
            ? Sts2LiveIntrospection.TryInvokeMethod(restSiteContext.RoomObject, "OnProceedButtonReleased", choice.Control)
            : Sts2LiveIntrospection.TryInvokeParameterlessMethod(choice.Control, "OnRelease");
        if (!executed)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not execute the requested rest-site intent through the active live hooks.",
                details: [new ActionFailureDetail("choice_id", choice.Snapshot.Id, "The active rest-site room did not expose the required hook for this control.")]);
        }

        return ActionExecutionResult.Success(
            actionInstanceId: $"action:{expectedAction}:{request.RequestId}",
            kind: request.Kind,
            message: $"Executed {expectedAction}.");
    }

    private const string RestSiteButtonTypeName = "MegaCrit.Sts2.Core.Nodes.RestSite.NRestSiteButton";

    // Directly release a rest-site control when the standard choice resolution can't see a visible/
    // populated option (headless/browser). For proceed, release the proceed button; for an option,
    // release the button whose live OptionId matches the request, else the first option button. Returns
    // false if no suitable control could be released.
    private static bool TryReleaseRestSiteControlFallback(object roomObject, string expectedAction, string requestedId)
    {
        if (expectedAction == "proceed-rest-site")
        {
            // CRITICAL: enable map travel before opening the map. NRestSiteRoom.ShowProceedButton() does
            // both _proceedButton.Enable() AND NMapScreen.SetTravelEnabled(true); it normally runs when the
            // chosen rest option finishes resolving, but in headless that async resolution (HideChoices ->
            // ShowProceedButton) frequently doesn't fire, so SetTravelEnabled(true) never runs. Releasing
            // the proceed button (OnProceedButtonReleased -> NMapScreen.Open()) then opens a map with travel
            // DISABLED — every node un-votable — and the bot loops on proceed forever. Force ShowProceedButton
            // first so the opened map is travel-enabled.
            Sts2LiveIntrospection.TryInvokeParameterlessMethod(roomObject, "ShowProceedButton");
            var proceed = Sts2LiveIntrospection.GetMemberValue(roomObject, "ProceedButton")
                ?? Sts2LiveIntrospection.GetMemberValue(roomObject, "_proceedButton");
            return proceed is not null
                && (Sts2LiveIntrospection.TryInvokeMethod(roomObject, "OnProceedButtonReleased", proceed)
                    || Sts2LiveIntrospection.TryInvokeParameterlessMethod(proceed, "OnRelease"));
        }

        if (Sts2LiveIntrospection.GetMemberValue(roomObject, "_choicesContainer") is not Node container)
        {
            return false;
        }

        Control? firstButton = null;
        Control? requestedButton = null;
        foreach (var child in container.GetChildren())
        {
            if (child is not Control control || !Sts2LiveIntrospection.IsType(child, RestSiteButtonTypeName))
            {
                continue;
            }

            firstButton ??= control;
            var option = Sts2LiveIntrospection.GetMemberValue(child, "Option");
            var optionId = option is null ? null : Sts2LiveIntrospection.GetMemberValue(option, "OptionId")?.ToString();
            if (!string.IsNullOrWhiteSpace(optionId) && string.Equals(optionId, requestedId, StringComparison.OrdinalIgnoreCase))
            {
                requestedButton = control;
                break;
            }
        }

        var button = requestedButton ?? firstButton;
        return button is not null && Sts2LiveIntrospection.TryInvokeParameterlessMethod(button, "OnRelease");
    }

}
