using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2;

/// <summary>Shared semantic-action descriptor vocabulary for embedded discovery and bridge handshake.</summary>
internal static class Sts2ActionDescriptorCatalog
{
    internal static IReadOnlyList<ActionDescriptorSnapshot> Build(bool playCardImplemented, bool dangerousMode)
    {
        var actions = new List<ActionDescriptorSnapshot>
        {
            A("play-card", SemanticActionKind.PlayCard, "Play a card against an optional target.", "sts2 act play-card --card c_1 --target e_1", !playCardImplemented, playCardImplemented ? ActionImplementationStatus.Implemented : ActionImplementationStatus.Scaffolded, P("cardId", true, "Stable card id from state.combat.hand[].id."), P("targetId", false, "Optional stable target id when the card requires a target.")),
            A("choose", SemanticActionKind.Choose, "Select a visible choice by stable choice id.", "sts2 act choose --choice reward:p1:0", false, ActionImplementationStatus.Implemented, P("choiceId", true, "Stable choice id from state.choices[].id.")),
            A("confirm-selection", SemanticActionKind.ConfirmSelection, "Confirm the currently staged selection on a supported selection overlay.", "sts2 act confirm-selection", false, ActionImplementationStatus.Implemented),
            A("cancel-selection", SemanticActionKind.CancelSelection, "Cancel the currently staged selection on a supported selection overlay.", "sts2 act cancel-selection", false, ActionImplementationStatus.Implemented),
            A("select-map-node", SemanticActionKind.SelectMapNode, "Select an executable map node by stable id.", "sts2 act select-map-node --node map-node:3:1", false, ActionImplementationStatus.Implemented, P("mapNodeId", true, "Stable map node id from state.choices[].id.")),
            A("end-turn", SemanticActionKind.EndTurn, "End the current turn for the active player.", "sts2 act end-turn", false, ActionImplementationStatus.Implemented),
            A("select-character", SemanticActionKind.SelectCharacter, "Select an executable multiplayer lobby character by stable id.", "sts2 act select-character --character silent", false, ActionImplementationStatus.Implemented, P("characterId", true, "Stable character id from state.lobby.availableCharacters[].id.")),
            A("ready", SemanticActionKind.Ready, "Mark the local multiplayer lobby player ready when the current screen allows it.", "sts2 act ready", false, ActionImplementationStatus.Implemented),
            A("unready", SemanticActionKind.Unready, "Clear the local multiplayer lobby ready state when the current screen allows it.", "sts2 act unready", false, ActionImplementationStatus.Implemented),
            A("join-lobby-player", SemanticActionKind.JoinLobbyPlayer, "Create or reuse a named host-local multiplayer lobby seat.", "sts2 act join-lobby-player --display-name Alice", false, ActionImplementationStatus.Implemented, P("displayName", true, "Exact display name for the synthetic host-local lobby seat.")),
            A("leave-lobby-player", SemanticActionKind.LeaveLobbyPlayer, "Remove a synthetic host-local multiplayer lobby seat by player id.", "sts2 act leave-lobby-player --player-id p:2", false, ActionImplementationStatus.Implemented, P("playerId", true, "Synthetic host-local player id from state.lobby.players[].id.")),
        };
        actions.AddRange(NonCombat());
        if (dangerousMode)
        {
            actions.Add(A("mouse-click", SemanticActionKind.MouseClick, "Dispatch a dangerous raw viewport mouse click at explicit coordinates.", "sts2 --mode dangerous act mouse click --x 100 --y 200", true, ActionImplementationStatus.Implemented, P("x", true, "Viewport x coordinate, matching screenshot pixel space.", "integer"), P("y", true, "Viewport y coordinate, matching screenshot pixel space.", "integer"), P("button", false, "Optional mouse button: left, right, or middle. Defaults to left.")));
        }
        return actions;
    }

