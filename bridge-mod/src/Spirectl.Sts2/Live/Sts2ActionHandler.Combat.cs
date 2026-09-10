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
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Potions;
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
    private ActionExecutionResult ExecutePlayCard(SemanticActionRequest request)
    {
        var cardId = request.CardId?.Trim();
        if (string.IsNullOrWhiteSpace(cardId))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "play-card requires a stable card id from the current combat hand.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "card_id",
                        Value: string.Empty,
                        Note: "Provide a stable card id from state.combat.hand[].id or availableActions[].arguments.cardId."),
                ]);
        }

        var requestedPlayerId = request.Perspective?.PlayerId;
        if (!TryResolveCombatContext(requestedPlayerId, out var combatContext))
        {
            return CombatActionUnavailable(
                request.Kind,
                "play-card is only available during the active combat play phase for the local player.",
                "card_id",
                cardId,
                "Retry when state.screen.id is combat and availableActions includes play-card.");
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
                    Action: "play-card"),
                out var ownershipFailure))
        {
            return ownershipFailure;
        }

        if (!Sts2ActionCatalog.CanPlayCard(
            combatContext.Screen.ScreenType,
            combatContext.ActionOwnerPlayerId,
            Sts2CombatFacts.IsPlayPhase(combatContext.ActionOwnerPlayer),
            requestedPlayerId,
            combatContext.IsActionOwnerHostLocalSeat))
        {
            return CombatActionUnavailable(
                request.Kind,
                "play-card is only available during the active combat play phase for the local player.",
                "player_id",
                requestedPlayerId ?? string.Empty,
                $"screen={combatContext.Screen.ScreenType}, activePlayerId={combatContext.ActionOwnerPlayerId}, isPlayerTurn={Sts2CombatFacts.IsPlayPhase(combatContext.ActionOwnerPlayer)}.");
        }

        // The hand's selection backstop blocks card play while a card effect is
        // asking for hand cards (e.g. Survivor's discard); mirror that gate.
        if (NPlayerHand.Instance?.IsInCardSelection == true)
        {
            return CombatActionUnavailable(
                request.Kind,
                "play-card is blocked while the hand is in card-selection mode.",
                "card_id",
                cardId,
                "Resolve state.run.view.handSelection first via select-hand-card/confirm-hand-selection.");
        }

        var resolvedCard = ResolveLocalHandCard(combatContext.ActionOwnerPlayer, combatContext.ActionOwnerPlayerId, cardId);
        if (resolvedCard is null)
        {
            return CombatActionUnavailable(
                request.Kind,
                "play-card requires a stable card id from the current combat hand.",
                "card_id",
                cardId,
                "Provide a stable card id from state.combat.hand[].id or availableActions[].arguments.cardId.");
        }

        if (!resolvedCard.Card.CanPlay())
        {
            return CombatActionUnavailable(
                request.Kind,
                "play-card requires a currently playable card from the active combat hand.",
                "card_id",
                cardId,
                "The requested card is currently unplayable; use state.combat.hand[].playable or availableActions to discover legal actions.");
        }

        var requestedTargetId = request.TargetId?.Trim();
        var resolvedTarget = ResolveCombatTarget(combatContext.CombatState, requestedTargetId);
        if (!string.IsNullOrWhiteSpace(requestedTargetId) && resolvedTarget is null)
        {
            return CombatActionUnavailable(
                request.Kind,
                "play-card requires a stable target id visible in the current combat state.",
                "target_id",
                requestedTargetId,
                "Provide a target id from state.combat.hand[].targetIds or availableActions[].arguments.targetId.");
        }

        if (string.IsNullOrWhiteSpace(requestedTargetId) && !resolvedCard.Card.CanPlayTargeting(null))
        {
            var availableTargetIds = ResolveTargetIds(resolvedCard.Card, combatContext.CombatState);
            var note = availableTargetIds.Count == 0
                ? "The requested card currently needs an explicit target from state.combat.hand[].targetIds."
                : $"The requested card needs one of the current target ids: {string.Join(", ", availableTargetIds)}.";
            return CombatActionUnavailable(
                request.Kind,
                "play-card requires a target for the requested card in the current combat state.",
                "target_id",
                string.Empty,
                note);
        }

        if (!resolvedCard.Card.CanPlayTargeting(resolvedTarget?.Creature))
        {
            return CombatActionUnavailable(
                request.Kind,
                "play-card requires a legal target for the requested card in the current combat state.",
                "target_id",
                requestedTargetId ?? string.Empty,
                "Use state.combat.hand[].targetIds or availableActions to discover currently valid targets.");
        }

        if (!resolvedCard.Card.TryManualPlay(resolvedTarget?.Creature))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.RuntimeFailure,
                message: "The runtime rejected the requested card play after preflight validation succeeded.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "card_id",
                        Value: cardId,
                        Note: "The combat state likely changed before the card play was enqueued."),
                ]);
        }

        var message = resolvedTarget is null
            ? $"Played {resolvedCard.Name}."
            : $"Played {resolvedCard.Name} targeting {resolvedTarget.Name}.";
        _logStream.Write(BridgeLogLevel.Info, "bridge.action", message);
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:play-card:{request.RequestId}",
            kind: request.Kind,
            message: message);
    }

    private ActionExecutionResult ExecuteUsePotion(SemanticActionRequest request)
    {
        var potionId = request.PotionId?.Trim();
        if (string.IsNullOrWhiteSpace(potionId))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "use-potion requires a stable potion id from the current combat belt.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "potion_id",
                        Value: string.Empty,
                        Note: "Provide a stable potion id from state.combat.potions[].id or availableActions[].arguments.potionId."),
                ]);
        }

        var requestedPlayerId = request.Perspective?.PlayerId;
        if (!TryResolveCombatContext(requestedPlayerId, out var combatContext))
        {
            return CombatActionUnavailable(
                request.Kind,
                "use-potion is only available during the active combat play phase for the local player.",
                "potion_id",
                potionId,
                "Retry when state.screen.id is combat and availableActions includes use-potion.");
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
                    Action: "use-potion"),
                out var ownershipFailure))
        {
            return ownershipFailure;
        }

        if (!Sts2ActionCatalog.CanUsePotion(
            combatContext.Screen.ScreenType,
            combatContext.ActionOwnerPlayerId,
            Sts2CombatFacts.IsPlayPhase(combatContext.ActionOwnerPlayer),
            combatContext.ActionOwnerPlayerId,
            requestedPlayerId,
            combatContext.IsActionOwnerHostLocalSeat))
        {
            return CombatActionUnavailable(
                request.Kind,
                "use-potion is only available during the active combat play phase for the local player.",
                "player_id",
                requestedPlayerId ?? string.Empty,
                $"screen={combatContext.Screen.ScreenType}, activePlayerId={combatContext.ActionOwnerPlayerId}, isPlayerTurn={Sts2CombatFacts.IsPlayPhase(combatContext.ActionOwnerPlayer)}.");
        }

        var resolvedPotion = ResolveLocalPotion(combatContext.ActionOwnerPlayer, combatContext.ActionOwnerPlayerId, potionId);
        if (resolvedPotion is null)
        {
            return CombatActionUnavailable(
                request.Kind,
                "use-potion requires a stable potion id from the current combat belt.",
                "potion_id",
                potionId,
                "Provide a stable potion id from state.combat.potions[].id or availableActions[].arguments.potionId.");
        }

        if (resolvedPotion.Potion.IsQueued)
        {
            return CombatActionUnavailable(
                request.Kind,
                "use-potion requires a potion that is not already queued for use.",
                "potion_id",
                potionId,
                "Retry after the queued potion action resolves.");
        }

        if (!PotionCanBeUsedManually(resolvedPotion.Potion))
        {
            return CombatActionUnavailable(
                request.Kind,
                "use-potion requires a potion that can be manually used.",
                "potion_id",
                potionId,
                "Automatic potions trigger from game events and are not exposed as manual use-potion actions.");
        }

        if (!resolvedPotion.Potion.PassesCustomUsabilityCheck)
        {
            return CombatActionUnavailable(
                request.Kind,
                "use-potion requires a potion that is currently usable.",
                "potion_id",
                potionId,
                "Use state.combat.potions[].usable or availableActions to discover legal potion actions.");
        }

        var requestedTargetId = request.TargetId?.Trim();
        var availableTargetIds = ResolvePotionTargetIds(resolvedPotion.Potion, combatContext.ActionOwnerPlayerId, combatContext.CombatState);
        var requiresTarget = PotionNeedsTarget(resolvedPotion.Potion);
        var resolvedTarget = ResolvePotionTarget(combatContext.CombatState, requestedTargetId);
        if (!string.IsNullOrWhiteSpace(requestedTargetId) && resolvedTarget is null)
        {
            return CombatActionUnavailable(
                request.Kind,
                "use-potion requires a stable visible target id from the current combat state.",
                "target_id",
                requestedTargetId,
                "Provide a target id from state.combat.potions[].targetIds or availableActions[].arguments.targetId.");
        }

        if (requiresTarget && string.IsNullOrWhiteSpace(requestedTargetId))
        {
            // "Self" potions (Bone Brew, Attack Potion, Duplicator, …) target the owner
            // unambiguously — the browser popup drinks them via use-potion with NO explicit
            // target (only AnyEnemy/AnyPlayer/AnyAlly go through the targeting reticle). Resolve
            // the owner's own creature instead of rejecting, so Self potions are actually usable.
            var targetType = Sts2LiveIntrospection.GetMemberValue(resolvedPotion.Potion, "TargetType")?.ToString();
            if (string.Equals(targetType, "Self", StringComparison.Ordinal) && availableTargetIds.Count > 0)
            {
                resolvedTarget = ResolvePotionTarget(combatContext.CombatState, availableTargetIds[0]);
            }

            if (resolvedTarget is null)
            {
                var note = availableTargetIds.Count == 0
                    ? "The requested potion currently needs a target, but no valid target ids are visible."
                    : $"The requested potion needs one of the current target ids: {string.Join(", ", availableTargetIds)}.";
                return CombatActionUnavailable(
                    request.Kind,
                    "use-potion requires a target for the requested potion in the current combat state.",
                    "target_id",
                    string.Empty,
                    note);
            }
        }

        if (!string.IsNullOrWhiteSpace(requestedTargetId)
            && !availableTargetIds.Contains(requestedTargetId, StringComparer.Ordinal))
        {
            return CombatActionUnavailable(
                request.Kind,
                "use-potion requires a legal target for the requested potion in the current combat state.",
                "target_id",
                requestedTargetId,
                "Use state.combat.potions[].targetIds or availableActions to discover currently valid targets.");
        }

        resolvedPotion.Potion.EnqueueManualUse(resolvedTarget?.Creature);
        var message = resolvedTarget is null
            ? $"Used {resolvedPotion.Name}."
            : $"Used {resolvedPotion.Name} targeting {resolvedTarget.Name}.";
        _logStream.Write(BridgeLogLevel.Info, "bridge.action", message);
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:use-potion:{request.RequestId}",
            kind: request.Kind,
            message: message);
    }

    private ActionExecutionResult ExecuteOpenPotionPopup(SemanticActionRequest request)
    {
        if (!TryResolvePotionUiAction(request, "open-potion-popup", requireUsable: true, out _, out var resolvedPotion, out var holder, out var failure))
        {
            return failure!;
        }

        holder!.TryGrabFocus();
        if (!Sts2LiveIntrospection.TryInvokeMethod(holder, "OpenPotionPopup"))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not open the potion popup through the active live hooks.",
                details: [new ActionFailureDetail("potion_id", resolvedPotion!.Id, "The top-bar potion holder did not expose OpenPotionPopup.")]);
        }

        return ActionExecutionResult.Success(
            actionInstanceId: $"action:open-potion-popup:{request.RequestId}",
            kind: request.Kind,
            message: $"Opened {resolvedPotion!.Name}.");
    }

    private ActionExecutionResult ExecuteStartPotionTargeting(SemanticActionRequest request)
    {
        if (!TryResolvePotionUiAction(request, "start-potion-targeting", requireUsable: true, out _, out var resolvedPotion, out var holder, out var failure))
        {
            return failure!;
        }

        if (!PotionNeedsTarget(resolvedPotion!.Potion))
        {
            return CombatActionUnavailable(
                request.Kind,
                "start-potion-targeting requires a potion that uses target selection.",
                "potion_id",
                resolvedPotion.Id,
                "Use use-potion for potions that do not require a manual target.");
        }

        holder!.TryGrabFocus();
        StartPotionTargeting(holder, resolvedPotion.Potion);

        return ActionExecutionResult.Success(
            actionInstanceId: $"action:start-potion-targeting:{request.RequestId}",
            kind: request.Kind,
            message: $"Started targeting with {resolvedPotion.Name}.");
    }

    private ActionExecutionResult ExecuteDiscardPotion(SemanticActionRequest request)
    {
        if (!TryResolvePotionUiAction(request, "discard-potion", requireUsable: false, out _, out var resolvedPotion, out var holder, out var failure))
        {
            return failure!;
        }

        if (!Sts2LiveIntrospection.TryInvokeMethod(holder!, "DiscardPotion"))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not discard the potion through the active live hooks.",
                details: [new ActionFailureDetail("potion_id", resolvedPotion!.Id, "The top-bar potion holder did not expose DiscardPotion.")]);
        }

        return ActionExecutionResult.Success(
            actionInstanceId: $"action:discard-potion:{request.RequestId}",
            kind: request.Kind,
            message: $"Discarded {resolvedPotion!.Name}.");
    }

    private ActionExecutionResult ExecuteSelectTarget(SemanticActionRequest request)
    {
        var requestedPlayerId = request.Perspective?.PlayerId;
        if (!TryResolveCombatContext(requestedPlayerId, out var combatContext))
        {
            return CombatActionUnavailable(
                request.Kind,
                "select-target is only available during active combat targeting.",
                "target_id",
                request.TargetId ?? string.Empty,
                "Retry when state.run.view.selectedPotion.mode is targeting.");
        }

        var requestedTargetId = request.TargetId?.Trim();
        var resolvedTarget = ResolveCombatTarget(combatContext.CombatState, requestedTargetId);
        if (resolvedTarget is null)
        {
            return CombatActionUnavailable(
                request.Kind,
                "select-target requires a stable visible target id from the current combat state.",
                "target_id",
                requestedTargetId ?? string.Empty,
                "Provide a target id derived from state.run.view.selectedPotion plus combat creatures, or availableActions[].arguments.targetId.");
        }

        if (Sts2TargetManagerAccess.InstanceOrNull is not { IsInSelection: true } targetManager)
        {
            return CombatActionUnavailable(
                request.Kind,
                "select-target requires an active target selection flow.",
                "target_id",
                requestedTargetId ?? string.Empty,
                "Start potion targeting first.");
        }

        var targetNode = NCombatRoom.Instance?.GetCreatureNode(resolvedTarget.Creature);
        if (targetNode is null)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.NotVisible,
                message: "Could not resolve a visible node for the requested combat target.",
                details: [new ActionFailureDetail("target_id", resolvedTarget.Id, "The active combat room did not expose a creature node for this target.")]);
        }

        var selectedPotion = ResolveSelectedPotion();
        targetManager.OnNodeHovered(targetNode);
        if (!Sts2LiveIntrospection.TryInvokeMethod(targetManager, "FinishTargeting", false))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not finish target selection through the active live hooks.",
                details: [new ActionFailureDetail("target_id", resolvedTarget.Id, "NTargetManager did not expose FinishTargeting.")]);
        }

        if (selectedPotion is not null)
        {
            selectedPotion.EnqueueManualUse(resolvedTarget.Creature);
            RunManager.Instance?.HoveredModelTracker.OnLocalPotionDeselected();
        }

        return ActionExecutionResult.Success(
            actionInstanceId: $"action:select-target:{request.RequestId}",
            kind: request.Kind,
            message: $"Selected target {resolvedTarget.Name}.");
    }

    private bool TryResolvePotionUiAction(
        SemanticActionRequest request,
        string actionName,
        bool requireUsable,
        [NotNullWhen(true)] out CombatActionContext? combatContext,
        [NotNullWhen(true)] out ResolvedCombatPotion? resolvedPotion,
        [NotNullWhen(true)] out NPotionHolder? holder,
        [NotNullWhen(false)] out ActionExecutionResult? failure)
    {
        combatContext = null;
        resolvedPotion = null;
        holder = null;
        failure = null;
        var potionId = request.PotionId?.Trim();
        if (string.IsNullOrWhiteSpace(potionId))
        {
            failure = ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: $"{actionName} requires a stable potion id from the current combat belt.",
                details: [new ActionFailureDetail("potion_id", string.Empty, "Provide a stable potion id or slot index from state.run.players[].potions[].")]);
            return false;
        }

        var requestedPlayerId = request.Perspective?.PlayerId;
        if (!TryResolveCombatContext(requestedPlayerId, out combatContext))
        {
            failure = CombatActionUnavailable(
                request.Kind,
                $"{actionName} is only available during the active combat play phase for the local player.",
                "potion_id",
                potionId,
                $"Retry when state.screen.id is combat and availableActions includes {actionName}.");
            return false;
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
                    Action: actionName),
                out var ownershipFailure))
        {
            failure = ownershipFailure;
            return false;
        }

        resolvedPotion = ResolveLocalPotion(combatContext.ActionOwnerPlayer, combatContext.ActionOwnerPlayerId, potionId);
        if (resolvedPotion is null)
        {
            failure = CombatActionUnavailable(
                request.Kind,
                $"{actionName} requires a stable potion id from the current combat belt.",
                "potion_id",
                potionId,
                "Provide a stable potion id or slot index from state.run.players[].potions[].");
            return false;
        }

        if (requireUsable)
        {
            if (resolvedPotion.Potion.IsQueued)
            {
                failure = CombatActionUnavailable(request.Kind, $"{actionName} requires a potion that is not already queued for use.", "potion_id", potionId, "Retry after the queued potion action resolves.");
                return false;
            }

            if (!PotionCanBeUsedManually(resolvedPotion.Potion))
            {
                failure = CombatActionUnavailable(request.Kind, $"{actionName} requires a potion that can be manually used.", "potion_id", potionId, "Automatic potions trigger from game events.");
                return false;
            }

            if (!resolvedPotion.Potion.PassesCustomUsabilityCheck)
            {
                failure = CombatActionUnavailable(request.Kind, $"{actionName} requires a potion that is currently usable.", "potion_id", potionId, "Use state.run.players[].potions[] plus potion models and availableActions to discover legal potion actions.");
                return false;
            }
        }

        holder = ResolveTopBarPotionHolder(resolvedPotion.SlotIndex, resolvedPotion.Potion);
        if (holder is null)
        {
            failure = ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.NotVisible,
                message: $"{actionName} requires the potion to be visible in the local top bar.",
                details: [new ActionFailureDetail("potion_id", resolvedPotion.Id, "No top-bar potion holder matched the requested potion.")]);
            return false;
        }

        return true;
    }

    private static void StartPotionTargeting(NPotionHolder holder, PotionModel potion)
    {
        RunManager.Instance?.HoveredModelTracker.OnLocalPotionSelected(potion);
        var startPosition = holder.GlobalPosition + Vector2.Right * holder.Size.X * 0.5f + Vector2.Down * 50f;
        Sts2TargetManagerAccess.InstanceOrNull?.StartTargeting(
            potion.TargetType,
            startPosition,
            TargetMode.ClickMouseToTarget,
            () => holder.Potion is null || !ReferenceEquals(holder.Potion.Model, potion),
            null);
    }

    private static PotionModel? ResolveSelectedPotion()
        => Sts2LiveIntrospection.GetMemberValue(RunManager.Instance?.HoveredModelTracker, "_localSelectedPotion") as PotionModel;

    private static NPotionHolder? ResolveTopBarPotionHolder(int slotIndex, PotionModel potion)
    {
        var holders = Sts2LiveIntrospection.GetMemberValue(
            Sts2LiveIntrospection.GetMemberValue(
                Sts2LiveIntrospection.GetMemberValue(
                    Sts2LiveIntrospection.GetMemberValue(NRun.Instance, "GlobalUi"),
                    "TopBar"),
                "PotionContainer"),
            "_holders");
        var holder = EnumerateActionCollection(holders).ElementAtOrDefault(slotIndex) as NPotionHolder;
        if (holder?.Potion?.Model == potion)
        {
            return holder;
        }

        return EnumerateActionCollection(holders)
            .OfType<NPotionHolder>()
            .FirstOrDefault(candidate => candidate.Potion?.Model == potion);
    }

    private static IEnumerable<object?> EnumerateActionCollection(object? value)
        => value is System.Collections.IEnumerable enumerable
            ? enumerable.Cast<object?>()
            : [];

}
