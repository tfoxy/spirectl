using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Live;

public sealed partial class Sts2ActionHandler
{
    private ActionExecutionResult ExecuteOnMainThread(SemanticActionRequest request)
    {
        try
        {
            return request.Kind switch
            {
                SemanticActionKind.PlayCard => ExecutePlayCard(request),
                SemanticActionKind.UsePotion => ExecuteUsePotion(request),
                SemanticActionKind.OpenPotionPopup => ExecuteOpenPotionPopup(request),
                SemanticActionKind.StartPotionTargeting => ExecuteStartPotionTargeting(request),
                SemanticActionKind.SelectTarget => ExecuteSelectTarget(request),
                SemanticActionKind.DiscardPotion => ExecuteDiscardPotion(request),
                SemanticActionKind.Choose => ExecuteChoose(request),
                SemanticActionKind.ConfirmSelection => ExecuteConfirmSelection(request),
                SemanticActionKind.CancelSelection => ExecuteCancelSelection(request),
                SemanticActionKind.SelectMapNode => ExecuteSelectMapNode(request),
                SemanticActionKind.DrawMapStroke => ExecuteDrawMapStroke(request),
                SemanticActionKind.ClearMapDrawings => ExecuteClearMapDrawings(request),
                SemanticActionKind.EndTurn => ExecuteEndTurn(request),
                SemanticActionKind.CancelEndTurn => ExecuteCancelEndTurn(request),
                SemanticActionKind.Ready => ExecuteReady(request),
                SemanticActionKind.Unready => ExecuteUnready(request),
                SemanticActionKind.SelectCharacter => ExecuteSelectCharacter(request),
                SemanticActionKind.JoinLobbyPlayer => ExecuteJoinLobbyPlayer(request),
                SemanticActionKind.LeaveLobbyPlayer => ExecuteLeaveLobbyPlayer(request),
                SemanticActionKind.DisconnectClient => ExecuteDisconnectClient(request),
                SemanticActionKind.SetClientName => ExecuteSetClientName(request),
                SemanticActionKind.ClaimReward => ExecuteClaimReward(request),
                SemanticActionKind.SkipRewards => ExecuteSkipRewards(request),
                SemanticActionKind.SelectCard => ExecuteSelectCard(request),
                SemanticActionKind.SkipCardSelection => ExecuteSkipCardSelection(request),
                SemanticActionKind.SelectBundle => ExecuteSelectBundle(request),
                SemanticActionKind.BuyCard => ExecuteBuyCard(request),
                SemanticActionKind.BuyRelic => ExecuteBuyRelic(request),
                SemanticActionKind.BuyPotion => ExecuteBuyPotion(request),
                SemanticActionKind.RemoveCard => ExecuteRemoveCard(request),
                SemanticActionKind.LeaveShop => ExecuteLeaveShop(request),
                SemanticActionKind.OpenShop => ExecuteOpenShop(request),
                SemanticActionKind.ProceedMerchantRoom => ExecuteProceedMerchantRoom(request),
                SemanticActionKind.CloseShopInventory => ExecuteCloseShopInventory(request),
                SemanticActionKind.Rest => ExecuteRest(request),
                SemanticActionKind.Smith => ExecuteSmith(request),
                SemanticActionKind.UseRestSiteOption => ExecuteUseRestSiteOption(request),
                SemanticActionKind.ProceedRestSite => ExecuteProceedRestSite(request),
                SemanticActionKind.OpenChest => ExecuteOpenChest(request),
                SemanticActionKind.TakeRelic => ExecuteTakeRelic(request),
                SemanticActionKind.ProceedTreasureRoom => ExecuteProceedTreasureRoom(request),
                SemanticActionKind.BackFromMap => ExecuteBackFromMap(request),
                SemanticActionKind.SelectEventOption => ExecuteSelectEventOption(request),
                SemanticActionKind.OpenEventShop => ExecuteOpenEventShop(request),
                SemanticActionKind.UseCrystalSphereControl => ExecuteUseCrystalSphereControl(request),
                SemanticActionKind.ProceedEvent => ExecuteProceedEvent(request),
                SemanticActionKind.ToggleMap => ExecuteToggleMap(request),
                SemanticActionKind.ToggleDeck => ExecuteToggleDeck(request),
                SemanticActionKind.ToggleSettings => ExecuteToggleSettings(request),
                SemanticActionKind.SortDeckView => ExecuteSortDeckView(request),
                SemanticActionKind.ToggleDeckViewUpgrades => ExecuteToggleDeckViewUpgrades(request),
                SemanticActionKind.ViewDrawPile => ExecuteViewDrawPile(request),
                SemanticActionKind.ViewDiscardPile => ExecuteViewDiscardPile(request),
                SemanticActionKind.ViewExhaustPile => ExecuteViewExhaustPile(request),
                SemanticActionKind.InspectRelic => ExecuteInspectRelic(request),
                SemanticActionKind.CloseInspectRelic => ExecuteCloseInspectRelic(request),
                SemanticActionKind.SelectHandCard => ExecuteSelectHandCard(request),
                SemanticActionKind.DeselectHandCard => ExecuteDeselectHandCard(request),
                SemanticActionKind.ConfirmHandSelection => ExecuteConfirmHandSelection(request),
                SemanticActionKind.MouseClick => ExecuteMouseClick(request),
                SemanticActionKind.HoverElement => ExecuteHoverElement(request),
                SemanticActionKind.KeyInput => ExecuteKeyInput(request),
                SemanticActionKind.ControllerInput => ExecuteControllerInput(request),
                SemanticActionKind.SetScrollOffset => ExecuteSetScrollOffset(request),
                _ => ActionExecutionResult.Failure(
                    kind: request.Kind,
                    code: ActionFailureCode.InvalidAction,
                    message: $"Unsupported semantic action kind '{request.Kind}'.")
            };
        }
        catch (Exception ex)
        {
            _logStream.Write(
                BridgeLogLevel.Error,
                "bridge.action",
                $"Action '{request.Kind}' failed with an unhandled exception: {ex}");
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.RuntimeFailure,
                message: $"Action '{request.Kind}' failed with an unhandled runtime exception.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "exception",
                        Value: ex.GetType().Name,
                        Note: ex.Message),
                ]);
        }
    }
}
