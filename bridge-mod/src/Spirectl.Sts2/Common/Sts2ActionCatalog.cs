using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2;

public static class Sts2ActionCatalog
{
    public const string MainMenuStartRunChoiceId = "menu:start-run";

    public static IReadOnlyList<AvailableActionSnapshot> MainMenuActions()
    {
        return
        [
            new AvailableActionSnapshot(
                Sts2ActionIds.MainMenuStartRun(),
                SemanticActionKind.Choose,
                "Choose the visible Start Run option.",
                "sts2 act choose --choice menu:start-run",
                Provisional: false,
                Arguments: new ActionArgumentsSnapshot(null, null, null, MainMenuStartRunChoiceId, null, null, IntentKind: "start-run"),
                IntentKind: "start-run",
                PreferredAction: "choose",
                LegalityStatus: ActionLegalityKind.Legal),
        ];
    }

    public static IReadOnlyList<AvailableActionSnapshot> RewardActions(
        IReadOnlyList<ChoiceSnapshot> rewardChoices,
        IReadOnlyDictionary<string, string?> choiceOwnersById)
    {
        return rewardChoices
            .Select(choice => RewardAction(choice, choiceOwnersById.GetValueOrDefault(choice.Id)))
            .ToArray();
    }

    public static IReadOnlyList<AvailableActionSnapshot> ShopActions(
        IReadOnlyList<ChoiceSnapshot> shopChoices,
        IReadOnlyDictionary<string, string?> choiceOwnersById)
    {
        return shopChoices
            .Select(choice => ShopAction(choice, choiceOwnersById.GetValueOrDefault(choice.Id)))
            .ToArray();
    }

    public static IReadOnlyList<AvailableActionSnapshot> CardSelectionActions(
        string? playerId,
        IReadOnlyList<ChoiceSnapshot> choices,
        bool confirmSelectionAvailable = false,
        bool cancelSelectionAvailable = false)
    {
        var actions = choices
            .Select(choice => CardSelectionAction(choice, choice.OwnerPlayerId ?? playerId))
            .Where(action => action is not null)
            .Select(action => action!)
            .ToList();

        if (confirmSelectionAvailable)
        {
            actions.Add(new AvailableActionSnapshot(
                Sts2ActionIds.IntentWithoutChoice("card-selection", "confirm-selection"),
                SemanticActionKind.ConfirmSelection,
                "Confirm the currently staged selection.",
                "sts2 act confirm-selection",
                Provisional: false,
                Arguments: new ActionArgumentsSnapshot(
                    playerId,
                    null,
                    null,
                    null,
                    null,
                    null),
                IntentKind: "confirm-selection",
                OwnerPlayerId: playerId,
                Perspective: ResolvePerspective(playerId),
                PreferredAction: "confirm-selection",
                LegalityStatus: ActionLegalityKind.Legal));
        }

        if (cancelSelectionAvailable)
        {
            actions.Add(new AvailableActionSnapshot(
                Sts2ActionIds.IntentWithoutChoice("card-selection", "cancel-selection"),
                SemanticActionKind.CancelSelection,
                "Cancel the currently staged selection.",
                "sts2 act cancel-selection",
                Provisional: false,
                Arguments: new ActionArgumentsSnapshot(
                    playerId,
                    null,
                    null,
                    null,
                    null,
                    null),
                IntentKind: "cancel-selection",
                OwnerPlayerId: playerId,
                Perspective: ResolvePerspective(playerId),
                PreferredAction: "cancel-selection",
                LegalityStatus: ActionLegalityKind.Legal));
        }

        return actions;
    }

    private static AvailableActionSnapshot RewardAction(ChoiceSnapshot choice, string? ownerPlayerId)
    {
        if (string.Equals(choice.PreferredAction, "claim-reward", StringComparison.Ordinal))
        {
            return IntentAction(
                "rewards",
                choice,
                ownerPlayerId,
                SemanticActionKind.ClaimReward,
                "claim-reward",
                $"Claim {choice.Label}.",
                $"sts2 act claim-reward --reward {choice.Id}",
                new ActionArgumentsSnapshot(
                    ownerPlayerId,
                    null,
                    null,
                    null,
                    null,
                    null,
                    IntentKind: "claim-reward",
                    Values: new Dictionary<string, string> { ["rewardId"] = choice.Id }));
        }

        if (string.Equals(choice.PreferredAction, "skip-rewards", StringComparison.Ordinal))
        {
            return IntentAction(
                "rewards",
                choice,
                ownerPlayerId,
                SemanticActionKind.SkipRewards,
                "skip-rewards",
                choice.Label,
                "sts2 act skip-rewards",
                new ActionArgumentsSnapshot(ownerPlayerId, null, null, null, null, null, IntentKind: "skip-rewards"));
        }

        return ChoiceAction("reward", choice, ownerPlayerId);
    }

    private static AvailableActionSnapshot ShopAction(ChoiceSnapshot choice, string? ownerPlayerId)
    {
        if (string.Equals(choice.PreferredAction, "buy-card", StringComparison.Ordinal))
        {
            return IntentAction(
                "shop",
                choice,
                ownerPlayerId,
                SemanticActionKind.BuyCard,
                "buy-card",
                choice.Label,
                $"sts2 act buy-card --shop-item {choice.Id}",
                new ActionArgumentsSnapshot(
                    ownerPlayerId,
                    choice.Id,
                    null,
                    null,
                    null,
                    null,
                    IntentKind: "buy-card",
                    Values: new Dictionary<string, string> { ["shopItemId"] = choice.Id }));
        }

        if (string.Equals(choice.PreferredAction, "buy-relic", StringComparison.Ordinal))
        {
            return IntentAction(
                "shop",
                choice,
                ownerPlayerId,
                SemanticActionKind.BuyRelic,
                "buy-relic",
                choice.Label,
                $"sts2 act buy-relic --shop-item {choice.Id}",
                new ActionArgumentsSnapshot(
                    ownerPlayerId,
                    null,
                    null,
                    null,
                    null,
                    null,
                    IntentKind: "buy-relic",
                    Values: new Dictionary<string, string> { ["shopItemId"] = choice.Id, ["relicId"] = choice.Id }));
        }

        if (string.Equals(choice.PreferredAction, "buy-potion", StringComparison.Ordinal))
        {
            return IntentAction(
                "shop",
                choice,
                ownerPlayerId,
                SemanticActionKind.BuyPotion,
                "buy-potion",
                choice.Label,
                $"sts2 act buy-potion --shop-item {choice.Id}",
                new ActionArgumentsSnapshot(
                    ownerPlayerId,
                    null,
                    null,
                    null,
                    null,
                    null,
                    PotionId: choice.Id,
                    IntentKind: "buy-potion",
                    Values: new Dictionary<string, string> { ["shopItemId"] = choice.Id }));
        }

        if (string.Equals(choice.PreferredAction, "remove-card", StringComparison.Ordinal))
        {
            return IntentAction(
                "shop",
                choice,
                ownerPlayerId,
                SemanticActionKind.RemoveCard,
                "remove-card",
                choice.Label,
                $"sts2 act remove-card --shop-item {choice.Id}",
                new ActionArgumentsSnapshot(
                    ownerPlayerId,
                    null,
                    null,
                    null,
                    null,
                    null,
                    IntentKind: "remove-card",
                    Values: new Dictionary<string, string> { ["shopItemId"] = choice.Id }));
        }

        if (string.Equals(choice.PreferredAction, "leave-shop", StringComparison.Ordinal))
        {
            return IntentAction(
                "shop",
                choice,
                ownerPlayerId,
                SemanticActionKind.LeaveShop,
                "leave-shop",
                choice.Label,
                "sts2 act leave-shop",
                new ActionArgumentsSnapshot(ownerPlayerId, null, null, null, null, null, IntentKind: "leave-shop"));
        }

        if (string.Equals(choice.PreferredAction, "close-shop-inventory", StringComparison.Ordinal))
        {
            return IntentAction(
                "shop",
                choice,
                ownerPlayerId,
                SemanticActionKind.CloseShopInventory,
                "close-shop-inventory",
                choice.Label,
                "sts2 act close-shop-inventory",
                new ActionArgumentsSnapshot(ownerPlayerId, null, null, null, null, null, IntentKind: "close-shop-inventory"));
        }

        return ChoiceAction("shop", choice, ownerPlayerId);
    }

