using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;

public sealed partial class Sts2ActionHandler
{
    // In-hand card selection mode (NPlayerHand SimpleSelect/UpgradeSelect, e.g.
    // Survivor's "Discard 1 card."). select-hand-card mirrors the game's
    // holder press (SelectCardInSimpleMode/SelectCardInUpgradeMode),
    // deselect-hand-card mirrors clicking a staged card
    // (NSelectedHandCardContainer.DeselectCard), and confirm-hand-selection
    // mirrors the confirm button (OnSelectModeConfirmButtonPressed), which
    // resolves the awaiting card effect. There is intentionally no cancel
    // action: the game offers no user-facing cancel (selection only unwinds on
    // hand teardown).
    private ActionExecutionResult ExecuteSelectHandCard(SemanticActionRequest request)
        => ExecuteHandSelectionAction(request, "select-hand-card", (hand, handSelection, context) =>
        {
            var cardId = request.CardId?.Trim() ?? string.Empty;
            if (handSelection.SelectedCardIds.Contains(cardId, StringComparer.Ordinal))
            {
                return CombatActionUnavailable(
                    request.Kind,
                    "select-hand-card requires a hand card that is not already staged.",
                    "card_id",
                    cardId,
                    "The card is already in state.run.view.handSelection.selectedCardIds; use deselect-hand-card to unstage it.");
            }

            var swappedOutCardId = handSelection.SelectedCardIds.Count >= handSelection.MaxSelect
                ? handSelection.SelectedCardIds[^1]
                : null;
            var failure = TryStageHandCard(hand, handSelection, context, request, cardId, out var stagedName);
            if (failure is not null)
            {
                return failure;
            }

            var message = swappedOutCardId is null
                ? $"Staged hand card '{stagedName}' for the active hand selection."
                : $"Staged hand card '{stagedName}', swapping out '{swappedOutCardId}' (selection was at maxSelect).";
            _logStream.Write(BridgeLogLevel.Info, "bridge.action", $"Executed select-hand-card for '{cardId}'.");
            return ActionExecutionResult.Success(
                actionInstanceId: $"action:select-hand-card:{request.RequestId}",
                kind: request.Kind,
                message: message);
        });

    // Stage one hand card into the active selection (NPlayerHand
    // SelectCardInSimpleMode/SelectCardInUpgradeMode), validating the id against
    // the selectable set. Returns null on success (with the resolved card name),
    // or the structured failure to propagate. Shared by select-hand-card and the
    // confirm-hand-selection stage-then-confirm path.
    private ActionExecutionResult? TryStageHandCard(
        NPlayerHand hand,
        StateHandSelectionViewSnapshot handSelection,
        CombatActionContext context,
        SemanticActionRequest request,
        string cardId,
        out string? stagedName)
    {
        stagedName = null;
        if (!handSelection.SelectableCardIds.Contains(cardId, StringComparer.Ordinal))
        {
            return CombatActionUnavailable(
                request.Kind,
                $"{request.Kind} requires a selectable hand card id.",
                "card_id",
                cardId,
                "Provide a card id from state.run.view.handSelection.selectableCardIds.");
        }

        var resolvedCard = ResolveLocalHandCard(context.ActionOwnerPlayer, context.ActionOwnerPlayerId, cardId);
        var holder = resolvedCard is null ? null : hand.GetCardHolder(resolvedCard.Card);
        if (resolvedCard is null || holder is null)
        {
            return CombatActionUnavailable(
                request.Kind,
                $"{request.Kind} could not resolve the requested card in the live hand.",
                "card_id",
                cardId,
                "Use a current id from state.run.view.handSelection.selectableCardIds; ids go stale when the hand changes.");
        }

        var selectMethod = string.Equals(handSelection.Mode, "upgrade-select", StringComparison.Ordinal)
            ? "SelectCardInUpgradeMode"
            : "SelectCardInSimpleMode";
        if (!Sts2LiveIntrospection.TryInvokeMethod(hand, selectMethod, holder))
        {
            return HandSelectionMissingHook(request, $"NPlayerHand.{selectMethod}");
        }

        stagedName = resolvedCard.Name;
        return null;
    }