    private static IReadOnlyList<ActionDescriptorSnapshot> NonCombat()
        =>
        [
            S("claim-reward", SemanticActionKind.ClaimReward, "Claim a visible reward by stable reward id.", "sts2 act claim-reward --reward reward:p1:0", P("rewardId", true, "Stable reward id from state.rewards.rewards[].id."), Player()),
            S("skip-rewards", SemanticActionKind.SkipRewards, "Skip or proceed past the visible rewards screen.", "sts2 act skip-rewards", Player()),
            S("select-card", SemanticActionKind.SelectCard, "Select a visible card by stable card id.", "sts2 act select-card --card c_1", P("cardId", true, "Stable card id from the visible card selection state."), Player()),
            S("skip-card-selection", SemanticActionKind.SkipCardSelection, "Skip the current visible card-selection flow.", "sts2 act skip-card-selection", Player()),
            S("select-bundle", SemanticActionKind.SelectBundle, "Select a visible bundle by stable bundle id.", "sts2 act select-bundle --bundle bundle:0", P("bundleId", true, "Stable bundle id from state.bundleSelection.bundles[].id."), Player()),
            S("buy-card", SemanticActionKind.BuyCard, "Buy a visible shop card item.", "sts2 act buy-card --shop-item shop:item:0", P("shopItemId", true, "Stable shop item id from state.shop.purchasableItems[].id."), P("cardId", false, "Optional visible card id."), Player()),
            S("buy-relic", SemanticActionKind.BuyRelic, "Buy a visible shop relic item.", "sts2 act buy-relic --shop-item shop:item:1", P("shopItemId", true, "Stable shop item id from state.shop.purchasableItems[].id."), P("relicId", false, "Optional visible relic id."), Player()),
            S("buy-potion", SemanticActionKind.BuyPotion, "Buy a visible shop potion item.", "sts2 act buy-potion --shop-item shop:item:2", P("shopItemId", true, "Stable shop item id from state.shop.purchasableItems[].id."), P("potionId", false, "Optional visible potion id."), Player()),
            S("remove-card", SemanticActionKind.RemoveCard, "Trigger the visible shop card-removal flow (the card to remove is then chosen via the deck-card-selection overlay).", "sts2 act remove-card --shop-item shop:card-removal", P("shopItemId", true, "Card-removal shop item id from state.run.currentRoom.shop.inventory.cardRemovalEntry.id (shop:card-removal)."), P("cardId", false, "Unused; the card to remove is chosen via the deck-card-selection overlay opened by this action."), Player()),
            S("leave-shop", SemanticActionKind.LeaveShop, "Leave the current visible shop.", "sts2 act leave-shop", Player()),
            S("open-shop", SemanticActionKind.OpenShop, "Open the merchant inventory in the current shop room (the human-equivalent of clicking the merchant).", "sts2 act open-shop", Player()),
            S("proceed-merchant-room", SemanticActionKind.ProceedMerchantRoom, "Leave the current merchant room and open the travel map (the human-equivalent of the merchant room's Proceed button).", "sts2 act proceed-merchant-room", Player()),
            S("close-shop-inventory", SemanticActionKind.CloseShopInventory, "Close the current shop inventory overlay.", "sts2 act close-shop-inventory", Player()),
            S("rest", SemanticActionKind.Rest, "Use the visible rest option.", "sts2 act rest", Player()),
            S("smith", SemanticActionKind.Smith, "Smith or upgrade a visible card by stable id.", "sts2 act smith --card c_1", P("cardId", true, "Stable card id selected for smithing."), Player()),
            S("use-rest-site-option", SemanticActionKind.UseRestSiteOption, "Use a visible rest-site option by stable id.", "sts2 act use-rest-site-option --option rest:option", P("restOptionId", true, "Stable rest option id from state.restSite.controls[].id."), Player()),
            S("proceed-rest-site", SemanticActionKind.ProceedRestSite, "Proceed from the current rest-site room.", "sts2 act proceed-rest-site", Player()),
            S("open-chest", SemanticActionKind.OpenChest, "Open the visible treasure chest.", "sts2 act open-chest", Player()),
            S("take-relic", SemanticActionKind.TakeRelic, "Take a visible relic by stable id.", "sts2 act take-relic --relic relic:0", P("relicId", true, "Stable relic id from the visible treasure or relic-selection state."), Player()),
            S("proceed-treasure-room", SemanticActionKind.ProceedTreasureRoom, "Proceed from the current treasure room.", "sts2 act proceed-treasure-room", Player()),
            S("back-from-map", SemanticActionKind.BackFromMap, "Go back from the current map screen.", "sts2 act back-from-map", Player()),
            S("select-event-option", SemanticActionKind.SelectEventOption, "Select a visible event option by stable id.", "sts2 act select-event-option --event-option event:option:0", P("eventOptionId", true, "Stable event option id from state.eventRoom.options[].id."), Player()),
            S("open-event-shop", SemanticActionKind.OpenEventShop, "Open a visible event shop entry.", "sts2 act open-event-shop --event-option event:shop", P("eventOptionId", true, "Stable event shop option id."), Player()),
            S("use-crystal-sphere-control", SemanticActionKind.UseCrystalSphereControl, "Use a visible Crystal Sphere control by stable id.", "sts2 act use-crystal-sphere-control --control crystal-sphere:small-divination", P("controlId", true, "Stable Crystal Sphere control id."), Player()),
            S("proceed-event", SemanticActionKind.ProceedEvent, "Proceed from the current event room.", "sts2 act proceed-event", Player()),
            I("open-potion-popup", SemanticActionKind.OpenPotionPopup, "Open a visible top-bar potion popup.", "sts2 act open-potion-popup --potion potion:p:1:0:fire-potion", P("potionId", true, "Stable potion id or slot index from state.run.players[].potions[].")),
            I("start-potion-targeting", SemanticActionKind.StartPotionTargeting, "Enter target selection for a visible potion.", "sts2 act start-potion-targeting --potion potion:p:1:0:fire-potion", P("potionId", true, "Stable potion id or slot index from state.run.players[].potions[].")),
            I("select-target", SemanticActionKind.SelectTarget, "Select a visible target while target selection is active.", "sts2 act select-target --target creature:1", P("targetId", true, "Stable target id derived from state.run.view.selectedPotion, combat creatures, and availableActions.")),
            I("discard-potion", SemanticActionKind.DiscardPotion, "Discard a visible top-bar potion through its popup.", "sts2 act discard-potion --potion potion:p:1:0:fire-potion", P("potionId", true, "Stable potion id or slot index from state.run.players[].potions[].")),
            I("toggle-map", SemanticActionKind.ToggleMap, "Toggle the run top-bar map.", "sts2 act toggle-map", Player()),
            I("toggle-deck", SemanticActionKind.ToggleDeck, "Toggle the run top-bar deck view.", "sts2 act toggle-deck", Player()),
            I("toggle-settings", SemanticActionKind.ToggleSettings, "Toggle the run top-bar settings menu.", "sts2 act toggle-settings", Player()),
            I("sort-deck-view", SemanticActionKind.SortDeckView, "Sort the current deck view.", "sts2 act sort-deck-view --by type", P("by", true, "Deck view sort key: obtained, type, cost, or alphabet."), Player()),
            I("toggle-deck-view-upgrades", SemanticActionKind.ToggleDeckViewUpgrades, "Toggle deck view upgrade previews.", "sts2 act toggle-deck-view-upgrades", Player()),
            I("view-draw-pile", SemanticActionKind.ViewDrawPile, "Open the combat draw pile viewer.", "sts2 act view-draw-pile", Player()),
            I("view-discard-pile", SemanticActionKind.ViewDiscardPile, "Open the combat discard pile viewer.", "sts2 act view-discard-pile", Player()),
            I("view-exhaust-pile", SemanticActionKind.ViewExhaustPile, "Open the combat exhaust pile viewer.", "sts2 act view-exhaust-pile", Player()),
            I("inspect-relic", SemanticActionKind.InspectRelic, "Open the relic-details overlay for a relic-bar relic.", "sts2 act inspect-relic --relic relic:p:1:0:BURNING_BLOOD", P("relicId", true, "Stable relic id (or model id) from state.run.players[].relics[]."), Player()),
            I("close-inspect-relic", SemanticActionKind.CloseInspectRelic, "Close the open relic-details overlay.", "sts2 act close-inspect-relic", Player()),
            I("select-hand-card", SemanticActionKind.SelectHandCard, "Stage a hand card while in-hand selection mode is active (state.run.view.handSelection).", "sts2 act select-hand-card --card 12", P("cardId", true, "Stable card id from state.run.view.handSelection.selectableCardIds."), Player()),
            I("deselect-hand-card", SemanticActionKind.DeselectHandCard, "Unstage a previously selected hand card from the active in-hand selection.", "sts2 act deselect-hand-card --card 12", P("cardId", true, "Stable card id from state.run.view.handSelection.selectedCardIds."), Player()),
            I("confirm-hand-selection", SemanticActionKind.ConfirmHandSelection, "Confirm the staged in-hand selection; resolves the awaiting card effect. Pass cardId(s) to stage the selection and confirm in one call (for clients that keep selection local).", "sts2 act confirm-hand-selection --card 12", P("cardId", false, "Stable card id(s) from state.run.view.handSelection.selectableCardIds to stage before confirming; repeatable. Omit to confirm whatever is already staged."), Player()),
        ];

    private static ActionDescriptorSnapshot S(string id, SemanticActionKind kind, string summary, string hint, params ActionParameterDescriptorSnapshot[] parameters) => A(id, kind, summary, hint, true, ActionImplementationStatus.Scaffolded, parameters);
    private static ActionDescriptorSnapshot I(string id, SemanticActionKind kind, string summary, string hint, params ActionParameterDescriptorSnapshot[] parameters) => A(id, kind, summary, hint, false, ActionImplementationStatus.Implemented, parameters);
    private static ActionDescriptorSnapshot A(string id, SemanticActionKind kind, string summary, string hint, bool provisional, ActionImplementationStatus status, params ActionParameterDescriptorSnapshot[] parameters) => new(id, kind, summary, hint, provisional, status, parameters);
    private static ActionParameterDescriptorSnapshot Player() => P("playerId", false, "Owning player id when explicit ownership is needed.");
    private static ActionParameterDescriptorSnapshot P(string name, bool required, string summary, string type = "string") => new(name, type, required, summary);
}