    private static AvailableActionSnapshot? CardSelectionAction(ChoiceSnapshot choice, string? ownerPlayerId)
    {
        if (string.Equals(choice.PreferredAction, "select-card", StringComparison.Ordinal))
        {
            return IntentAction(
                "card-selection",
                choice,
                ownerPlayerId,
                SemanticActionKind.SelectCard,
                "select-card",
                $"Select {choice.Label}.",
                $"sts2 act select-card --card {choice.Id}",
                new ActionArgumentsSnapshot(
                    ownerPlayerId,
                    choice.Id,
                    null,
                    null,
                    null,
                    null,
                    IntentKind: "select-card",
                    Values: new Dictionary<string, string> { ["cardId"] = choice.Id }));
        }

        if (string.Equals(choice.PreferredAction, "skip-card-selection", StringComparison.Ordinal))
        {
            return IntentAction(
                "card-selection",
                choice,
                ownerPlayerId,
                SemanticActionKind.SkipCardSelection,
                "skip-card-selection",
                choice.Label,
                "sts2 act skip-card-selection",
                new ActionArgumentsSnapshot(ownerPlayerId, null, null, null, null, null, IntentKind: "skip-card-selection"));
        }

        if (string.Equals(choice.PreferredAction, "select-bundle", StringComparison.Ordinal))
        {
            return IntentAction(
                "card-selection",
                choice,
                ownerPlayerId,
                SemanticActionKind.SelectBundle,
                "select-bundle",
                $"Select {choice.Label}.",
                $"sts2 act select-bundle --bundle {choice.Id}",
                new ActionArgumentsSnapshot(
                    ownerPlayerId,
                    null,
                    null,
                    null,
                    null,
                    null,
                    IntentKind: "select-bundle",
                    Values: new Dictionary<string, string> { ["bundleId"] = choice.Id }));
        }

        return IsFallbackChoice(choice)
            ? ChoiceAction("card-selection", choice, ownerPlayerId)
            : null;
    }

    private static AvailableActionSnapshot IntentAction(
        string actionScope,
        ChoiceSnapshot choice,
        string? ownerPlayerId,
        SemanticActionKind kind,
        string intentKind,
        string summary,
        string cliCommandHint,
        ActionArgumentsSnapshot arguments)
        => new(
            Sts2ActionIds.Intent(actionScope, intentKind, choice.Id),
            kind,
            summary,
            cliCommandHint,
            Provisional: choice.Provisional,
            Arguments: arguments,
            IntentKind: intentKind,
            OwnerPlayerId: ownerPlayerId,
            Perspective: choice.Perspective ?? ResolvePerspective(ownerPlayerId),
            PreferredAction: intentKind,
            LegalityStatus: choice.LegalityStatus == ActionLegalityKind.Unspecified
                ? ActionLegalityKind.Legal
                : choice.LegalityStatus,
            DisabledReason: choice.DisabledReason,
            CheckedHookPaths: choice.CheckedHookPaths,
            PreferredActionRef: choice.PreferredActionRef);

    public static IReadOnlyList<AvailableActionSnapshot> CardOverlayActions(
        CardOverlayStateSnapshot overlay,
        IReadOnlyList<ChoiceSnapshot> fallbackChoices)
    {
        var actions = new List<AvailableActionSnapshot>();
        AddOverlayAffordanceAction(actions, overlay.Close);
        AddOverlayAffordanceAction(actions, overlay.Back);
        actions.AddRange(fallbackChoices.Where(IsFallbackChoice).Select(choice => ChoiceAction("card-overlay", choice, choice.OwnerPlayerId ?? overlay.OwnerPlayerId)));
        return actions;
    }

    public static IReadOnlyList<AvailableActionSnapshot> EventRoomActions(
        string? playerId,
        IReadOnlyList<ChoiceSnapshot> choices)
    {
        return choices
            .Select(choice => EventRoomAction(choice, choice.OwnerPlayerId ?? playerId))
            .Where(action => action is not null)
            .Select(action => action!)
            .ToArray();
    }

    public static IReadOnlyList<AvailableActionSnapshot> CrystalSphereActions(
        string? playerId,
        IReadOnlyList<ChoiceSnapshot> choices)
    {
        return choices
            .Select(choice => CrystalSphereAction(choice, choice.OwnerPlayerId ?? playerId))
            .Where(action => action is not null)
            .Select(action => action!)
            .ToArray();
    }

    private static AvailableActionSnapshot? EventRoomAction(ChoiceSnapshot choice, string? ownerPlayerId)
    {
        if (string.Equals(choice.PreferredAction, "select-event-option", StringComparison.Ordinal))
        {
            return IntentAction(
                "event-room",
                choice,
                ownerPlayerId,
                SemanticActionKind.SelectEventOption,
                "select-event-option",
                choice.Label,
                $"sts2 act select-event-option --event-option {choice.Id}",
                new ActionArgumentsSnapshot(
                    ownerPlayerId,
                    null,
                    null,
                    null,
                    null,
                    null,
                    IntentKind: "select-event-option",
                    Values: new Dictionary<string, string> { ["eventOptionId"] = choice.Id }));
        }

        if (string.Equals(choice.PreferredAction, "open-event-shop", StringComparison.Ordinal))
        {
            return IntentAction(
                "event-room",
                choice,
                ownerPlayerId,
                SemanticActionKind.OpenEventShop,
                "open-event-shop",
                choice.Label,
                $"sts2 act open-event-shop --event-option {choice.Id}",
                new ActionArgumentsSnapshot(
                    ownerPlayerId,
                    null,
                    null,
                    null,
                    null,
                    null,
                    IntentKind: "open-event-shop",
                    Values: new Dictionary<string, string> { ["eventOptionId"] = choice.Id }));
        }

        if (string.Equals(choice.PreferredAction, "proceed-event", StringComparison.Ordinal))
        {
            return IntentAction(
                "event-room",
                choice,
                ownerPlayerId,
                SemanticActionKind.ProceedEvent,
                "proceed-event",
                choice.Label,
                "sts2 act proceed-event",
                new ActionArgumentsSnapshot(ownerPlayerId, null, null, null, null, null, IntentKind: "proceed-event"));
        }

        return IsFallbackChoice(choice) ? ChoiceAction("event-room", choice, ownerPlayerId) : null;
    }