    private ActionExecutionResult ExecuteDeselectHandCard(SemanticActionRequest request)
        => ExecuteHandSelectionAction(request, "deselect-hand-card", (hand, handSelection, context) =>
        {
            var cardId = request.CardId?.Trim() ?? string.Empty;
            if (string.Equals(handSelection.Mode, "upgrade-select", StringComparison.Ordinal))
            {
                return CombatActionUnavailable(
                    request.Kind,
                    "deselect-hand-card is not available in upgrade-select mode.",
                    "card_id",
                    cardId,
                    "The upgrade-select hand UI swaps the staged card on select-hand-card instead of deselecting.");
            }

            if (!handSelection.SelectedCardIds.Contains(cardId, StringComparer.Ordinal))
            {
                return CombatActionUnavailable(
                    request.Kind,
                    "deselect-hand-card requires a currently staged card id.",
                    "card_id",
                    cardId,
                    "Provide a card id from state.run.view.handSelection.selectedCardIds.");
            }

            var resolvedCard = ResolveLocalHandCard(context.ActionOwnerPlayer, context.ActionOwnerPlayerId, cardId);
            if (resolvedCard is null)
            {
                return CombatActionUnavailable(
                    request.Kind,
                    "deselect-hand-card could not resolve the requested card in the live hand.",
                    "card_id",
                    cardId,
                    "Use a current id from state.run.view.handSelection.selectedCardIds; ids go stale when the hand changes.");
            }

            var container = Sts2LiveIntrospection.GetMemberValue(hand, "_selectedHandCardContainer");
            if (container is null
                || !Sts2LiveIntrospection.TryInvokeMethod(container, "DeselectCard", resolvedCard.Card))
            {
                return HandSelectionMissingHook(request, "NPlayerHand._selectedHandCardContainer.DeselectCard");
            }

            _logStream.Write(BridgeLogLevel.Info, "bridge.action", $"Executed deselect-hand-card for '{cardId}'.");
            return ActionExecutionResult.Success(
                actionInstanceId: $"action:deselect-hand-card:{request.RequestId}",
                kind: request.Kind,
                message: $"Unstaged hand card '{resolvedCard.Name}' from the active hand selection.");
        });

    private ActionExecutionResult ExecuteConfirmHandSelection(SemanticActionRequest request)
        => ExecuteHandSelectionAction(request, "confirm-hand-selection", (hand, handSelection, context) =>
        {
            // Stage any requested card not already staged on the host. This folds
            // staging + confirm into one call for clients that keep the selection
            // purely local (no per-click host round-trip). Empty cardIds preserves
            // the original behavior (confirm whatever is already staged).
            var stagedCount = 0;
            foreach (var rawCardId in request.CardIds ?? Array.Empty<string>())
            {
                var cardId = rawCardId?.Trim() ?? string.Empty;
                if (cardId.Length == 0 || handSelection.SelectedCardIds.Contains(cardId, StringComparer.Ordinal))
                {
                    continue;
                }

                var failure = TryStageHandCard(hand, handSelection, context, request, cardId, out _);
                if (failure is not null)
                {
                    return failure;
                }

                stagedCount++;
            }

            // Re-resolve so CanConfirm / the staged count reflect the freshly staged
            // selection (the snapshot above predates the SelectCardInSimpleMode calls).
            var effectiveSelection = handSelection;
            if (stagedCount > 0)
            {
                var notices = new List<StateNoticeSnapshot>();
                effectiveSelection = Sts2StateProvider.ResolveHandSelectionView(notices, context.LocalPlayerId)
                    ?? handSelection;
            }

            if (!effectiveSelection.CanConfirm)
            {
                return CombatActionUnavailable(
                    request.Kind,
                    "confirm-hand-selection requires the staged selection to satisfy minSelect..maxSelect.",
                    "selected_count",
                    effectiveSelection.SelectedCardIds.Count.ToString(),
                    $"Stage between {effectiveSelection.MinSelect} and {effectiveSelection.MaxSelect} cards first (state.run.view.handSelection.canConfirm), or pass them as confirm-hand-selection cardIds.");
            }

            // The NButton argument is unused by the game handler.
            if (!Sts2LiveIntrospection.TryInvokeMethod(hand, "OnSelectModeConfirmButtonPressed", (object?)null))
            {
                return HandSelectionMissingHook(request, "NPlayerHand.OnSelectModeConfirmButtonPressed");
            }

            _logStream.Write(BridgeLogLevel.Info, "bridge.action", "Executed confirm-hand-selection.");
            return ActionExecutionResult.Success(
                actionInstanceId: $"action:confirm-hand-selection:{request.RequestId}",
                kind: request.Kind,
                message: $"Confirmed the in-hand selection ({effectiveSelection.SelectedCardIds.Count} card(s)).");
        });