    private static AvailableActionSnapshot? CrystalSphereAction(ChoiceSnapshot choice, string? ownerPlayerId)
    {
        if (string.Equals(choice.PreferredAction, "use-crystal-sphere-control", StringComparison.Ordinal))
        {
            return IntentAction(
                "crystal-sphere",
                choice,
                ownerPlayerId,
                SemanticActionKind.UseCrystalSphereControl,
                "use-crystal-sphere-control",
                choice.Label,
                $"sts2 act use-crystal-sphere-control --control {choice.Id}",
                new ActionArgumentsSnapshot(
                    ownerPlayerId,
                    null,
                    null,
                    null,
                    null,
                    null,
                    IntentKind: "use-crystal-sphere-control",
                    Values: new Dictionary<string, string> { ["controlId"] = choice.Id }));
        }

        if (string.Equals(choice.PreferredAction, "proceed-event", StringComparison.Ordinal))
        {
            return IntentAction(
                "crystal-sphere",
                choice,
                ownerPlayerId,
                SemanticActionKind.ProceedEvent,
                "proceed-event",
                choice.Label,
                "sts2 act proceed-event",
                new ActionArgumentsSnapshot(ownerPlayerId, null, null, null, null, null, IntentKind: "proceed-event"));
        }

        return IsFallbackChoice(choice) ? ChoiceAction("crystal-sphere", choice, ownerPlayerId) : null;
    }

    public static IReadOnlyList<AvailableActionSnapshot> TreasureRoomActions(
        string? playerId,
        IReadOnlyList<ChoiceSnapshot> choices)
    {
        return choices
            .Select(choice => TreasureRoomAction(choice, choice.OwnerPlayerId ?? playerId))
            .Where(action => action is not null)
            .Select(action => action!)
            .ToArray();
    }

    public static IReadOnlyList<AvailableActionSnapshot> RestSiteActions(
        IReadOnlyList<ChoiceSnapshot> restSiteChoices,
        IReadOnlyDictionary<string, string?> choiceOwnersById)
    {
        return restSiteChoices
            .Select(choice => RestSiteAction(choice, choiceOwnersById.GetValueOrDefault(choice.Id)))
            .Where(action => action is not null)
            .Select(action => action!)
            .ToArray();
    }

    private static AvailableActionSnapshot? TreasureRoomAction(ChoiceSnapshot choice, string? ownerPlayerId)
    {
        if (string.Equals(choice.PreferredAction, "open-chest", StringComparison.Ordinal))
        {
            return IntentAction(
                "treasure-room",
                choice,
                ownerPlayerId,
                SemanticActionKind.OpenChest,
                "open-chest",
                choice.Label,
                "sts2 act open-chest",
                new ActionArgumentsSnapshot(ownerPlayerId, null, null, null, null, null, IntentKind: "open-chest"));
        }

        if (string.Equals(choice.PreferredAction, "take-relic", StringComparison.Ordinal))
        {
            var relicId = choice.Arguments?.Values?.GetValueOrDefault("relicId")
                ?? (Sts2TreasureRoomIds.TryParseRelicChoiceId(choice.Id, out var parsedRelicId, out _) ? parsedRelicId : choice.Id);
            return IntentAction(
                "treasure-room",
                choice,
                ownerPlayerId,
                SemanticActionKind.TakeRelic,
                "take-relic",
                choice.Label,
                $"sts2 act take-relic --relic {relicId}",
                new ActionArgumentsSnapshot(
                    ownerPlayerId,
                    null,
                    null,
                    null,
                    null,
                    null,
                    IntentKind: "take-relic",
                    Values: new Dictionary<string, string> { ["relicId"] = relicId }));
        }

        if (string.Equals(choice.PreferredAction, "proceed-treasure-room", StringComparison.Ordinal))
        {
            return IntentAction(
                "treasure-room",
                choice,
                ownerPlayerId,
                SemanticActionKind.ProceedTreasureRoom,
                "proceed-treasure-room",
                choice.Label,
                "sts2 act proceed-treasure-room",
                new ActionArgumentsSnapshot(ownerPlayerId, null, null, null, null, null, IntentKind: "proceed-treasure-room"));
        }

        return IsFallbackChoice(choice) ? ChoiceAction("treasure-room", choice, ownerPlayerId) : null;
    }

    private static AvailableActionSnapshot? RestSiteAction(ChoiceSnapshot choice, string? ownerPlayerId)
    {
        if (string.Equals(choice.PreferredAction, "rest", StringComparison.Ordinal))
        {
            return IntentAction(
                "rest-site",
                choice,
                ownerPlayerId,
                SemanticActionKind.Rest,
                "rest",
                choice.Label,
                "sts2 act rest",
                new ActionArgumentsSnapshot(ownerPlayerId, null, null, null, null, null, IntentKind: "rest", Values: choice.Arguments?.Values));
        }

        if (string.Equals(choice.PreferredAction, "smith", StringComparison.Ordinal))
        {
            return IntentAction(
                "rest-site",
                choice,
                ownerPlayerId,
                SemanticActionKind.Smith,
                "smith",
                choice.Label,
                "sts2 act smith",
                new ActionArgumentsSnapshot(ownerPlayerId, null, null, null, null, null, IntentKind: "smith", Values: choice.Arguments?.Values));
        }

        if (string.Equals(choice.PreferredAction, "use-rest-site-option", StringComparison.Ordinal))
        {
            var restOptionId = choice.Arguments?.Values?.GetValueOrDefault("restOptionId")
                ?? (Sts2RestSiteIds.TryParseOptionChoiceId(choice.Id, out _, out var parsedOptionId, out _) ? parsedOptionId : choice.Id);
            return IntentAction(
                "rest-site",
                choice,
                ownerPlayerId,
                SemanticActionKind.UseRestSiteOption,
                "use-rest-site-option",
                choice.Label,
                $"sts2 act use-rest-site-option --option {restOptionId}",
                new ActionArgumentsSnapshot(
                    ownerPlayerId,
                    null,
                    null,
                    null,
                    null,
                    null,
                    IntentKind: "use-rest-site-option",
                    Values: new Dictionary<string, string> { ["restOptionId"] = restOptionId }));
        }

        if (string.Equals(choice.PreferredAction, "proceed-rest-site", StringComparison.Ordinal))
        {
            return IntentAction(
                "rest-site",
                choice,
                ownerPlayerId,
                SemanticActionKind.ProceedRestSite,
                "proceed-rest-site",
                choice.Label,
                "sts2 act proceed-rest-site",
                new ActionArgumentsSnapshot(ownerPlayerId, null, null, null, null, null, IntentKind: "proceed-rest-site"));
        }

        return IsFallbackChoice(choice) ? ChoiceAction("rest-site", choice, ownerPlayerId) : null;
    }

    // In-hand card selection mode (run.view.handSelection, e.g. Survivor's
    // "Discard 1 card."): replaces the normal combat action surface while the
    // hand stages a choice — the game's selection backstop blocks play-card and
    // end-turn input until the selection is confirmed.
    public static IReadOnlyList<AvailableActionSnapshot> HandSelectionActions(
        StateHandSelectionViewSnapshot handSelection)
    {
        var actions = new List<AvailableActionSnapshot>();
        var playerId = handSelection.PlayerId;
        foreach (var cardId in handSelection.SelectableCardIds)
        {
            if (handSelection.SelectedCardIds.Contains(cardId, StringComparer.Ordinal))
            {
                continue;
            }

            actions.Add(new AvailableActionSnapshot(
                Sts2ActionIds.Intent("combat-room", "select-hand-card", cardId),
                SemanticActionKind.SelectHandCard,
                $"Stage hand card {cardId} for: {handSelection.PromptText}",
                $"sts2 act select-hand-card --card {cardId}",
                Provisional: false,
                Arguments: new ActionArgumentsSnapshot(playerId, cardId, null, null, null, null, IntentKind: "select-hand-card"),
                IntentKind: "select-hand-card",
                OwnerPlayerId: playerId,
                Perspective: ResolvePerspective(playerId),
                RemoteOrchestration: OwnershipMetadata.LocalOnlyDegraded()));
        }

        if (!string.Equals(handSelection.Mode, "upgrade-select", StringComparison.Ordinal))
        {
            foreach (var cardId in handSelection.SelectedCardIds)
            {
                actions.Add(new AvailableActionSnapshot(
                    Sts2ActionIds.Intent("combat-room", "deselect-hand-card", cardId),
                    SemanticActionKind.DeselectHandCard,
                    $"Unstage hand card {cardId} from the pending selection.",
                    $"sts2 act deselect-hand-card --card {cardId}",
                    Provisional: false,
                    Arguments: new ActionArgumentsSnapshot(playerId, cardId, null, null, null, null, IntentKind: "deselect-hand-card"),
                    IntentKind: "deselect-hand-card",
                    OwnerPlayerId: playerId,
                    Perspective: ResolvePerspective(playerId),
                    RemoteOrchestration: OwnershipMetadata.LocalOnlyDegraded()));
            }
        }

        if (handSelection.CanConfirm)
        {
            actions.Add(new AvailableActionSnapshot(
                Sts2ActionIds.IntentWithoutChoice("combat-room", "confirm-hand-selection"),
                SemanticActionKind.ConfirmHandSelection,
                $"Confirm the staged selection ({handSelection.SelectedCardIds.Count}/{handSelection.MaxSelect}): {handSelection.PromptText}",
                "sts2 act confirm-hand-selection",
                Provisional: false,
                Arguments: new ActionArgumentsSnapshot(playerId, null, null, null, null, null, IntentKind: "confirm-hand-selection"),
                IntentKind: "confirm-hand-selection",
                OwnerPlayerId: playerId,
                Perspective: ResolvePerspective(playerId),
                RemoteOrchestration: OwnershipMetadata.LocalOnlyDegraded()));
        }

        return actions;
    }

    public static IReadOnlyList<AvailableActionSnapshot> CombatActions(
        string? activePlayerId,
        bool isPlayerTurn)
    {
        if (!CanEndTurn("combat", activePlayerId, isPlayerTurn))
        {
            return [];
        }

        return
        [
            new AvailableActionSnapshot(
                Sts2ActionIds.EndTurn(activePlayerId),
                SemanticActionKind.EndTurn,
                $"End turn for {activePlayerId}.",
                "sts2 act end-turn",
                Provisional: false,
                Arguments: new ActionArgumentsSnapshot(activePlayerId, null, null, null, null, null),
                OwnerPlayerId: activePlayerId,
                Perspective: ResolvePerspective(activePlayerId),
                RemoteOrchestration: OwnershipMetadata.LocalOnlyDegraded()),
        ];
    }