    private ActionExecutionResult ExecuteHandSelectionAction(
        SemanticActionRequest request,
        string actionName,
        Func<NPlayerHand, StateHandSelectionViewSnapshot, CombatActionContext, ActionExecutionResult> execute)
    {
        var requestedPlayerId = request.Perspective?.PlayerId ?? ResolveRequestValue(request, "playerId");
        if (!TryResolveCombatContext(requestedPlayerId, out var combatContext))
        {
            return CombatActionUnavailable(
                request.Kind,
                $"{actionName} is only available during combat.",
                "screen",
                string.Empty,
                "Retry when state.screen.id is combat and state.run.view.handSelection is present.");
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
                    actionName),
                out var ownershipFailure))
        {
            return ownershipFailure;
        }

        var hand = NPlayerHand.Instance;
        if (hand is null || !hand.IsInCardSelection)
        {
            return CombatActionUnavailable(
                request.Kind,
                $"{actionName} requires the hand to be in card-selection mode.",
                "hand_selection",
                string.Empty,
                "Retry while state.run.view.handSelection is present (a card effect is asking for hand cards).");
        }

        if (NOverlayStack.Instance?.ScreenCount > 0)
        {
            return CombatActionUnavailable(
                request.Kind,
                $"{actionName} is blocked while an overlay screen is open.",
                "overlay",
                string.Empty,
                "Resolve or close the open overlay first; the hand ignores selection input under an overlay.");
        }

        var notices = new List<StateNoticeSnapshot>();
        var handSelection = Sts2StateProvider.ResolveHandSelectionView(notices, combatContext.LocalPlayerId);
        if (handSelection is null)
        {
            return CombatActionUnavailable(
                request.Kind,
                $"{actionName} requires the hand to be in card-selection mode.",
                "hand_selection",
                string.Empty,
                "Retry while state.run.view.handSelection is present.");
        }

        if (handSelection.IsPeeking)
        {
            return CombatActionUnavailable(
                request.Kind,
                $"{actionName} is blocked while the hand is peeking at the combat state.",
                "is_peeking",
                "true",
                "Selection input is suspended while state.run.view.handSelection.isPeeking is true.");
        }

        return execute(hand, handSelection, combatContext);
    }

    private static ActionExecutionResult HandSelectionMissingHook(SemanticActionRequest request, string hookPath)
        => ActionExecutionResult.Failure(
            kind: request.Kind,
            code: ActionFailureCode.MissingHook,
            message: $"The live hand-selection hook '{hookPath}' was not found; the game build may have changed.",
            details:
            [
                new ActionFailureDetail(
                    Field: "hook",
                    Value: hookPath,
                    Note: "Hand-selection actions execute private NPlayerHand members by reflection; re-validate against the current game build.",
                    ReasonCode: ActionFailureCode.MissingHook),
            ],
            checkedHookPaths: [hookPath]);
}