    public static IReadOnlyList<AvailableActionSnapshot> CombatActions(CombatStateSnapshot combat)
    {
        var actions = new List<AvailableActionSnapshot>();
        var activePlayerId = combat.ActivePlayerId;
        var remoteOrchestration = CombatOwnerRemoteOrchestration(combat, activePlayerId);

        if (!CanPlayCard("combat", activePlayerId, combat.IsPlayerTurn)
            || string.IsNullOrWhiteSpace(activePlayerId)
            || !combat.PlayersById.TryGetValue(activePlayerId, out var activePlayer))
        {
            if (CanEndTurn("combat", activePlayerId, combat.IsPlayerTurn))
            {
                actions.Add(new AvailableActionSnapshot(
                    Sts2ActionIds.EndTurn(activePlayerId),
                    SemanticActionKind.EndTurn,
                    $"End turn for {activePlayerId}.",
                    "sts2 act end-turn",
                    Provisional: false,
                    Arguments: new ActionArgumentsSnapshot(activePlayerId, null, null, null, null, null),
                    OwnerPlayerId: activePlayerId,
                    Perspective: ResolvePerspective(activePlayerId),
                    RemoteOrchestration: remoteOrchestration));
            }
            return actions;
        }

        var enemiesById = combat.Enemies.ToDictionary(enemy => enemy.Id, StringComparer.Ordinal);
        foreach (var card in activePlayer.Hand)
        {
            if (!card.Playable)
            {
                continue;
            }

            if (card.TargetIds.Count == 0)
            {
                actions.Add(new AvailableActionSnapshot(
                    Sts2ActionIds.PlayCard(activePlayerId, card.Id),
                    SemanticActionKind.PlayCard,
                    $"Play {card.Name}.",
                    $"sts2 act play-card --card {card.Id}",
                    Provisional: false,
                    Arguments: new ActionArgumentsSnapshot(activePlayerId, card.Id, null, null, null, null),
                    OwnerPlayerId: activePlayerId,
                    Perspective: ResolvePerspective(activePlayerId),
                    RemoteOrchestration: remoteOrchestration));
                continue;
            }

            foreach (var targetId in card.TargetIds.Distinct(StringComparer.Ordinal))
            {
                var targetName = enemiesById.GetValueOrDefault(targetId)?.Name ?? targetId;
                actions.Add(new AvailableActionSnapshot(
                    Sts2ActionIds.PlayCard(activePlayerId, card.Id, targetId),
                    SemanticActionKind.PlayCard,
                    $"Play {card.Name} targeting {targetName}.",
                    $"sts2 act play-card --card {card.Id} --target {targetId}",
                    Provisional: false,
                    Arguments: new ActionArgumentsSnapshot(activePlayerId, card.Id, targetId, null, null, null),
                    OwnerPlayerId: activePlayerId,
                    Perspective: ResolvePerspective(activePlayerId),
                    RemoteOrchestration: remoteOrchestration));
            }
        }

        foreach (var potion in activePlayer.Potions ?? [])
        {
            if (!CanUsePotion("combat", activePlayerId, combat.IsPlayerTurn, potion.OwnerPlayerId)
                || !potion.Usable)
            {
                continue;
            }

            if (potion.TargetIds.Count == 0)
            {
                actions.Add(new AvailableActionSnapshot(
                    Sts2ActionIds.UsePotion(activePlayerId, potion.Id),
                    SemanticActionKind.UsePotion,
                    $"Use {potion.Name}.",
                    $"sts2 act use-potion --potion {potion.Id}",
                    Provisional: false,
                    Arguments: new ActionArgumentsSnapshot(activePlayerId, null, null, null, null, null, potion.Id),
                    OwnerPlayerId: activePlayerId,
                    Perspective: ResolvePerspective(activePlayerId),
                    RemoteOrchestration: remoteOrchestration));
                continue;
            }

            foreach (var targetId in potion.TargetIds.Distinct(StringComparer.Ordinal))
            {
                var targetName = enemiesById.GetValueOrDefault(targetId)?.Name
                    ?? combat.PlayersById.GetValueOrDefault(targetId)?.Character
                    ?? targetId;
                actions.Add(new AvailableActionSnapshot(
                    Sts2ActionIds.UsePotion(activePlayerId, potion.Id, targetId),
                    SemanticActionKind.UsePotion,
                    $"Use {potion.Name} targeting {targetName}.",
                    $"sts2 act use-potion --potion {potion.Id} --target {targetId}",
                    Provisional: false,
                    Arguments: new ActionArgumentsSnapshot(activePlayerId, null, targetId, null, null, null, potion.Id),
                    OwnerPlayerId: activePlayerId,
                    Perspective: ResolvePerspective(activePlayerId),
                    RemoteOrchestration: remoteOrchestration));
            }
        }

        AddPotionUiActions(actions, combat, activePlayer, activePlayerId, remoteOrchestration);

        actions.Add(new AvailableActionSnapshot(
            Sts2ActionIds.EndTurn(activePlayerId),
            SemanticActionKind.EndTurn,
            $"End turn for {activePlayerId}.",
            "sts2 act end-turn",
            Provisional: false,
            Arguments: new ActionArgumentsSnapshot(activePlayerId, null, null, null, null, null),
            OwnerPlayerId: activePlayerId,
            Perspective: ResolvePerspective(activePlayerId),
            RemoteOrchestration: remoteOrchestration));

        return actions;
    }

    private static void AddPotionUiActions(
        ICollection<AvailableActionSnapshot> actions,
        CombatStateSnapshot combat,
        CombatPlayerStateSnapshot activePlayer,
        string activePlayerId,
        RemoteClientOrchestrationCapabilitySnapshot remoteOrchestration)
    {
        foreach (var potion in activePlayer.Potions ?? [])
        {
            if (!CanUsePotion("combat", activePlayerId, combat.IsPlayerTurn, potion.OwnerPlayerId)
                || !potion.Usable)
            {
                continue;
            }

            actions.Add(new AvailableActionSnapshot(
                Sts2ActionIds.Intent("combat", "open-potion-popup", potion.Id),
                SemanticActionKind.OpenPotionPopup,
                $"Open {potion.Name} potion popup.",
                $"sts2 act open-potion-popup --potion {potion.Id}",
                Provisional: false,
                Arguments: new ActionArgumentsSnapshot(
                    activePlayerId,
                    null,
                    null,
                    null,
                    null,
                    null,
                    PotionId: potion.Id,
                    IntentKind: "open-potion-popup",
                    Values: new Dictionary<string, string> { ["potionId"] = potion.Id }),
                IntentKind: "open-potion-popup",
                OwnerPlayerId: activePlayerId,
                Perspective: ResolvePerspective(activePlayerId),
                PreferredAction: "open-potion-popup",
                LegalityStatus: ActionLegalityKind.Legal,
                RemoteOrchestration: remoteOrchestration));
        }

        var selectedPotion = combat.SelectedPotion;
        if (selectedPotion is null)
        {
            return;
        }

        var activePotion = (activePlayer.Potions ?? [])
            .FirstOrDefault(potion => potion.SlotIndex == selectedPotion.SlotIndex);
        if (activePotion is null)
        {
            return;
        }

        var activePotionId = activePotion.Id;
        if (string.Equals(selectedPotion.Mode, "popup", StringComparison.Ordinal))
        {
            if (activePlayer.CanRemovePotions && activePotion.Usable)
            {
                var actionKind = activePotion.RequiresTarget
                    ? SemanticActionKind.StartPotionTargeting
                    : SemanticActionKind.UsePotion;
                var intentKind = activePotion.RequiresTarget ? "start-potion-targeting" : "use-potion";
                actions.Add(new AvailableActionSnapshot(
                    Sts2ActionIds.Intent("combat", intentKind, activePotionId),
                    actionKind,
                    activePotion.RequiresTarget ? "Start potion target selection." : "Use the active potion.",
                    activePotion.RequiresTarget
                        ? $"sts2 act start-potion-targeting --potion {activePotionId}"
                        : $"sts2 act use-potion --potion {activePotionId}",
                    Provisional: false,
                    Arguments: new ActionArgumentsSnapshot(
                        activePlayerId,
                        null,
                        null,
                        null,
                        null,
                        null,
                        PotionId: activePotionId,
                        IntentKind: intentKind,
                        Values: new Dictionary<string, string> { ["potionId"] = activePotionId }),
                    IntentKind: intentKind,
                    OwnerPlayerId: activePlayerId,
                    Perspective: ResolvePerspective(activePlayerId),
                    PreferredAction: intentKind,
                    LegalityStatus: ActionLegalityKind.Legal,
                    RemoteOrchestration: remoteOrchestration));
            }

            if (activePlayer.CanRemovePotions)
            {
                actions.Add(new AvailableActionSnapshot(
                    Sts2ActionIds.Intent("combat", "discard-potion", activePotionId),
                    SemanticActionKind.DiscardPotion,
                    "Discard the active potion.",
                    $"sts2 act discard-potion --potion {activePotionId}",
                    Provisional: false,
                    Arguments: new ActionArgumentsSnapshot(
                        activePlayerId,
                        null,
                        null,
                        null,
                        null,
                        null,
                        PotionId: activePotionId,
                        IntentKind: "discard-potion",
                        Values: new Dictionary<string, string> { ["potionId"] = activePotionId }),
                    IntentKind: "discard-potion",
                    OwnerPlayerId: activePlayerId,
                    Perspective: ResolvePerspective(activePlayerId),
                    PreferredAction: "discard-potion",
                    LegalityStatus: ActionLegalityKind.Legal,
                    RemoteOrchestration: remoteOrchestration));
            }

            return;
        }

        if (!string.Equals(selectedPotion.Mode, "targeting", StringComparison.Ordinal))
        {
            return;
        }

        foreach (var targetId in activePotion.TargetIds.Distinct(StringComparer.Ordinal))
        {
            var targetName = combat.Enemies.FirstOrDefault(enemy => string.Equals(enemy.Id, targetId, StringComparison.Ordinal))?.Name
                ?? combat.PlayersById.GetValueOrDefault(targetId)?.Character
                ?? targetId;
            actions.Add(new AvailableActionSnapshot(
                Sts2ActionIds.Intent("combat", "select-target", targetId),
                SemanticActionKind.SelectTarget,
                $"Select {targetName}.",
                $"sts2 act select-target --target {targetId}",
                Provisional: false,
                Arguments: new ActionArgumentsSnapshot(
                    activePlayerId,
                    null,
                    targetId,
                    null,
                    null,
                    null,
                    PotionId: activePotionId,
                    IntentKind: "select-target",
                    Values: new Dictionary<string, string>
                    {
                        ["potionId"] = activePotionId,
                        ["targetId"] = targetId,
                    }),
                IntentKind: "select-target",
                OwnerPlayerId: activePlayerId,
                Perspective: ResolvePerspective(activePlayerId),
                PreferredAction: "select-target",
                LegalityStatus: ActionLegalityKind.Legal,
                RemoteOrchestration: remoteOrchestration));
        }

        actions.Add(new AvailableActionSnapshot(
            Sts2ActionIds.IntentWithoutChoice("combat", "cancel-selection"),
            SemanticActionKind.CancelSelection,
            "Cancel active target selection.",
            "sts2 act cancel-selection",
            Provisional: false,
            Arguments: new ActionArgumentsSnapshot(
                activePlayerId,
                null,
                null,
                null,
                null,
                null,
                PotionId: activePotionId,
                IntentKind: "cancel-selection",
                Values: new Dictionary<string, string> { ["potionId"] = activePotionId }),
            IntentKind: "cancel-selection",
            OwnerPlayerId: activePlayerId,
            Perspective: ResolvePerspective(activePlayerId),
            PreferredAction: "cancel-selection",
            LegalityStatus: ActionLegalityKind.Legal,
            RemoteOrchestration: remoteOrchestration));
    }

    private static RemoteClientOrchestrationCapabilitySnapshot CombatOwnerRemoteOrchestration(CombatStateSnapshot combat, string? playerId)
        => !string.IsNullOrWhiteSpace(playerId)
           && combat.PlayersById.TryGetValue(playerId, out var player)
           && player.IsHostLocalSeat
            ? OwnershipMetadata.HostLocalSeat()
            : OwnershipMetadata.LocalOnlyDegraded();

    public static IReadOnlyList<AvailableActionSnapshot> MapActions(
        string? localPlayerId,
        IReadOnlyList<Sts2MapNodeSnapshot> nodes,
        bool isTravelEnabled = true,
        bool isTraveling = false,
        IReadOnlyList<ChoiceSnapshot>? flowChoices = null)
    {
        var actions = new List<AvailableActionSnapshot>();

        if (isTravelEnabled && !isTraveling)
        {
            actions.AddRange(nodes
                .Where(node => CanSelectMapNode(node, isTravelEnabled, isTraveling))
                .Select(node => new AvailableActionSnapshot(
                    Sts2ActionIds.Intent("map", "select-map-node", node.Id),
                    SemanticActionKind.SelectMapNode,
                    $"Select {node.Label}.",
                    $"sts2 act select-map-node --node {node.Id}",
                    Provisional: false,
                    Arguments: new ActionArgumentsSnapshot(localPlayerId, null, null, null, null, node.Id),
                    IntentKind: "select-map-node",
                    OwnerPlayerId: localPlayerId,
                    Perspective: ResolvePerspective(localPlayerId),
                    PreferredAction: "select-map-node",
                    LegalityStatus: ActionLegalityKind.Legal)));
        }

        foreach (var choice in flowChoices ?? [])
        {
            if (string.Equals(choice.PreferredAction, "back-from-map", StringComparison.Ordinal))
            {
                actions.Add(IntentAction(
                    "map",
                    choice,
                    choice.OwnerPlayerId ?? localPlayerId,
                    SemanticActionKind.BackFromMap,
                    "back-from-map",
                    choice.Label,
                    "sts2 act back-from-map",
                    new ActionArgumentsSnapshot(choice.OwnerPlayerId ?? localPlayerId, null, null, null, null, null, IntentKind: "back-from-map")));
            }
            else if (IsFallbackChoice(choice))
            {
                actions.Add(ChoiceAction("map", choice, choice.OwnerPlayerId ?? localPlayerId));
            }
        }

        return actions;
    }

    public static IReadOnlyList<AvailableActionSnapshot> LobbyActions(LobbyStateSnapshot lobby)
    {
        if (string.IsNullOrWhiteSpace(lobby.LocalPlayerId)
            || !lobby.PlayersById.TryGetValue(lobby.LocalPlayerId, out var localPlayer))
        {
            return [];
        }

        var actions = new List<AvailableActionSnapshot>();

        if (CanReady(Sts2SupportedScreenIds.StartRunLobbyScreenId, localPlayer))
        {
            actions.Add(new AvailableActionSnapshot(
                Sts2ActionIds.LobbyReady(),
                SemanticActionKind.Ready,
                "Mark the local lobby player ready.",
                "sts2 act ready",
                Provisional: false,
                Arguments: new ActionArgumentsSnapshot(localPlayer.Id, null, null, null, null, null),
                IntentKind: "ready",
                OwnerPlayerId: localPlayer.Id,
                Perspective: ResolvePerspective(localPlayer.Id),
                PreferredAction: "ready",
                LegalityStatus: ActionLegalityKind.Legal));
        }

        if (CanUnready(Sts2SupportedScreenIds.StartRunLobbyScreenId, localPlayer))
        {
            actions.Add(new AvailableActionSnapshot(
                Sts2ActionIds.LobbyUnready(),
                SemanticActionKind.Unready,
                "Mark the local lobby player unready.",
                "sts2 act unready",
                Provisional: false,
                Arguments: new ActionArgumentsSnapshot(localPlayer.Id, null, null, null, null, null),
                IntentKind: "unready",
                OwnerPlayerId: localPlayer.Id,
                Perspective: ResolvePerspective(localPlayer.Id),
                PreferredAction: "unready",
                LegalityStatus: ActionLegalityKind.Legal));
        }

        if (!localPlayer.IsReady)
        {
            foreach (var character in lobby.AvailableCharacters)
            {
                if (!CanSelectCharacter(Sts2SupportedScreenIds.StartRunLobbyScreenId, localPlayer, character))
                {
                    continue;
                }

                actions.Add(new AvailableActionSnapshot(
                    Sts2ActionIds.LobbySelectCharacter(character.Id),
                    SemanticActionKind.SelectCharacter,
                    $"Select {character.Name} for the local lobby player.",
                    $"sts2 act select-character --character {character.Id}",
                    Provisional: false,
                    Arguments: new ActionArgumentsSnapshot(localPlayer.Id, null, null, null, character.Id, null),
                    IntentKind: "select-character",
                    OwnerPlayerId: localPlayer.Id,
                    Perspective: ResolvePerspective(localPlayer.Id),
                    PreferredAction: "select-character",
                    LegalityStatus: ActionLegalityKind.Legal));
            }
        }

        return actions;
    }

    public static IReadOnlyList<ChoiceSnapshot> LobbyChoices(
        LobbyStateSnapshot lobby,
        bool includeCharacterChoices)
    {
        if (string.IsNullOrWhiteSpace(lobby.LocalPlayerId)
            || !lobby.PlayersById.TryGetValue(lobby.LocalPlayerId, out var localPlayer))
        {
            return [];
        }

        var choices = new List<ChoiceSnapshot>();
        if (CanReady(Sts2SupportedScreenIds.StartRunLobbyScreenId, localPlayer))
        {
            choices.Add(new ChoiceSnapshot(
                "lobby:ready",
                "Ready",
                "lobby-flow",
                Provisional: false,
                OwnerPlayerId: localPlayer.Id,
                ChoiceKind: "lobby-flow",
                IntentKind: "ready",
                Perspective: ResolvePerspective(localPlayer.Id),
                PreferredAction: "ready",
                Arguments: new ActionArgumentsSnapshot(localPlayer.Id, null, null, null, null, null, IntentKind: "ready"),
                Enabled: true,
                PreferredActionRef: new VisibleActionReferenceSnapshot(
                    Sts2ActionIds.LobbyReady(),
                    "Mark the local lobby player ready.",
                    Enabled: true,
                    OwnerPlayerId: localPlayer.Id,
                    ActionKind: SemanticActionKind.Ready,
                    IntentKind: "ready",
                    Arguments: new ActionArgumentsSnapshot(localPlayer.Id, null, null, null, null, null, IntentKind: "ready"),
                    LegalityStatus: ActionLegalityKind.Legal,
                    Perspective: ResolvePerspective(localPlayer.Id)),
                LegalityStatus: ActionLegalityKind.Legal));
        }

        if (CanUnready(Sts2SupportedScreenIds.StartRunLobbyScreenId, localPlayer))
        {
            choices.Add(new ChoiceSnapshot(
                "lobby:unready",
                "Unready",
                "lobby-flow",
                Provisional: false,
                OwnerPlayerId: localPlayer.Id,
                ChoiceKind: "lobby-flow",
                IntentKind: "unready",
                Perspective: ResolvePerspective(localPlayer.Id),
                PreferredAction: "unready",
                Arguments: new ActionArgumentsSnapshot(localPlayer.Id, null, null, null, null, null, IntentKind: "unready"),
                Enabled: true,
                PreferredActionRef: new VisibleActionReferenceSnapshot(
                    Sts2ActionIds.LobbyUnready(),
                    "Mark the local lobby player unready.",
                    Enabled: true,
                    OwnerPlayerId: localPlayer.Id,
                    ActionKind: SemanticActionKind.Unready,
                    IntentKind: "unready",
                    Arguments: new ActionArgumentsSnapshot(localPlayer.Id, null, null, null, null, null, IntentKind: "unready"),
                    LegalityStatus: ActionLegalityKind.Legal,
                    Perspective: ResolvePerspective(localPlayer.Id)),
                LegalityStatus: ActionLegalityKind.Legal));
        }

        if (includeCharacterChoices && !localPlayer.IsReady)
        {
            foreach (var character in lobby.AvailableCharacters)
            {
                choices.Add(new ChoiceSnapshot(
                    $"lobby:character:{character.Id}",
                    $"Select {character.Name}",
                    "lobby-character",
                    Provisional: false,
                    OwnerPlayerId: localPlayer.Id,
                    ChoiceKind: "lobby-character",
                    IntentKind: "select-character",
                    Perspective: ResolvePerspective(localPlayer.Id),
                    PreferredAction: "select-character",
                    Arguments: new ActionArgumentsSnapshot(localPlayer.Id, null, null, null, character.Id, null, IntentKind: "select-character"),
                    Enabled: character.IsUnlocked,
                    DisabledReason: character.IsUnlocked ? null : "character-locked",
                    PreferredActionRef: character.IsUnlocked
                        ? new VisibleActionReferenceSnapshot(
                            Sts2ActionIds.LobbySelectCharacter(character.Id),
                            $"Select {character.Name} for the local lobby player.",
                            Enabled: true,
                            OwnerPlayerId: localPlayer.Id,
                            ActionKind: SemanticActionKind.SelectCharacter,
                            IntentKind: "select-character",
                            Arguments: new ActionArgumentsSnapshot(localPlayer.Id, null, null, null, character.Id, null, IntentKind: "select-character"),
                            LegalityStatus: ActionLegalityKind.Legal,
                            Perspective: ResolvePerspective(localPlayer.Id))
                        : null,
                    LegalityStatus: character.IsUnlocked ? ActionLegalityKind.Legal : ActionLegalityKind.Illegal));
            }
        }

        return choices;
    }

    private static AvailableActionSnapshot ChoiceAction(
        string actionScope,
        ChoiceSnapshot choice,
        string? ownerPlayerId)
    {
        return new AvailableActionSnapshot(
            Sts2ActionIds.Choice(actionScope, choice.Id),
            SemanticActionKind.Choose,
            $"Choose {choice.Label}.",
            $"sts2 act choose --choice {choice.Id}",
            Provisional: choice.Provisional,
            Arguments: choice.Arguments ?? new ActionArgumentsSnapshot(
                ownerPlayerId,
                null,
                null,
                choice.Id,
                null,
                null,
                IntentKind: choice.IntentKind ?? ResolveChoiceIntentKind(choice)),
            IntentKind: choice.IntentKind ?? ResolveChoiceIntentKind(choice),
            OwnerPlayerId: ownerPlayerId,
            Perspective: choice.Perspective ?? ResolvePerspective(ownerPlayerId),
            PreferredAction: choice.PreferredAction ?? "choose",
            LegalityStatus: choice.LegalityStatus == ActionLegalityKind.Unspecified
                ? ActionLegalityKind.Legal
                : choice.LegalityStatus,
            DisabledReason: choice.DisabledReason,
            CheckedHookPaths: choice.CheckedHookPaths,
            PreferredActionRef: choice.PreferredActionRef);
    }

    private static bool IsFallbackChoice(ChoiceSnapshot choice)
        => string.IsNullOrWhiteSpace(choice.PreferredAction)
           || string.Equals(choice.PreferredAction, "choose", StringComparison.Ordinal);

    private static void AddOverlayAffordanceAction(
        ICollection<AvailableActionSnapshot> actions,
        OverlayAffordanceSnapshot? affordance)
    {
        if (affordance is null || !affordance.Enabled)
        {
            return;
        }

        actions.Add(new AvailableActionSnapshot(
            Sts2ActionIds.Choice("card-overlay", affordance.Id),
            SemanticActionKind.Choose,
            $"Choose {affordance.Label}.",
            $"sts2 act choose --choice {affordance.Id}",
            Provisional: affordance.Provisional,
            Arguments: new ActionArgumentsSnapshot(
                affordance.OwnerPlayerId,
                null,
                null,
                affordance.Id,
                null,
                null),
            IntentKind: affordance.IntentKind,
            OwnerPlayerId: affordance.OwnerPlayerId,
            Perspective: affordance.Perspective,
            PreferredAction: affordance.PreferredAction ?? "choose"));
    }

    private static string ResolveChoiceIntentKind(ChoiceSnapshot choice)
        => choice.Kind switch
        {
            "card-overlay-control" => "choose-overlay-control",
            "card-overlay-flow" => choice.Id switch
            {
                "card-overlay:back" => "back-overlay",
                "card-overlay:close" => "close-overlay",
                _ => "choose-overlay-flow",
            },
            "card-selection-bundle" => "choose-bundle",
            "card-selection-card" => "choose-card",
            "card-selection-skip" => "skip-card-selection",
            "card-selection-alternative" => "choose-card-selection-alternative",
            "crystal-sphere-cell" => "choose-crystal-sphere-cell",
            "crystal-sphere-flow" => "proceed",
            "crystal-sphere-tool" => "choose-crystal-sphere-tool",
            "event-option" => "choose-event-option",
            "event-shop-entry" => "open-shop",
            "map-flow" => "choose-map-flow",
            "reward" => "claim-reward",
            "reward-flow" => "skip-or-proceed",
            "rest-site-flow" => "proceed",
            "rest-site-option" => "choose-rest-site-option",
            "shop-card" => "buy-card",
            "shop-card-removal" => "remove-card",
            "shop-flow" => "leave-shop",
            "shop-potion" => "buy-potion",
            "shop-relic" => "buy-relic",
            "treasure-room-flow" => "choose-treasure-room-flow",
            "treasure-room-relic" => "take-relic",
            _ => choice.ChoiceKind ?? "choose",
        };

    private static string? ResolvePerspective(string? ownerPlayerId)
        => OwnershipMetadata.ResolvePerspective(ownerPlayerId);

    public static bool IsExecutableChoiceId(string screenType, string? choiceId)
    {
        return screenType == "main-menu"
            && string.Equals(choiceId, MainMenuStartRunChoiceId, StringComparison.Ordinal);
    }

    public static bool CanEndTurn(
        string screenType,
        string? activePlayerId,
        bool isPlayerTurn,
        string? requestedPlayerId = null,
        bool activeOwnerIsHostLocalSeat = false)
    {
        if (screenType != "combat" || !isPlayerTurn || string.IsNullOrWhiteSpace(activePlayerId))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(requestedPlayerId)
            || string.Equals(requestedPlayerId, activePlayerId, StringComparison.Ordinal)
            || (activeOwnerIsHostLocalSeat && string.Equals(requestedPlayerId, activePlayerId, StringComparison.Ordinal));
    }

    // The inverse of CanEndTurn: a player can cancel only once they have ended
    // (are ready to end turn) and are still waiting on living allies. Unlike
    // end-turn this does NOT require IsPlayPhase, because once a seat is ready the
    // host-local combat state can report a non-play phase while allies finish.
    public static bool CanCancelEndTurn(
        string screenType,
        string? activePlayerId,
        bool playerHasEndedTurn,
        string? requestedPlayerId = null,
        bool activeOwnerIsHostLocalSeat = false)
    {
        if (screenType != "combat" || !playerHasEndedTurn || string.IsNullOrWhiteSpace(activePlayerId))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(requestedPlayerId)
            || string.Equals(requestedPlayerId, activePlayerId, StringComparison.Ordinal)
            || (activeOwnerIsHostLocalSeat && string.Equals(requestedPlayerId, activePlayerId, StringComparison.Ordinal));
    }

    public static bool CanPlayCard(
        string screenType,
        string? activePlayerId,
        bool isPlayerTurn,
        string? requestedPlayerId = null,
        bool activeOwnerIsHostLocalSeat = false)
    {
        return CanEndTurn(screenType, activePlayerId, isPlayerTurn, requestedPlayerId, activeOwnerIsHostLocalSeat);
    }

    public static bool CanUsePotion(
        string screenType,
        string? activePlayerId,
        bool isPlayerTurn,
        string? potionOwnerPlayerId,
        string? requestedPlayerId = null,
        bool activeOwnerIsHostLocalSeat = false)
    {
        return screenType == "combat"
            && isPlayerTurn
            && !string.IsNullOrWhiteSpace(activePlayerId)
            && string.Equals(activePlayerId, potionOwnerPlayerId, StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(requestedPlayerId)
                || string.Equals(requestedPlayerId, activePlayerId, StringComparison.Ordinal)
                || (activeOwnerIsHostLocalSeat && string.Equals(requestedPlayerId, activePlayerId, StringComparison.Ordinal)));
    }

    public static bool CanReady(string screenType, LobbyPlayerSnapshot? localPlayer)
    {
        return Sts2SupportedScreenIds.IsLobbyScreenType(screenType)
            && localPlayer is not null
            && !localPlayer.IsReady
            && !string.IsNullOrWhiteSpace(localPlayer.SelectedCharacterId);
    }

    public static bool CanUnready(string screenType, LobbyPlayerSnapshot? localPlayer)
    {
        return Sts2SupportedScreenIds.IsLobbyScreenType(screenType)
            && localPlayer is not null
            && localPlayer.IsReady;
    }

    public static bool CanSelectCharacter(
        string screenType,
        LobbyPlayerSnapshot? localPlayer,
        LobbyCharacterSnapshot? character)
    {
        return Sts2SupportedScreenIds.IsLobbyScreenType(screenType)
            && localPlayer is not null
            && character is not null
            && !localPlayer.IsReady
            && character.IsUnlocked
            && !string.Equals(localPlayer.SelectedCharacterId, character.Id, StringComparison.Ordinal);
    }

    public static bool CanSelectMapNode(
        Sts2MapNodeSnapshot? node,
        bool isTravelEnabled,
        bool isTraveling)
    {
        return node is not null
            && node.Travelable
            && isTravelEnabled
            && !isTraveling;
    }
}
